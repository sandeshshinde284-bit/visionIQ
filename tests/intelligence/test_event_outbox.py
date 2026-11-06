"""S03-08: outbox dispatch decisions with a fake connection (real SQL: the opt-in DB test)."""

import json
import uuid
from contextlib import nullcontext
from typing import Any

from app.events.contracts import find_contracts_dir
from app.events.outbox import (
    MAX_PUBLISH_ATTEMPTS,
    dispatch_pending,
    error_code,
    retry_delay_seconds,
)

VALID = json.loads(
    (find_contracts_dir() / "fixtures/valid/analysis.completed.v1.json").read_text("utf-8")
)


class FakeConnection:
    """Records each row outcome and the transaction it was committed in."""

    def __init__(self, rows: list[tuple[Any, ...]]) -> None:
        self.rows = rows
        self.updates: list[tuple[str, tuple[Any, ...]]] = []
        self.transactions = 0

    def transaction(self) -> nullcontext[None]:
        self.transactions += 1
        return nullcontext()

    def execute(self, sql: str, params: tuple[Any, ...]) -> "FakeConnection":
        if sql.startswith("UPDATE") and "RETURNING" not in sql:  # skip the claim
            self.updates.append((sql, params))
        return self

    def fetchall(self) -> list[tuple[Any, ...]]:
        return self.rows


class RecordingPublisher:
    def __init__(self, fail: bool = False) -> None:
        self.sent: list[dict[str, str]] = []
        self.fail = fail

    def publish(self, topic: str, data: bytes, attributes: dict[str, str]) -> None:
        if self.fail:
            raise ConnectionError("broker unavailable")
        self.sent.append(attributes)


def test_unpublishable_row_is_parked_and_does_not_block_later_rows() -> None:
    broken, good = uuid.uuid4(), uuid.uuid4()
    connection = FakeConnection(
        [
            (broken, "visioniq-analysis-completed-v1", {"event_type": "analysis.completed"}, 0),
            (good, "visioniq-analysis-completed-v1", VALID, 0),
        ]
    )
    publisher = RecordingPublisher()

    sent = dispatch_pending(connection, publisher)  # type: ignore[arg-type]

    assert sent == 1
    assert [a["event_id"] for a in publisher.sent] == [VALID["event_id"]]
    assert [params for _, params in connection.updates] == [
        ("UnpublishablePayload", broken),  # parked, out of the queue
        (good,),  # marked published
    ]
    assert "parked_at = now()" in connection.updates[0][0]
    # The claim and each outcome commit separately, so a later failure cannot undo an earlier one.
    assert connection.transactions == 3


def test_publish_failure_backs_off_exponentially() -> None:
    row = uuid.uuid4()
    connection = FakeConnection([(row, "visioniq-analysis-completed-v1", VALID, 3)])

    sent = dispatch_pending(connection, RecordingPublisher(fail=True))  # type: ignore[arg-type]

    assert sent == 0
    assert connection.updates[-1][1] == ("ConnectionError", 40, row)


class Http2Error(Exception):
    pass


def test_error_code_keeps_digits_and_drops_other_characters() -> None:
    assert error_code(Http2Error()) == "Http2Error"
    weird = type("Bad-Name!", (Exception,), {})
    assert error_code(weird()) == "BadName"


def test_publish_failure_parks_the_row_once_attempts_are_used_up() -> None:
    row = uuid.uuid4()
    last = MAX_PUBLISH_ATTEMPTS - 1  # failures before this one
    connection = FakeConnection([(row, "visioniq-analysis-completed-v1", VALID, last)])

    sent = dispatch_pending(connection, RecordingPublisher(fail=True))  # type: ignore[arg-type]

    assert sent == 0
    sql, params = connection.updates[-1]
    assert "parked_at = now()" in sql
    assert params == ("ConnectionError", row)


def test_retry_delay_backs_off_then_gives_up() -> None:
    delays = [retry_delay_seconds(attempts) for attempts in range(MAX_PUBLISH_ATTEMPTS)]

    assert delays[:4] == [5, 10, 20, 40]
    assert max(d for d in delays if d is not None) == 600
    assert delays[-1] is None
    assert delays.count(None) == 1
