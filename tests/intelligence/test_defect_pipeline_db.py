"""DEF-01 to DEF-06 against real PostgreSQL (opt-in, after `python -m db.manage all`).

One deep request goes all the way: accepted by the push handler, left alone by the brief worker,
claimed by the fix worker, and answered with a stored, immutable fix result, its executions, the
model-call log and the ``analysis.completed`` event. This is what migrations ``20261110_0024``
and the worker's SQL promise; the other fix tests use a recording connection.

The Git repository and the test runs are the same stand-ins the unit tests use (a tiny real
repository, a runner that reports instead of starting a process): the database is what is real.
"""

import json
import os
import uuid
from pathlib import Path

import pytest

psycopg = pytest.importorskip("psycopg")

from corpus_support import conninfo  # noqa: E402
from defect_support import (  # noqa: E402
    SAFE_FIX,
    TEST_ANSWER,
    FakeFixSources,
    FakeRunner,
    gateway,
    honest,
    make_demo,
    settings,
)
from jsonschema import Draft202012Validator  # noqa: E402

from app.brief import worker as brief_worker  # noqa: E402
from app.defect import schema  # noqa: E402
from app.defect import worker as fix_worker  # noqa: E402
from app.defect.worker import FixWorker, failed_fix, store  # noqa: E402
from app.events.contracts import get_contracts  # noqa: E402
from app.events.envelope import EventEnvelope  # noqa: E402
from app.events.processing import (  # noqa: E402
    DEEP_ANALYSIS_NOT_IMPLEMENTED,
    EventContext,
    handle_analysis_requested,
)
from app.events.topology import load_topology  # noqa: E402

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
CONTRACTS = get_contracts()
TOPOLOGY = load_topology(CONTRACTS.directory)
ON = EventContext(CONTRACTS, TOPOLOGY, brief_pipeline_enabled=True, defect_pipeline_enabled=True)
OFF = EventContext(CONTRACTS, TOPOLOGY, brief_pipeline_enabled=True, defect_pipeline_enabled=False)
FIXTURE = json.loads(
    (CONTRACTS.directory / "fixtures/valid/analysis.requested.v1.json").read_text("utf-8")
)


def intelligence():
    return psycopg.connect(conninfo("INTELLIGENCE"))


def deep_request(base_sha: str, repository_id: str = "kiosk-demo") -> EventEnvelope:
    event = json.loads(json.dumps(FIXTURE))
    event["event_id"], event["run_id"] = str(uuid.uuid4()), str(uuid.uuid4())
    event["correlation_id"] = event["run_id"]
    event["data"].update(
        project_id="Kiosk",
        ticket_id="4711",
        repository_id=repository_id,
        base_commit_sha=base_sha,
        analysis_mode="deep",
        security_scope_id="project:Kiosk",
    )
    return EventEnvelope.model_validate(event)


def accept(envelope: EventEnvelope, context: EventContext = ON) -> None:
    """What the push endpoint does: the inbox row and the queued request, in one transaction."""
    with intelligence() as connection, connection.transaction():
        connection.execute(
            "INSERT INTO knowledge.event_inbox (event_id, event_type, schema_version, trace_id) "
            "VALUES (%s, %s, %s, %s)",
            (envelope.event_id, envelope.event_type, envelope.schema_version, envelope.trace_id),
        )
        handle_analysis_requested(connection, envelope, context)


def worker(tmp_path: Path, demo, *answers) -> FixWorker:
    runner = FakeRunner(honest)
    return FixWorker(
        intelligence,
        lambda scope: FakeFixSources(),
        gateway(*answers),
        demo.policy,
        settings(tmp_path),
        ON,
        lambda _policy: runner,
    )


def drain(job: FixWorker) -> None:
    while job.run_once():
        pass


def stored(run_id: uuid.UUID) -> dict:
    with intelligence() as connection:
        request = connection.execute(
            "SELECT status, result_id, result_state, reason_codes, attempt_count "
            "FROM knowledge.analysis_requests WHERE run_id = %s",
            (run_id,),
        ).fetchone()
        fix = connection.execute(
            "SELECT fix_id, state, verdict, reproducibility, gates_overall, base_commit_sha, "
            "candidate_commit_sha, candidate_tree_sha, document "
            "FROM knowledge.fix_runs WHERE run_id = %s",
            (run_id,),
        ).fetchone()
        executions = connection.execute(
            "SELECT execution_id, purpose, code, outcome FROM knowledge.fix_executions "
            "WHERE fix_id = %s",
            (fix[0] if fix else None,),
        ).fetchall()
        calls = connection.execute(
            "SELECT task, provider, status FROM knowledge.llm_calls WHERE run_id = %s "
            "ORDER BY created_at",
            (run_id,),
        ).fetchall()
        events = connection.execute(
            "SELECT payload FROM knowledge.event_outbox WHERE payload ->> 'run_id' = %s",
            (str(run_id),),
        ).fetchall()
    return {
        "request": request,
        "fix": fix,
        "executions": executions,
        "calls": calls,
        "events": events,
    }


def test_a_deep_request_is_answered_once_with_a_stored_verified_fix(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    envelope = deep_request(demo.base_sha)
    accept(envelope)

    # The brief worker shares the queue and must leave a deep request alone.
    with intelligence() as connection, connection.transaction(force_rollback=True):
        assert envelope.run_id not in {
            item.request.run_id for item in brief_worker.claim(connection)
        }
    assert stored(envelope.run_id)["request"][:2] == ("pending", None)

    job = worker(tmp_path, demo, TEST_ANSWER, SAFE_FIX)
    drain(job)
    drain(job)  # nothing left to claim: the run is answered once

    result = stored(envelope.run_id)
    status, result_id, state, reasons, attempts = result["request"]
    assert (status, state, reasons, attempts) == ("completed", "PUBLISHED", [], 1)
    fix_id, fix_state, verdict, reproducibility, overall, base, candidate, tree, document = result[
        "fix"
    ]
    assert fix_id == result_id
    assert (fix_state, verdict, reproducibility) == ("PUBLISHED", "VERIFIED", "REPRODUCED")
    assert overall == "NOT_GREEN"  # native CI and the engineer's review are still open
    assert base == demo.base_sha and candidate == document["candidate_sha"]
    assert tree == document["patch"]["tree_sha"]
    validator = Draft202012Validator(schema.json_schema("fix-result.v1.schema.json"))
    assert list(validator.iter_errors(document)) == []
    assert {row[0] for row in result["executions"]} == {
        execution["execution_id"] for execution in document["executions"]
    }
    assert ("X1", "reproduction", "baseline", "FAIL_TARGET_SIGNATURE") in result["executions"]
    assert [call[0] for call in result["calls"]] == ["reproduction_test", "code_fix"]
    (event,) = result["events"]
    CONTRACTS.validate(event[0])
    assert event[0]["event_type"] == "analysis.completed"
    assert event[0]["data"]["result_id"] == str(fix_id)
    assert event[0]["data"]["result_state"] == "PUBLISHED"


def test_a_stored_fix_result_cannot_be_changed_or_answered_twice(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    envelope = deep_request(demo.base_sha)
    accept(envelope)
    drain(worker(tmp_path, demo, TEST_ANSWER, SAFE_FIX))
    fix_id = stored(envelope.run_id)["fix"][0]

    for statement in (
        "UPDATE knowledge.fix_runs SET verdict = 'FAIL' WHERE fix_id = %s",
        "DELETE FROM knowledge.fix_runs WHERE fix_id = %s",
        "UPDATE knowledge.fix_executions SET outcome = 'PASS' WHERE fix_id = %s",
        "DELETE FROM knowledge.fix_executions WHERE fix_id = %s",
    ):
        with intelligence() as connection, pytest.raises(psycopg.errors.RaiseException):
            connection.execute(statement, (fix_id,))

    # A second answer for the same run (a worker whose lease ran out) stores nothing.
    with intelligence() as connection:
        late = fix_worker.claim  # the run is completed, so there is nothing to claim
        assert envelope.run_id not in {item.request.run_id for item in late(connection)}
    claimed = fix_worker.ClaimedFix(
        fix_worker.FixRequest(
            run_id=envelope.run_id,
            ticket=fix_worker.TicketRef(project="Kiosk", id="4711", revision=12),
            repository_id="kiosk-demo",
            base_commit_sha=demo.base_sha,
            security_scope_id="project:Kiosk",
        ),
        "visioniq-dev",
        envelope,
        envelope.occurred_at,
        2,
    )
    with intelligence() as connection:
        assert store(connection, claimed, failed_fix(claimed, settings(tmp_path)), ON) is False
    after = stored(envelope.run_id)
    assert after["fix"][0] == fix_id and len(after["events"]) == 1


def test_the_database_refuses_a_published_result_that_is_not_verified(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    envelope = deep_request(demo.base_sha)
    accept(envelope)

    insert = (
        "INSERT INTO knowledge.fix_runs (fix_id, run_id, tenant_id, project_id, ticket_id, "
        "ticket_revision, repository_id, base_commit_sha, state, verdict, reason_codes, "
        "reproducibility, gates_overall, schema_version, document, routing_revision, accepted_at, "
        "total_ms) VALUES (%s, %s, 't', 'Kiosk', '4711', 1, 'kiosk-demo', %s, %s, %s, %s, %s, "
        "'NOT_GREEN', 1, '{}'::jsonb, 'r', now(), 0)"
    )
    for state, verdict, reasons, reproducibility in (
        ("PUBLISHED", "FAIL", [], "REPRODUCED"),  # green state on a failed verdict
        ("PUBLISHED", "VERIFIED", [], "REPRODUCED"),  # verified without a candidate commit
        ("PARTIAL", "INCONCLUSIVE", [], "REPRODUCED"),  # falling short without saying why
    ):
        with intelligence() as connection, pytest.raises(psycopg.errors.CheckViolation):
            connection.execute(
                insert,
                (
                    uuid.uuid4(),
                    envelope.run_id,
                    demo.base_sha,
                    state,
                    verdict,
                    reasons,
                    reproducibility,
                ),
            )


def test_an_unknown_repository_is_answered_blocked_and_stored(tmp_path: Path) -> None:
    demo = make_demo(tmp_path)
    envelope = deep_request(demo.base_sha, repository_id="not-in-the-policy")
    accept(envelope)

    drain(worker(tmp_path, demo))  # no model answer is needed: the policy stops the run first

    result = stored(envelope.run_id)
    assert result["request"][2:4] == ("BLOCKED", ["repository_not_configured"])
    assert result["fix"][1:3] == ("BLOCKED", "BLOCKED") and result["fix"][6] is None
    assert result["executions"] == [] and result["calls"] == []
    assert result["events"][0][0]["data"]["reason_codes"] == ["repository_not_configured"]


def test_requests_accepted_before_the_pipeline_was_switched_off_are_answered(
    tmp_path: Path,
) -> None:
    demo = make_demo(tmp_path)
    first, second = deep_request(demo.base_sha), deep_request(demo.base_sha)
    accept(first)
    accept(second)  # both accepted while the pipeline was on; nothing claims them any more

    with intelligence() as connection:
        assert fix_worker.answer_stranded(connection, OFF) >= 2
        assert fix_worker.answer_stranded(connection, OFF) == 0  # once

    for envelope in (first, second):
        result = stored(envelope.run_id)
        assert result["request"][0] == "completed"
        assert result["request"][2:4] == ("BLOCKED", [DEEP_ANALYSIS_NOT_IMPLEMENTED])
        assert result["fix"][1:3] == ("BLOCKED", "BLOCKED")
        (event,) = result["events"]
        CONTRACTS.validate(event[0])
        assert event[0]["data"]["reason_codes"] == [DEEP_ANALYSIS_NOT_IMPLEMENTED]


def test_past_fixes_are_read_by_full_path_under_the_runs_access_scope() -> None:
    from app.defect.sources import PostgresFixSources

    ticket = FakeFixSources().ticket
    assert ticket is not None
    fixes = PostgresFixSources(intelligence, None, ["project:none"])  # type: ignore[arg-type]

    # The query runs on PostgreSQL; a scope with no history has no fixes.
    assert fixes.historical_fixes(ticket, ["src/kiosk/retry_policy.py"]) == []


def test_with_the_fix_pipeline_off_a_deep_request_is_blocked_at_once_and_never_queued(
    tmp_path: Path,
) -> None:
    demo = make_demo(tmp_path)
    envelope = deep_request(demo.base_sha)

    accept(envelope, OFF)

    result = stored(envelope.run_id)
    assert result["request"][0] == "completed"
    assert result["request"][2:4] == ("BLOCKED", [DEEP_ANALYSIS_NOT_IMPLEMENTED])
    assert result["fix"] is None and len(result["events"]) == 1
