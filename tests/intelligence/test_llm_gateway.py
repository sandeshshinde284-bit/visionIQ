"""BRF-02: routing table (S08-04), budget and fallback (S08-05), escalation (S08-10), call log."""

import uuid

import pytest
import structlog
from brief_support import GOOD_DRAFT, ScriptedProvider, gateway_for

from app.llm.gateway import (
    REASON_BUDGET,
    REASON_NO_PROVIDER,
    REASON_REFUSED,
    REASON_UNAVAILABLE,
    Budget,
    RunLedger,
)
from app.llm.prompts import TEMPLATES
from app.llm.routing import (
    ESCALATE_EVIDENCE_CONFLICT,
    ESCALATE_HIGH_IMPACT,
    ROUTING_REVISION,
    ROUTING_TABLE,
    RoutingPolicy,
)
from app.llm.types import DataClassification, EvidenceContext, EvidenceItem, TaskKind

CONTEXT = EvidenceContext(
    snapshot_id="snap-1",
    ticket_title="Bug 4711: Barcode not printed",
    ticket_text="Barcode print failed after retry.",
    evidence=(
        EvidenceItem(
            evidence_id="E1",
            kind="work_item_current",
            title="Bug 101",
            text="Pay at kiosk, payment captured, retry printing: no barcode on the receipt. "
            "Fixed by making the retry policy idempotent.",
        ),
        EvidenceItem(
            evidence_id="E2",
            kind="work_item_current",
            title="Bug 102",
            text="Retrying a captured payment prints twice. The retry counter fix was merged.",
        ),
    ),
)
VERDICTS = {"verdicts": [{"index": 0, "verdict": "supported", "reason": "matches"}]}


def ledger(
    classification: DataClassification = DataClassification.INTERNAL,
    max_calls: int = 3,
    max_tokens: int = 60_000,
    max_seconds: float = 90.0,
    clock=None,
) -> RunLedger:
    budget = (
        Budget(max_calls, max_tokens, max_seconds, clock)
        if clock
        else Budget(max_calls, max_tokens, max_seconds)
    )
    return RunLedger(uuid.uuid4(), classification, budget)


def policy(*names: str, claude: DataClassification | None = DataClassification.INTERNAL):
    providers = {name: ScriptedProvider(name, [GOOD_DRAFT]) for name in names}
    access = {"gemini": DataClassification.RESTRICTED}
    if claude is not None:
        access["claude"] = claude
    return RoutingPolicy(providers, access)


# ---- S08-04: every branch of the routing table -----------------------------------------------


@pytest.mark.parametrize(
    ("task", "primary"),
    [
        (TaskKind.TICKET_CLASSIFICATION, "gemini"),
        (TaskKind.BRIEF_DRAFT, "gemini"),
        (TaskKind.EVIDENCE_CRITIQUE, "claude"),
        (TaskKind.ROOT_CAUSE, "gemini"),
        (TaskKind.CODE_FIX, "claude"),
        (TaskKind.ENHANCEMENT_APPROACH, "gemini"),
        (TaskKind.KNOWLEDGE_EXTRACTION, "gemini"),
    ],
)
def test_each_task_goes_to_the_provider_the_v1_11_table_names(task, primary) -> None:
    decision = policy("gemini", "claude").primary(task, DataClassification.INTERNAL)

    assert decision.provider is not None and decision.provider.name == primary
    assert (decision.role, decision.reason) == ("primary", "routing_table")


def test_every_task_kind_has_a_route_and_analytics_uses_no_model() -> None:
    assert set(ROUTING_TABLE) == set(TaskKind)
    decision = policy("gemini", "claude").primary(
        TaskKind.ADVICE_ANALYTICS, DataClassification.PUBLIC
    )
    assert (decision.provider, decision.reason) == (None, "no_model_for_task")


def test_data_above_a_providers_limit_is_never_routed_to_it() -> None:
    routing = policy("gemini", "claude", claude=DataClassification.INTERNAL)

    restricted = routing.primary(TaskKind.CODE_FIX, DataClassification.RESTRICTED)
    internal = routing.primary(TaskKind.CODE_FIX, DataClassification.INTERNAL)

    assert (restricted.provider, restricted.reason) == (None, "no_allowed_provider")
    assert internal.provider is not None and internal.provider.name == "claude"


def test_claude_gets_nothing_until_d04_sets_a_limit() -> None:
    routing = policy("gemini", "claude", claude=None)

    assert routing.primary(TaskKind.CODE_FIX, DataClassification.PUBLIC).provider is None
    assert (
        routing.escalation(
            TaskKind.BRIEF_DRAFT,
            DataClassification.PUBLIC,
            {ESCALATE_HIGH_IMPACT},
            drafted_by="gemini",
        )
        is None
    )
    assert routing.fallback(TaskKind.BRIEF_DRAFT, DataClassification.PUBLIC, "gemini") is None


def test_a_brief_falls_to_claude_only_when_gemini_is_not_configured() -> None:
    decision = policy("claude").primary(TaskKind.BRIEF_DRAFT, DataClassification.INTERNAL)

    assert decision.provider is not None and decision.provider.name == "claude"
    assert decision.reason == "primary_not_available"
    # Code fixes have no such fallback: Gemini does not write patches (no benchmark yet).
    assert policy("gemini").primary(TaskKind.CODE_FIX, DataClassification.PUBLIC).provider is None


def test_escalation_needs_a_listed_signal_and_a_different_provider() -> None:
    routing = policy("gemini", "claude")
    ask = routing.escalation

    assert ask(TaskKind.BRIEF_DRAFT, DataClassification.INTERNAL, set(), "gemini") is None
    assert ask(TaskKind.BRIEF_DRAFT, DataClassification.INTERNAL, {"curious"}, "gemini") is None
    chosen = ask(
        TaskKind.BRIEF_DRAFT,
        DataClassification.INTERNAL,
        {ESCALATE_HIGH_IMPACT, ESCALATE_EVIDENCE_CONFLICT},
        "gemini",
    )
    assert chosen is not None and chosen.provider is not None
    assert (chosen.provider.name, chosen.role, chosen.reason) == (
        "claude",
        "escalation",
        ESCALATE_EVIDENCE_CONFLICT,
    )
    # Claude never reviews its own draft.
    assert (
        ask(TaskKind.BRIEF_DRAFT, DataClassification.INTERNAL, {ESCALATE_HIGH_IMPACT}, "claude")
        is None
    )


# ---- Gateway: one call by default, logged; bounded fallback; budget -------------------------


def test_a_brief_is_one_gemini_call_and_claude_is_not_called() -> None:
    gemini = ScriptedProvider("gemini", [GOOD_DRAFT])
    claude = ScriptedProvider("claude", [GOOD_DRAFT])
    run = ledger()

    outcome = gateway_for(gemini, claude).generate(TaskKind.BRIEF_DRAFT, CONTEXT, run)

    assert outcome.result is not None and outcome.reason is None
    assert (len(gemini.tasks), len(claude.tasks)) == (1, 0)  # no blind double calls
    assert run.budget.calls == 1 and run.budget.tokens == 1100


def test_every_call_logs_provider_model_prompt_version_and_tokens() -> None:
    gemini = ScriptedProvider("gemini", [GOOD_DRAFT], model_id="gemini-test")

    with structlog.testing.capture_logs() as logs:
        gateway_for(gemini).generate(TaskKind.BRIEF_DRAFT, CONTEXT, ledger())

    (call,) = [entry for entry in logs if entry["event"] == "llm_call"]
    assert call["provider"] == "gemini" and call["model_id"] == "gemini-test"
    assert call["prompt_template_version"] == "brief.defect@v1"
    assert call["routing_revision"] == ROUTING_REVISION
    assert (call["input_tokens"], call["output_tokens"], call["status"]) == (900, 200, "OK")
    assert call["data_classification"] == "INTERNAL" and call["role"] == "primary"
    # The log is about the call, never its content.
    assert "Barcode" not in str(call) and "idempotent" not in str(call)


def test_a_provider_error_falls_back_to_claude_once_with_the_same_prompt() -> None:
    gemini = ScriptedProvider("gemini", ["ERROR"])
    claude = ScriptedProvider("claude", [GOOD_DRAFT])
    run = ledger()

    outcome = gateway_for(gemini, claude).generate(TaskKind.BRIEF_DRAFT, CONTEXT, run)

    assert outcome.result is not None and outcome.result.provider == "claude"
    assert [(c.result.provider, c.role, c.result.status) for c in run.calls] == [
        ("gemini", "primary", "ERROR"),
        ("claude", "fallback", "OK"),
    ]
    assert claude.tasks[0].user_prompt == gemini.tasks[0].user_prompt
    assert claude.tasks[0].output_schema == gemini.tasks[0].output_schema


def test_restricted_data_does_not_fall_back_to_claude() -> None:
    gemini = ScriptedProvider("gemini", ["TIMEOUT"])
    claude = ScriptedProvider("claude", [GOOD_DRAFT])

    outcome = gateway_for(gemini, claude).generate(
        TaskKind.BRIEF_DRAFT, CONTEXT, ledger(DataClassification.RESTRICTED)
    )

    assert (outcome.result, outcome.reason) == (None, REASON_UNAVAILABLE)
    assert claude.tasks == []


def test_a_refusal_is_not_papered_over_by_a_second_model() -> None:
    gemini = ScriptedProvider("gemini", ["REFUSED"])
    claude = ScriptedProvider("claude", [GOOD_DRAFT])

    outcome = gateway_for(gemini, claude).generate(TaskKind.BRIEF_DRAFT, CONTEXT, ledger())

    assert (outcome.result, outcome.reason) == (None, REASON_REFUSED)
    assert claude.tasks == []


@pytest.mark.parametrize(
    ("limits", "spent"),
    [
        ({"max_calls": 0}, "calls"),
        ({"max_tokens": 1_000}, "tokens"),
        ({"max_seconds": 5.0}, "time"),
    ],
)
def test_a_spent_budget_stops_before_the_next_call(limits, spent) -> None:
    now = [100.0]
    gemini = ScriptedProvider("gemini", [GOOD_DRAFT, GOOD_DRAFT])
    gateway = gateway_for(gemini)
    run = ledger(clock=lambda: now[0], **limits)
    if spent != "calls":
        assert gateway.generate(TaskKind.BRIEF_DRAFT, CONTEXT, run).result is not None
        now[0] += 6.0  # the first call used the tokens (1100 > 1000) or the clock moved on

    with structlog.testing.capture_logs() as logs:
        outcome = gateway.generate(TaskKind.BRIEF_DRAFT, CONTEXT, run)

    assert (outcome.result, outcome.reason) == (None, REASON_BUDGET)
    assert [entry["limit"] for entry in logs if entry["event"] == "llm_budget_exhausted"] == [spent]
    assert len(gemini.tasks) == (0 if spent == "calls" else 1)


def test_the_call_timeout_never_exceeds_what_is_left_of_the_run() -> None:
    now = [0.0]
    gemini = ScriptedProvider("gemini", [GOOD_DRAFT])
    run = ledger(max_seconds=20.0, clock=lambda: now[0])
    now[0] = 12.0

    gateway_for(gemini).generate(TaskKind.BRIEF_DRAFT, CONTEXT, run)

    assert gemini.policies[0].timeout_seconds == pytest.approx(8.0)


def test_output_that_breaks_the_schema_is_an_error_not_a_result() -> None:
    gemini = ScriptedProvider("gemini", [{"advice": "write whatever you like"}])
    run = ledger()

    outcome = gateway_for(gemini).generate(TaskKind.BRIEF_DRAFT, CONTEXT, run)

    assert (outcome.result, outcome.reason) == (None, REASON_UNAVAILABLE)
    assert run.calls[0].result.error_code == "schema_violation"


def test_a_crashing_provider_is_a_failed_call_not_a_failed_run() -> None:
    gemini = ScriptedProvider("gemini", [RuntimeError("boom")])
    run = ledger()

    outcome = gateway_for(gemini).generate(TaskKind.BRIEF_DRAFT, CONTEXT, run)

    assert outcome.reason == REASON_UNAVAILABLE
    assert (run.calls[0].result.status, run.calls[0].result.error_code) == ("ERROR", "RuntimeError")


def test_no_usable_provider_is_reported_without_building_a_prompt() -> None:
    outcome = gateway_for().generate(TaskKind.BRIEF_DRAFT, CONTEXT, ledger())

    assert (outcome.result, outcome.reason) == (None, REASON_NO_PROVIDER)


def test_an_escalation_is_logged_and_charged_to_the_same_budget() -> None:
    gemini = ScriptedProvider("gemini", [GOOD_DRAFT])
    claude = ScriptedProvider("claude", [VERDICTS])
    gateway = gateway_for(gemini, claude)
    run = ledger(max_calls=2)
    gateway.generate(TaskKind.BRIEF_DRAFT, CONTEXT, run)

    with structlog.testing.capture_logs() as logs:
        review = gateway.escalate(
            TaskKind.BRIEF_DRAFT,
            TaskKind.EVIDENCE_CRITIQUE,
            CONTEXT,
            run,
            {ESCALATE_HIGH_IMPACT},
            drafted_by="gemini",
        )
        again = gateway.escalate(
            TaskKind.BRIEF_DRAFT,
            TaskKind.EVIDENCE_CRITIQUE,
            CONTEXT,
            run,
            {ESCALATE_HIGH_IMPACT},
            drafted_by="gemini",
        )

    assert review.result is not None and review.result.provider == "claude"
    assert claude.tasks[0].prompt_template_version == "brief.critique@v1"
    assert run.calls[-1].role == "escalation" and run.budget.calls == 2
    assert next(e["reason"] for e in logs if e["event"] == "llm_escalation") == ESCALATE_HIGH_IMPACT
    assert again.reason == REASON_BUDGET  # bounded: the budget covers escalations too


def test_prompt_versions_are_named_and_unique() -> None:
    versions = [template.template_version for template in TEMPLATES.values()]

    assert versions == [
        "brief.defect@v1",
        "brief.critique@v1",
        "brief.enhancement@v1",  # ENH-01: enhancement guidance (master design 8.5)
        "defect.reproduction@v1",
        "defect.fix@v1",
    ]
    assert len(set(versions)) == len(versions)
