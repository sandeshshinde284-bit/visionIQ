"""S03-07: inbox transaction boundaries with a fake connection (real SQL: the opt-in DB test)."""

import json
from collections.abc import Iterator
from contextlib import contextmanager
from typing import Any

import psycopg
import pytest

from app.events import processing
from app.events.contracts import find_contracts_dir
from app.events.envelope import EventEnvelope
from app.events.processing import (
    EventContext,
    EventRejectedError,
    Outcome,
    PostgresEventProcessor,
)

EVENT = EventEnvelope.model_validate(
    json.loads(
        (find_contracts_dir() / "fixtures/valid/analysis.requested.v1.json").read_text("utf-8")
    )
)


class FakeConnection:
    """Logs statements and how each (nested) transaction ended: commit or rollback."""

    def __init__(self, seen: bool = False) -> None:
        self.seen = seen
        self.log: list[str] = []

    def __enter__(self) -> "FakeConnection":
        return self

    def __exit__(self, *_: object) -> None:
        pass

    @contextmanager
    def transaction(self) -> Iterator[None]:
        self.log.append("begin")
        try:
            yield
        except BaseException:
            self.log.append("rollback")
            raise
        self.log.append("commit")

    def execute(self, sql: str, params: tuple[Any, ...]) -> "FakeConnection":
        self.log.append(sql.split()[0] + (" rejected_reason" if "rejected_reason" in sql else ""))
        return self

    def fetchone(self) -> tuple[Any, ...] | None:
        return None if self.seen else (EVENT.event_id,)


def _process(
    monkeypatch: pytest.MonkeyPatch,
    connection: FakeConnection,
    handler: processing.Handler,
) -> Outcome:
    monkeypatch.setattr(processing, "connect", lambda _settings: connection)
    processor = PostgresEventProcessor(
        None,  # type: ignore[arg-type]
        EventContext(None, None),  # type: ignore[arg-type]
        {("analysis.requested", 1): handler},
    )
    return processor.process(EVENT)


def _reject(connection: psycopg.Connection, envelope: EventEnvelope, context: EventContext) -> None:
    connection.execute("INSERT INTO knowledge.analysis_requests ...", ())
    raise EventRejectedError("run_already_requested")


def test_rejected_event_keeps_its_inbox_row_but_not_the_handler_writes(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    connection = FakeConnection()

    with pytest.raises(EventRejectedError, match="run_already_requested"):
        _process(monkeypatch, connection, _reject)

    assert connection.log == [
        "begin",
        "INSERT",  # inbox row
        "begin",
        "INSERT",  # handler write, inside the savepoint
        "rollback",  # savepoint undone
        "UPDATE rejected_reason",
        "commit",  # the inbox row is kept, so a replay is a duplicate
    ]


def test_seen_event_is_a_duplicate_and_skips_the_handler(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    connection = FakeConnection(seen=True)

    assert _process(monkeypatch, connection, _reject) is Outcome.DUPLICATE
    assert connection.log == ["begin", "INSERT", "commit"]
