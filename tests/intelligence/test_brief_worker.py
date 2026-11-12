"""BRF-03: accepting a request, answering it once, and the committed brief schema (S10-04)."""

import json
import uuid
from collections.abc import Iterator
from contextlib import contextmanager
from datetime import UTC, datetime
from typing import Any

import pytest
from brief_support import GOOD_DRAFT, FakeSources, ScriptedProvider, gateway_for, offline_gateway
from jsonschema import Draft202012Validator

from app.brief import schema, worker
from app.brief.pipeline import BriefRequest, BriefSettings
from app.brief.worker import (
    AnalysisWorker,
    ClaimedRequest,
    failed_brief,
    retry_delay_seconds,
    store,
)
from app.events import processing
from app.events.contracts import get_contracts
from app.events.envelope import EventEnvelope
from app.events.processing import EventContext, EventRejectedError, handle_analysis_requested
from app.events.topology import load_topology

CONTRACTS = get_contracts()
FIXTURE = json.loads(
    (CONTRACTS.directory / "fixtures/valid/analysis.requested.v1.json").read_text("utf-8")
)


def context(enabled: bool) -> EventContext:
    return EventContext(CONTRACTS, load_topology(CONTRACTS.directory), enabled)


def requested(mode: str = "brief") -> EventEnvelope:
    event = json.loads(json.dumps(FIXTURE))
    event["data"]["analysis_mode"] = mode
    return EventEnvelope.model_validate(event)


class FakeConnection:
    """Records statements; answers RETURNING queries from ``returning``."""

    def __init__(self, returning: Any = ("row",)) -> None:
        self.returning = returning
        self.statements: list[tuple[str, tuple]] = []
        self.transactions = 0

    def __enter__(self) -> "FakeConnection":
        return self

    def __exit__(self, *_: object) -> None:
        pass

    @contextmanager
    def transaction(self) -> Iterator[None]:
        self.transactions += 1
        yield

    def execute(self, sql: str, params: Any = ()) -> "FakeConnection":
        self.statements.append((" ".join(sql.split()), tuple(params) if params else ()))
        return self

    def fetchone(self) -> Any:
        return self.returning

    def fetchall(self) -> list:
        return []

    def tables(self) -> list[str]:
        return [sql.split()[2] for sql, _ in self.statements if sql.startswith("INSERT")]


# ---- The push handler: accept fast, answer later ---------------------------------------------


def test_with_the_pipeline_on_a_brief_request_is_queued_and_not_answered_yet() -> None:
    connection = FakeConnection()
    envelope = requested()

    handle_analysis_requested(connection, envelope, context(enabled=True))

    assert connection.tables() == ["knowledge.analysis_requests"]  # no analysis.completed yet
    params = connection.statements[0][1]
    assert params[0] == envelope.run_id and params[10] == "pending"
    assert params[11].obj["event_id"] == str(envelope.event_id)  # kept to build the reply later
    assert params[12:] == (None, None, [], None)


@pytest.mark.parametrize(
    ("enabled", "mode", "reason"),
    [
        (False, "brief", processing.PIPELINE_DISABLED),
        (True, "deep", processing.DEEP_ANALYSIS_NOT_IMPLEMENTED),
    ],
)
def test_what_cannot_be_analysed_is_answered_blocked_at_once(enabled, mode, reason) -> None:
    connection = FakeConnection()

    handle_analysis_requested(connection, requested(mode), context(enabled))

    assert connection.tables() == ["knowledge.analysis_requests", "knowledge.event_outbox"]
    params = connection.statements[0][1]
    assert (params[10], params[13], params[14]) == ("completed", "BLOCKED", [reason])
    completed = connection.statements[1][1][-1].obj
    assert completed["event_type"] == "analysis.completed"
    assert completed["data"]["reason_codes"] == [reason] and completed["data"]["advice_ids"] == []


def test_a_second_request_for_an_accepted_run_is_rejected() -> None:
    with pytest.raises(EventRejectedError, match="run_already_requested"):
        handle_analysis_requested(FakeConnection(returning=None), requested(), context(True))


# ---- The worker: one stored answer per run ---------------------------------------------------


def claimed(attempt: int = 1) -> ClaimedRequest:
    envelope = requested()
    return ClaimedRequest(
        BriefRequest(envelope.run_id, "VisionIQ-Demo", "4711", 12),
        "project:VisionIQ-Demo",
        "visioniq-dev",
        envelope,
        datetime(2026, 11, 10, 9, 0, tzinfo=UTC),
        attempt,
    )


def run_worker(gateway, connection: FakeConnection, attempt: int = 1, sources=None) -> None:
    job = AnalysisWorker(
        lambda: connection,
        lambda scope: sources or FakeSources(),
        gateway,
        BriefSettings(),
        context(True),
    )
    job._process(claimed(attempt))


def test_the_brief_its_advice_the_call_log_and_the_reply_are_stored_in_one_transaction() -> None:
    connection = FakeConnection()

    run_worker(gateway_for(ScriptedProvider("gemini", [GOOD_DRAFT])), connection)

    assert connection.transactions == 1
    assert connection.statements[0][0].startswith("UPDATE knowledge.analysis_requests SET status")
    assert connection.tables() == [
        "knowledge.briefs",
        "knowledge.brief_advice",
        "knowledge.brief_advice",
        "knowledge.llm_calls",
        "knowledge.event_outbox",
    ]
    brief_row = next(p for sql, p in connection.statements if "INTO knowledge.briefs" in sql)
    document = brief_row[10].obj
    assert list(Draft202012Validator(schema.json_schema()).iter_errors(document)) == []
    assert brief_row[13] == datetime(2026, 11, 10, 9, 0, tzinfo=UTC)  # accepted_at, for BRF-07
    call = next(p for sql, p in connection.statements if "INTO knowledge.llm_calls" in sql)
    assert call[2:9] == (
        "brief_draft",
        "primary",
        "gemini",
        "scripted-1",
        None,
        "brief.defect@v1",
        "routing-2026-11-10.2",
    )
    completed = connection.statements[-1][1][-1].obj
    CONTRACTS.validate(completed)  # the reply matches analysis.completed v1
    assert completed["data"]["result_state"] == "PUBLISHED"
    assert completed["data"]["advice_ids"] == ["ADV-001", "ADV-002"]
    assert completed["data"]["evidence_ids"] == ["E1", "E2"]
    assert completed["data"]["result_id"] == document["brief_id"]
    assert (completed["run_id"], completed["trace_id"]) == (FIXTURE["run_id"], FIXTURE["trace_id"])


def test_a_run_that_was_already_answered_stores_nothing_twice() -> None:
    connection = FakeConnection(
        returning=None
    )  # the UPDATE ... WHERE status = 'pending' hit no row
    outcome = failed_brief(claimed())

    assert store(connection, claimed(), outcome, context(True)) is False
    assert connection.tables() == []


class Broken(FakeSources):
    def load_ticket(self, project_id: str, ticket_id: str):
        raise TimeoutError("database password=hunter2 unreachable")


def test_a_failing_run_is_retried_then_answered_blocked_instead_of_hanging(caplog) -> None:
    first, last = FakeConnection(), FakeConnection()

    run_worker(offline_gateway(), first, attempt=1, sources=Broken())
    run_worker(offline_gateway(), last, attempt=worker.MAX_ATTEMPTS, sources=Broken())

    (deferred,) = first.statements
    assert deferred[0].startswith("UPDATE knowledge.analysis_requests SET available_at")
    assert deferred[1][0] == retry_delay_seconds(1) == 15
    completed = last.statements[-1][1][-1].obj
    assert completed["data"]["result_state"] == "BLOCKED"
    assert completed["data"]["reason_codes"] == [worker.ANALYSIS_FAILED]
    assert "hunter2" not in str(last.statements)  # the error type is logged, never its text


def test_retry_delays_double_up_to_five_minutes() -> None:
    assert [retry_delay_seconds(n) for n in (1, 2, 3, 6, 9)] == [15, 30, 60, 300, 300]


# ---- S10-04: the committed JSON schema is the model -----------------------------------------


def test_the_committed_brief_schema_matches_the_model() -> None:
    committed = schema.find_schema_file().read_text(encoding="utf-8")

    assert committed == schema.schema_text(), "run `python -m app.brief.schema` and commit the file"


def test_the_schema_requires_ids_states_and_sources() -> None:
    validator = Draft202012Validator(schema.json_schema())
    brief = json.loads(failed_brief(claimed()).brief.model_dump_json())
    assert list(validator.iter_errors(brief)) == []

    for change in (
        {"state": "DONE"},
        {"risk": {**brief["risk"], "band": "VERY_HIGH"}},
        {
            "advice": [
                {
                    "advice_id": "1",
                    "kind": "DO",
                    "statement": "x",
                    "evidence_ids": [],
                    "support": [],
                }
            ]
        },
        {"surprise": True},
    ):
        assert list(validator.iter_errors({**brief, **change})), change
    assert uuid.UUID(brief["brief_id"]) and brief["schema_version"] == 1
