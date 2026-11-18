"""ENH-02 against real PostgreSQL (opt-in, after `python -m db.manage all`).

A starter request (a deep request for an enhancement) goes all the way: accepted by the push
handler, claimed by the fix worker, answered with a stored result of kind ``starter`` and the
``analysis.completed`` event. And the guidance a starter is built from is read back from what
the brief worker really stored (ENH-01), for the run's own ticket revision only.
"""

import os
from pathlib import Path

import pytest

psycopg = pytest.importorskip("psycopg")

from brief_support import gateway_for  # noqa: E402
from corpus_support import Corpus  # noqa: E402
from defect_support import claude, make_demo, settings  # noqa: E402
from test_brief_pipeline_db import accept as accept_brief  # noqa: E402
from test_brief_pipeline_db import drain as drain_briefs  # noqa: E402
from test_brief_pipeline_db import live_ticket, request_event  # noqa: E402
from test_brief_pipeline_db import stored as stored_brief  # noqa: E402
from test_brief_pipeline_db import worker as brief_worker  # noqa: E402
from test_defect_pipeline_db import (  # noqa: E402
    CONTRACTS,
    TOPOLOGY,
    accept,
    deep_request,
    drain,
    intelligence,
    stored,
)
from test_retrieval_db import demo_history  # noqa: E402
from test_starter_pipeline import GOOD, Sources  # noqa: E402

from app.brief.history import PostgresBriefSources  # noqa: E402
from app.defect.sources import PostgresFixSources  # noqa: E402
from app.defect.worker import FixWorker  # noqa: E402
from app.events.envelope import EventEnvelope  # noqa: E402
from app.events.processing import STARTER_PIPELINE_DISABLED, EventContext  # noqa: E402
from app.knowledge.embeddings import HashingEmbedder  # noqa: E402
from app.starter import pipeline as starter  # noqa: E402
from app.starter.pipeline import parse_guidance  # noqa: E402

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
STARTERS_ON = EventContext(CONTRACTS, TOPOLOGY, starter_pipeline_enabled=True)
# The fix pipeline being on does not make a starter possible.
STARTERS_OFF = EventContext(CONTRACTS, TOPOLOGY, defect_pipeline_enabled=True)


def starter_request(base_sha: str) -> EventEnvelope:
    deep = deep_request(base_sha)
    envelope = EventEnvelope.model_validate(
        {**deep.model_dump(mode="json"), "data": {**deep.data, "ticket_kind": "enhancement"}}
    )
    CONTRACTS.validate(envelope.model_dump(mode="json"))
    return envelope


def test_a_starter_request_is_answered_once_with_a_stored_starter(tmp_path: Path) -> None:
    demo = make_demo(tmp_path, starter_pr={"enabled": True})
    envelope = starter_request(demo.base_sha)
    accept(envelope, STARTERS_ON)
    job = FixWorker(
        intelligence,
        lambda scope: Sources(),
        gateway_for(claude(GOOD)),
        demo.policy,
        settings(tmp_path),
        STARTERS_ON,
        fixes_enabled=False,
        starters_enabled=True,
    )

    drain(job)
    drain(job)  # nothing left: no second answer

    result = stored(envelope.run_id)
    status, result_id, state, reasons, attempts = result["request"]
    fix_id, fix_state, verdict, reproducibility, overall, base, candidate, tree, document = result[
        "fix"
    ]
    assert (status, state, attempts) == ("completed", "PARTIAL", 1) and result_id == fix_id
    assert reasons == [starter.NOT_VERIFIED]
    # Stored as what it is: not verified, nothing reproduced, never green.
    assert (fix_state, verdict, reproducibility, overall) == (
        "PARTIAL",
        "INCONCLUSIVE",
        "UNAVAILABLE",
        "NOT_GREEN",
    )
    assert base == demo.base_sha and candidate and tree
    assert document["kind"] == "starter"
    assert document["pull_request"]["source_branch"].startswith("visioniq/starter/")
    assert document["pull_request"]["is_draft"] is True
    assert result["executions"] == []  # nothing was run
    assert result["calls"] == [("starter_scaffold", "claude", "OK")]
    ((payload,),) = result["events"]
    CONTRACTS.validate(payload)
    assert payload["event_type"] == "analysis.completed"
    assert (payload["data"]["result_id"], payload["data"]["result_state"]) == (
        str(fix_id),
        "PARTIAL",
    )
    assert payload["data"]["reason_codes"] == [starter.NOT_VERIFIED]


def test_with_the_starter_pipeline_off_a_starter_request_is_blocked_at_once(
    tmp_path: Path,
) -> None:
    demo = make_demo(tmp_path)
    envelope = starter_request(demo.base_sha)

    accept(envelope, STARTERS_OFF)

    result = stored(envelope.run_id)
    status, _result_id, state, reasons, attempts = result["request"]
    assert (status, state, attempts) == ("completed", "BLOCKED", 0)
    assert reasons == [STARTER_PIPELINE_DISABLED]
    assert result["fix"] is None  # never queued, nothing stored as a result
    ((payload,),) = result["events"]
    assert payload["data"]["reason_codes"] == [STARTER_PIPELINE_DISABLED]


def test_guidance_is_read_back_from_the_stored_brief_of_the_same_ticket_revision() -> None:
    corpus = Corpus()
    demo_history(corpus)
    first = request_event(corpus, live_ticket(corpus))
    envelope = EventEnvelope.model_validate(
        {**first.model_dump(mode="json"), "data": {**first.data, "ticket_kind": "enhancement"}}
    )
    accept_brief(envelope)
    drain_briefs(brief_worker())
    brief_id, _state, _band, document, _accepted_at, _ = stored_brief(envelope.run_id)["brief"]
    revision = envelope.data["ticket_revision"]
    sources = PostgresFixSources(
        intelligence,
        PostgresBriefSources(intelligence, HashingEmbedder(), [corpus.scope], lambda key: True),
        [corpus.scope],
    )

    found = sources.guidance("Payments", "4711", revision)
    other_revision = sources.guidance("Payments", "4711", revision + 1000)
    other_ticket = sources.guidance("Payments", "no-such-ticket", revision)

    assert found is not None and found["brief_id"] == str(brief_id) == document["brief_id"]
    guidance = parse_guidance(found)
    assert guidance is not None and guidance.brief_id == brief_id
    assert guidance.state == document["state"]
    assert "related_past_changes" in guidance.covered
    assert [item.advice_id for item in guidance.items] == [
        a["advice_id"] for a in document["advice"]
    ]
    assert other_revision is None and other_ticket is None
