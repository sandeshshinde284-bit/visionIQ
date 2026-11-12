"""BRF-03 / BRF-04: the brief pipeline end to end, with in-memory sources and scripted models."""

import json

import pytest
import structlog
from brief_support import (
    DUPLICATE,
    GOOD_DRAFT,
    PRINTED,
    FakeSources,
    ScriptedProvider,
    advice,
    bundle,
    gateway_for,
    key,
    offline_gateway,
    request,
    result,
)
from jsonschema import Draft202012Validator

from app.brief import schema
from app.brief.history import HistoryFacts
from app.brief.pipeline import (
    NO_HISTORY_FOUND,
    NO_SUPPORTED_ADVICE,
    TICKET_NOT_READABLE,
    TICKET_TOO_VAGUE,
    WEAK_EVIDENCE,
    BriefSettings,
    build_brief,
)
from app.knowledge.retrieval import Lesson
from app.llm.types import DataClassification, TaskKind

SCHEMA = Draft202012Validator(schema.json_schema())
REASON = r"^[a-z][a-z0-9_]{1,79}$"  # analysis.completed v1


def build(sources=None, gateway=None, settings=None):
    outcome = build_brief(
        request(),
        sources or FakeSources(),
        gateway or gateway_for(ScriptedProvider("gemini", [GOOD_DRAFT])),
        settings or BriefSettings(),
    )
    document = json.loads(outcome.brief.model_dump_json())
    assert list(SCHEMA.iter_errors(document)) == []  # every brief matches the published schema
    return outcome


def test_a_defect_gets_risk_areas_do_dont_and_an_approach_each_with_a_source() -> None:
    brief = build().brief

    assert (brief.state, brief.reason_codes) == ("PUBLISHED", [])
    assert [(a.advice_id, a.kind, a.evidence_ids) for a in brief.advice] == [
        ("ADV-001", "APPROACH", ["E1"]),
        ("ADV-002", "DONT", ["E2"]),
    ]
    evidence = {item.id: item for item in brief.evidence}
    for item in brief.advice:  # every point resolves to a real source link
        for evidence_id in item.evidence_ids:
            assert evidence[evidence_id].url.startswith("https://dev.azure.com/contoso/")
    assert evidence["E2"].notes == ["A fix linked to this ticket was later reverted."]
    assert [(s.path, s.basis) for s in brief.predicted_scope] == [
        ("RetryPolicy.cs", "named_in_ticket"),
        ("/src/Kiosk/ReceiptPrinter.cs", "changed_by_past_fix"),
    ]
    assert brief.risk.band == "MODERATE"  # 2 fixes, 1 reverted
    assert brief.ticket.title == "Bug 4711: Barcode not printed after payment retry"
    assert brief.snapshot_id == "snap-test"


def test_the_model_sees_bounded_evidence_and_code_computed_facts_only() -> None:
    gemini = ScriptedProvider("gemini", [GOOD_DRAFT])
    sources = FakeSources()

    build(sources, gateway_for(gemini))

    prompt = gemini.tasks[0].user_prompt
    assert prompt.count("<untrusted_data source=") == 3  # the ticket and two evidence items
    assert "Risk band: MODERATE" in prompt
    assert "[note] A fix linked to this ticket was later reverted." in prompt
    # The ticket itself is excluded from its own history.
    assert sources.searches[0][1] == (key("4711"),)


def test_the_brief_records_which_model_prompt_and_routing_produced_it() -> None:
    outcome = build(gateway=gateway_for(ScriptedProvider("gemini", [GOOD_DRAFT], model_id="g-1")))

    generation = outcome.brief.generation
    assert (generation.provider, generation.model_id) == ("gemini", "g-1")
    assert generation.prompt_template_version == "brief.defect@v1"
    assert generation.routing_revision.startswith("routing-")
    assert (generation.calls, generation.input_tokens, generation.output_tokens) == (1, 900, 200)
    assert outcome.ledger.calls[0].task is TaskKind.BRIEF_DRAFT


# ---- S10-10: missing or inconclusive evidence ------------------------------------------------


def test_an_unreadable_ticket_is_blocked_with_no_model_call() -> None:
    gemini = ScriptedProvider("gemini", [GOOD_DRAFT])

    brief = build(FakeSources(ticket=None), gateway_for(gemini)).brief

    assert (brief.state, brief.reason_codes) == ("BLOCKED", [TICKET_NOT_READABLE])
    assert brief.risk.band == "INSUFFICIENT_DATA" and brief.advice == [] and gemini.tasks == []


def test_a_vague_ticket_is_partial_and_asks_for_detail_instead_of_guessing() -> None:
    gemini = ScriptedProvider("gemini", [GOOD_DRAFT])
    vague = bundle(
        [],
        status="insufficient_query",
        reasons=["The query has too few specific terms to match history."],
        suggestions=["Add the exact error message or stack trace"],
    )

    brief = build(FakeSources(evidence=vague), gateway_for(gemini)).brief

    assert (brief.state, brief.reason_codes) == ("PARTIAL", [TICKET_TOO_VAGUE])
    assert "Add the exact error message or stack trace" in brief.limitations
    assert brief.advice == [] and gemini.tasks == []


def test_no_history_is_partial_and_missing_data_is_not_low_risk() -> None:
    gemini = ScriptedProvider("gemini", [GOOD_DRAFT])
    sources = FakeSources(
        evidence=bundle([], status="weak_evidence", reasons=["No result is supported."]),
        changed={},
        facts=HistoryFacts(fixes=(), activity=None, hotspots=None),
    )

    brief = build(sources, gateway_for(gemini)).brief

    assert (brief.state, brief.reason_codes) == ("PARTIAL", [NO_HISTORY_FOUND])
    assert brief.risk.band == "INSUFFICIENT_DATA"
    assert gemini.tasks == []  # nothing to ground advice on, so no model is asked


def test_thin_history_publishes_grounded_advice_as_partial_and_says_so() -> None:
    thin = bundle(
        [result("101", PRINTED.title, PRINTED.snippet, lanes={"keyword": 1})],
        status="weak_evidence",
        reasons=["No result is supported by more than one independent search."],
    )

    brief = build(FakeSources(evidence=thin)).brief

    assert (brief.state, brief.reason_codes) == ("PARTIAL", [WEAK_EVIDENCE])
    assert [a.advice_id for a in brief.advice] == ["ADV-001"]  # E2 is gone: its advice is dropped
    assert brief.evidence[0].relevance == "related"
    assert any("more than one independent search" in line for line in brief.limitations)


def test_unsupported_statements_are_removed_and_counted() -> None:
    draft = {
        "advice": [
            advice("Rewrite the whole payment module in Rust.", "E1", "rewrite everything now"),
            advice("Check the retry settings in the cloud console.", "E7"),
            GOOD_DRAFT["advice"][0],
        ],
        "limitations": [],
    }

    with structlog.testing.capture_logs() as logs:
        brief = build(gateway=gateway_for(ScriptedProvider("gemini", [draft]))).brief

    assert [a.advice_id for a in brief.advice] == ["ADV-001"]
    assert "2 generated statement(s) were removed" in " ".join(brief.limitations)
    (removed,) = [entry["removed"] for entry in logs if entry["event"] == "advice_removed"]
    assert removed == {"quote_not_in_source": 1, "unknown_evidence": 1}


def test_a_draft_with_nothing_supported_is_partial_not_padded() -> None:
    draft = {"advice": [advice("Restart the server every night.", "E1", "restart it nightly")]}
    draft["limitations"] = []

    brief = build(gateway=gateway_for(ScriptedProvider("gemini", [draft]))).brief

    assert (brief.state, brief.reason_codes, brief.advice) == ("PARTIAL", [NO_SUPPORTED_ADVICE], [])
    assert len(brief.evidence) == 2  # the evidence found is still shown


# ---- S08-05: budget and provider failures ----------------------------------------------------


@pytest.mark.parametrize(
    ("script", "limits", "reason"),
    [
        ([GOOD_DRAFT], {"max_calls": 0}, "llm_budget_exhausted"),
        (["ERROR"], {}, "llm_unavailable"),
        (["TIMEOUT"], {}, "llm_unavailable"),
        (["REFUSED"], {}, "llm_refused"),
    ],
)
def test_when_no_advice_can_be_generated_the_brief_is_partial_with_the_evidence(
    script, limits, reason
) -> None:
    brief = build(
        gateway=gateway_for(ScriptedProvider("gemini", script)), settings=BriefSettings(**limits)
    ).brief

    assert (brief.state, brief.reason_codes) == ("PARTIAL", [reason])
    assert brief.advice == []  # never invented advice
    assert [item.id for item in brief.evidence] == ["E1", "E2"]  # the evidence found
    assert brief.risk.band == "MODERATE" and len(brief.risk.factors) == 5
    assert any("No advice could be generated" in line for line in brief.limitations)


def test_restricted_data_with_no_approved_provider_is_partial() -> None:
    claude_only = gateway_for(ScriptedProvider("claude", [GOOD_DRAFT]), claude_limit=None)

    brief = build(
        gateway=claude_only, settings=BriefSettings(classification=DataClassification.RESTRICTED)
    ).brief

    assert (brief.state, brief.reason_codes) == ("PARTIAL", ["llm_no_allowed_provider"])


def test_all_reason_codes_fit_the_event_contract() -> None:
    import re

    from app.brief import pipeline, worker
    from app.llm import gateway

    codes = [
        pipeline.TICKET_NOT_READABLE,
        pipeline.TICKET_TOO_VAGUE,
        pipeline.NO_HISTORY_FOUND,
        pipeline.WEAK_EVIDENCE,
        pipeline.NO_SUPPORTED_ADVICE,
        gateway.REASON_BUDGET,
        gateway.REASON_NO_PROVIDER,
        gateway.REASON_REFUSED,
        gateway.REASON_UNAVAILABLE,
        worker.ANALYSIS_FAILED,
    ]

    assert all(re.fullmatch(REASON, code) for code in codes) and len(set(codes)) == len(codes)


# ---- S08-10 / S08-11: second opinion ---------------------------------------------------------


def test_conflicting_evidence_escalates_to_claude_and_the_disagreement_is_shown() -> None:
    gemini = ScriptedProvider("gemini", [GOOD_DRAFT])
    claude = ScriptedProvider(
        "claude",
        [
            {
                "verdicts": [
                    {"index": 0, "verdict": "supported", "reason": "E1 says so."},
                    {"index": 1, "verdict": "conflicting", "reason": "E2 only says it was merged."},
                    {"index": 9, "verdict": "unsupported", "reason": "no such item"},
                ]
            }
        ],
    )

    outcome = build(gateway=gateway_for(gemini, claude))
    brief = outcome.brief

    assert [a.evidence_state for a in brief.advice] == ["UNVERIFIED", "DISPUTED"]
    (note,) = brief.disagreements
    assert (note.advice_id, note.drafted_by, note.reviewed_by, note.verdict) == (
        "ADV-002",
        "gemini",
        "claude",
        "conflicting",
    )
    assert brief.generation.reviewed_by == "claude" and brief.generation.calls == 2
    assert [c.role for c in outcome.ledger.calls] == ["primary", "escalation"]
    # The reviewer judges the draft; it never adds, rewrites or rescues advice.
    assert len(brief.advice) == 2 and brief.state == "PUBLISHED"
    assert '"statement": "Do not reuse the retry counter fix' in claude.tasks[0].user_prompt


def test_a_routine_brief_is_not_escalated() -> None:
    calm = FakeSources(
        evidence=bundle([PRINTED]),
        facts=HistoryFacts(fixes=(), activity=9, hotspots=()),
    )
    claude = ScriptedProvider("claude", [{"verdicts": []}])

    outcome = build(calm, gateway_for(ScriptedProvider("gemini", [GOOD_DRAFT]), claude))

    assert claude.tasks == [] and outcome.brief.generation.reviewed_by is None
    assert outcome.brief.risk.band == "LOW"


def test_escalation_is_skipped_when_the_budget_or_d04_does_not_allow_it() -> None:
    claude = ScriptedProvider("claude", [{"verdicts": []}])
    gemini = ScriptedProvider("gemini", [GOOD_DRAFT, GOOD_DRAFT])

    no_budget = build(gateway=gateway_for(gemini, claude), settings=BriefSettings(max_calls=1))
    not_allowed = build(gateway=gateway_for(gemini, claude, claude_limit=None))

    assert claude.tasks == []
    for outcome in (no_budget, not_allowed):
        assert outcome.brief.state == "PUBLISHED" and outcome.brief.disagreements == []


def test_a_lesson_is_a_fact_for_the_model_not_a_source_to_cite() -> None:
    lesson = Lesson("p1", 3, "disputed", "Retry handling breaks printing", ["/src/Payments/x.cs"])
    draft = {
        "advice": [
            # A lesson has no link an engineer can open, so advice citing only "the lesson" goes.
            advice("Retry handling has broken printing before.", "E3", "Retry handling breaks"),
            GOOD_DRAFT["advice"][0],
        ],
        "limitations": [],
    }
    gemini = ScriptedProvider("gemini", [draft])
    claude = ScriptedProvider("claude", [{"verdicts": []}])
    sources = FakeSources(evidence=bundle([PRINTED, DUPLICATE], lessons=[lesson]))

    brief = build(sources, gateway_for(gemini, claude)).brief

    assert [item.id for item in brief.evidence] == ["E1", "E2"]
    assert all(item.url for item in brief.evidence)  # every citable source has a link
    assert "Recorded recurring pattern (disputed): Retry handling breaks printing" in (
        gemini.tasks[0].user_prompt
    )
    assert [a.advice_id for a in brief.advice] == ["ADV-001"]
    assert len(claude.tasks) == 1  # a disputed lesson is conflicting evidence: second opinion


# ---- Offline mode and timings ----------------------------------------------------------------


def test_offline_mode_builds_a_grounded_brief_without_any_model() -> None:
    outcome = build(gateway=offline_gateway())
    brief = outcome.brief

    assert brief.state == "PUBLISHED" and brief.generation.provider == "offline"
    assert [a.kind for a in brief.advice] == ["APPROACH", "DONT"]
    assert all(
        a.support[0].quote in e.snippet for a, e in zip(brief.advice, brief.evidence, strict=True)
    )
    assert any("offline" in line for line in brief.limitations)


def test_stage_timings_are_recorded_for_every_run() -> None:
    ticks = iter(range(0, 10_000, 250))
    outcome = build_brief(
        request(),
        FakeSources(),
        gateway_for(ScriptedProvider("gemini", [GOOD_DRAFT])),
        BriefSettings(),
        clock=lambda: next(ticks) / 1000,
    )

    timings = outcome.brief.timings
    assert timings.retrieval_ms == 250 and timings.generation_ms > 0
    assert timings.total_ms >= timings.retrieval_ms + timings.generation_ms


def test_the_revert_note_shown_to_the_model_can_be_quoted() -> None:
    draft = {
        "advice": [
            advice(
                "Do not repeat the duplicate receipt retry fix: it was reverted.",
                "E2",
                "A fix linked to this ticket was later reverted.",
                "DONT",
            )
        ],
        "limitations": [],
    }

    brief = build(gateway=gateway_for(ScriptedProvider("gemini", [draft]))).brief

    assert [(a.kind, a.evidence_ids) for a in brief.advice] == [("DONT", ["E2"])]


def test_a_failed_lesson_search_means_unknown_not_none() -> None:
    degraded = bundle(
        [PRINTED],
        reasons=["lessons search unavailable (OperationalError)", "Results may be incomplete."],
    )
    calm = FakeSources(evidence=degraded, facts=HistoryFacts(fixes=(), activity=9, hotspots=()))

    brief = build(calm).brief

    assert brief.risk.band == "INSUFFICIENT_DATA"
    assert next(f for f in brief.risk.factors if f.code == "known_lessons").missing
