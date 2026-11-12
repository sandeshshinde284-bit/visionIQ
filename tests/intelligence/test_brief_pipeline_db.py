"""BRF-03 / BRF-04 / BRF-08 against real PostgreSQL (opt-in, after `python -m db.manage all`).

One request goes all the way: accepted by the push handler, claimed by the analysis worker,
answered with a stored, immutable brief whose advice cites real evidence, and the
``analysis.completed`` event queued in the same transaction. The hotspot sweep reads the same
history and the next brief shows the flagged area.
"""

import json
import os
import uuid

import pytest

psycopg = pytest.importorskip("psycopg")

from corpus_support import Corpus, conninfo  # noqa: E402
from jsonschema import Draft202012Validator  # noqa: E402
from test_retrieval_db import LIVE_DEFECT, bug, demo_history  # noqa: E402

from app.brief import schema  # noqa: E402
from app.brief.history import PostgresBriefSources  # noqa: E402
from app.brief.pipeline import BriefSettings  # noqa: E402
from app.brief.worker import AnalysisWorker  # noqa: E402
from app.events.contracts import get_contracts  # noqa: E402
from app.events.envelope import EventEnvelope  # noqa: E402
from app.events.processing import (  # noqa: E402
    EventContext,
    EventRejectedError,
    handle_analysis_requested,
)
from app.events.topology import load_topology  # noqa: E402
from app.hotspots.sweep import run_sweep  # noqa: E402
from app.knowledge import snapshots  # noqa: E402
from app.knowledge.embeddings import HashingEmbedder  # noqa: E402
from app.llm.gateway import ModelGateway  # noqa: E402
from app.llm.providers.offline import OfflineProvider  # noqa: E402
from app.llm.routing import RoutingPolicy  # noqa: E402

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
CONTRACTS = get_contracts()
CONTEXT = EventContext(CONTRACTS, load_topology(CONTRACTS.directory), brief_pipeline_enabled=True)
FIXTURE = json.loads(
    (CONTRACTS.directory / "fixtures/valid/analysis.requested.v1.json").read_text("utf-8")
)


def intelligence():
    return psycopg.connect(conninfo("INTELLIGENCE"))


def request_event(corpus: Corpus, ticket: str, scope: str | None = None) -> EventEnvelope:
    event = json.loads(json.dumps(FIXTURE))
    event["event_id"], event["run_id"] = str(uuid.uuid4()), str(uuid.uuid4())
    event["correlation_id"] = event["run_id"]
    event["data"].update(
        project_id="Payments", ticket_id=ticket, security_scope_id=scope or corpus.scope
    )
    return EventEnvelope.model_validate(event)


def accept(envelope: EventEnvelope) -> None:
    """What the push endpoint does: the inbox row and the queued request, in one transaction."""
    with intelligence() as connection, connection.transaction():
        connection.execute(
            "INSERT INTO knowledge.event_inbox (event_id, event_type, schema_version, trace_id) "
            "VALUES (%s, %s, %s, %s)",
            (envelope.event_id, envelope.event_type, envelope.schema_version, envelope.trace_id),
        )
        handle_analysis_requested(connection, envelope, CONTEXT)


def worker() -> AnalysisWorker:
    embedder = HashingEmbedder()
    return AnalysisWorker(
        intelligence,
        lambda scope: PostgresBriefSources(intelligence, embedder, [scope], lambda key: True),
        ModelGateway(RoutingPolicy({}, {}, override=OfflineProvider())),
        BriefSettings(),
        CONTEXT,
    )


def drain(job: AnalysisWorker) -> None:
    while job.run_once():
        pass


def stored(run_id: uuid.UUID) -> dict:
    with intelligence() as connection:
        request = connection.execute(
            "SELECT status, result_id, result_state, reason_codes, attempt_count "
            "FROM knowledge.analysis_requests WHERE run_id = %s",
            (run_id,),
        ).fetchone()
        brief = connection.execute(
            "SELECT brief_id, state, risk_band, document, accepted_at, total_ms "
            "FROM knowledge.briefs WHERE run_id = %s",
            (run_id,),
        ).fetchone()
        advice = connection.execute(
            "SELECT advice_id, kind, evidence_ids FROM knowledge.brief_advice "
            "WHERE brief_id = %s ORDER BY advice_id",
            (brief[0] if brief else None,),
        ).fetchall()
        calls = connection.execute(
            "SELECT task, role, provider, model_id, prompt_template_version, status "
            "FROM knowledge.llm_calls WHERE run_id = %s",
            (run_id,),
        ).fetchall()
        events = connection.execute(
            "SELECT payload FROM knowledge.event_outbox WHERE payload ->> 'run_id' = %s",
            (str(run_id),),
        ).fetchall()
    return {"request": request, "brief": brief, "advice": advice, "calls": calls, "events": events}


def live_ticket(corpus: Corpus) -> str:
    """The defect an engineer just picked up, ingested and indexed like any other ticket."""
    corpus.add(
        "work_item_current",
        bug(4711, "Barcode not printed after payment retry", LIVE_DEFECT),
    )
    snapshots.refresh(conninfo("INTELLIGENCE"), corpus.read, HashingEmbedder())
    return "4711"


def test_a_request_is_answered_once_with_a_grounded_stored_brief() -> None:
    corpus = Corpus()
    demo_history(corpus)
    envelope = request_event(corpus, live_ticket(corpus))
    accept(envelope)
    assert stored(envelope.run_id)["request"][:3] == ("pending", None, None)

    job = worker()
    drain(job)
    drain(job)  # a second pass finds nothing left to do

    result = stored(envelope.run_id)
    status, result_id, state, reasons, attempts = result["request"]
    brief_id, brief_state, band, document, accepted_at, _ = result["brief"]
    assert (status, state, reasons, attempts) == ("completed", "PUBLISHED", [], 1)
    assert result_id == brief_id and brief_state == "PUBLISHED" and accepted_at is not None
    assert list(Draft202012Validator(schema.json_schema()).iter_errors(document)) == []

    # Every advice item cites evidence whose link opens the source ticket.
    urls = {item["id"]: item["url"] for item in document["evidence"]}
    assert result["advice"] and len(result["advice"]) == len(document["advice"])
    for advice_id, _kind, evidence_ids in result["advice"]:
        assert advice_id.startswith("ADV-") and evidence_ids
        assert all(
            urls[e].startswith(f"{corpus.org}/Payments/_workitems/edit/") for e in evidence_ids
        )
    kinds = {kind for _, kind, _ in result["advice"]}
    assert "DONT" in kinds  # the reverted fix of ticket 102 becomes a "don't"
    assert not any(
        item["item_key"] == corpus.key("work_item_current", "4711") for item in document["evidence"]
    )

    # Risk comes from recorded history: two fixes touched RetryPolicy.cs, one was reverted.
    factors = {f["code"]: f for f in document["risk"]["factors"]}
    assert band == "MODERATE" and factors["recent_fixes"]["value"] == 2
    assert factors["reverted_fixes"]["value"] == 1
    assert "ticket 102 -> PR 202 (reverted)" in factors["reverted_fixes"]["references"]
    assert factors["change_activity"]["value"] >= 2
    assert {s["path"] for s in document["predicted_scope"]} >= {"RetryPolicy.cs"}

    # One call logged, one reply queued, and the reply matches its contract.
    # The draft, then a second opinion because a cited fix was reverted (evidence conflict).
    assert sorted(result["calls"]) == [
        ("brief_draft", "primary", "offline", "offline-extractive-1", "brief.defect@v1", "OK"),
        (
            "evidence_critique",
            "escalation",
            "offline",
            "offline-extractive-1",
            "brief.critique@v1",
            "OK",
        ),
    ]
    ((payload,),) = result["events"]
    CONTRACTS.validate(payload)
    assert payload["data"]["result_id"] == str(brief_id)
    assert payload["data"]["advice_ids"] == [a[0] for a in result["advice"]]
    assert payload["trace_id"] == envelope.trace_id


def test_a_stored_brief_cannot_be_changed() -> None:
    corpus = Corpus()
    demo_history(corpus)
    envelope = request_event(corpus, live_ticket(corpus))
    accept(envelope)
    drain(worker())

    with intelligence() as connection:
        for statement in (
            "UPDATE knowledge.briefs SET state = 'PUBLISHED' WHERE run_id = %s",
            "DELETE FROM knowledge.briefs WHERE run_id = %s",
        ):
            with pytest.raises(psycopg.errors.RaiseException, match="immutable"):
                connection.execute(statement, (envelope.run_id,))
            connection.rollback()
    with pytest.raises(EventRejectedError, match="run_already_requested"):
        accept(  # another event for the same run is rejected, not answered again
            envelope.model_copy(update={"event_id": uuid.uuid4()})
        )


def test_a_ticket_outside_the_runs_scope_is_blocked_without_reading_it() -> None:
    corpus = Corpus()
    demo_history(corpus)
    envelope = request_event(corpus, live_ticket(corpus), scope="ado-project:someone-else/Other")
    accept(envelope)
    drain(worker())

    result = stored(envelope.run_id)
    assert result["request"][2:4] == ("BLOCKED", ["ticket_not_readable"])
    assert result["brief"][2] == "INSUFFICIENT_DATA" and result["advice"] == []
    assert result["calls"] == []  # no model saw anything
    assert result["brief"][3]["evidence"] == []


def test_the_hotspot_sweep_scores_recorded_history_and_feeds_the_next_brief() -> None:
    corpus = Corpus()
    demo_history(corpus)
    # More history in one area: /src/Payments keeps needing defect fixes.
    for number in range(300, 306):
        corpus.add("work_item_current", bug(number, f"Retry defect {number}", "Retry fails."))
        corpus.fix(str(number), str(number + 1000), ["/src/Payments/RetryPolicy.cs"])
    with psycopg.connect(conninfo("API")) as connection:
        for number in range(40):  # ordinary, non-defect changes elsewhere
            connection.execute(
                "INSERT INTO evidence.fix_change_sets (source_instance, project_id, repository_id, "
                "fix_kind, fix_id, paths, provenance_item_key, observed_at) "
                "VALUES (%s, 'Payments', 'repo-1', 'pull_request', %s, %s, %s, now())",
                (corpus.org, str(5000 + number), ["/web/site/page.css"], "a" * 64),
            )

    with intelligence() as connection:
        first = run_sweep(connection, "on_demand", window_days=365, area_depth=2)
    assert first is not None
    mine = {s.activity.area: s for s in first.areas if s.activity.source_instance == corpus.org}
    assert mine["src/Payments"].band == "HOT" and mine["src/Payments"].activity.defect_fixes == 8
    assert mine["web/site"].band == "NORMAL"  # 40 changes, no defect fixes: busy, not risky
    assert mine["web/site"].activity.changes == 40
    evidence = mine["src/Payments"].activity.evidence
    assert {
        "ticket_id": "102",
        "fix_kind": "pull_request",
        "fix_id": "202",
        "reverted": True,
    } in evidence

    with intelligence() as connection:
        rows = connection.execute(
            "SELECT band, rank, jsonb_array_length(evidence) FROM knowledge.hotspot_areas "
            "WHERE sweep_id = %s AND source_instance = %s AND area = 'src/Payments'",
            (first.sweep_id, corpus.org),
        ).fetchall()
    assert rows == [("HOT", mine["src/Payments"].rank, 8)]

    envelope = request_event(corpus, live_ticket(corpus))
    accept(envelope)
    drain(worker())
    document = stored(envelope.run_id)["brief"][3]
    hot = next(f for f in document["risk"]["factors"] if f["code"] == "hotspot_areas")
    assert hot["value"] == 1 and "src/Payments (HOT" in hot["references"][0]
    assert document["risk"]["band"] == "HIGH"
