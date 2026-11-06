"""FND-05 completion evidence against real PostgreSQL (opt-in, after `python -m db.manage all`).

The same analysis.requested event delivered twice produces one stored request, one result and one
outbound analysis.completed event, all carrying the caller's trace ID. A publish failure leaves the
event in the outbox for a later retry instead of losing it.
"""

import copy
import json
import os
import uuid
from collections.abc import Iterator

import pytest

from app.config import Settings
from app.db import connect
from app.events.contracts import ContractRegistry, find_contracts_dir
from app.events.envelope import EventEnvelope
from app.events.outbox import dispatch_pending
from app.events.processing import (
    EventContext,
    EventRejectedError,
    Outcome,
    PostgresEventProcessor,
)
from app.events.topology import load_topology

pytestmark = pytest.mark.skipif(
    os.getenv("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)

CONTRACTS = find_contracts_dir()


@pytest.fixture
def settings() -> Settings:
    return Settings()  # the developer's .env selects the local database and runtime identity


@pytest.fixture
def envelope(settings: Settings) -> Iterator[EventEnvelope]:
    raw = json.loads((CONTRACTS / "fixtures/valid/analysis.requested.v1.json").read_text("utf-8"))
    event = copy.deepcopy(raw)
    event["event_id"] = str(uuid.uuid4())
    event["run_id"] = str(uuid.uuid4())
    event["trace_id"] = uuid.uuid4().hex
    yield EventEnvelope.model_validate(event)


def _processor(settings: Settings) -> PostgresEventProcessor:
    contracts = ContractRegistry(CONTRACTS)
    return PostgresEventProcessor(settings, EventContext(contracts, load_topology(CONTRACTS)))


def test_same_event_twice_stores_one_result(settings: Settings, envelope: EventEnvelope) -> None:
    processor = _processor(settings)

    assert processor.process(envelope) is Outcome.PROCESSED
    assert processor.process(envelope) is Outcome.DUPLICATE

    with connect(settings) as connection:
        requests = connection.execute(
            "SELECT count(*) FROM knowledge.analysis_requests WHERE run_id = %s",
            (envelope.run_id,),
        ).fetchone()
        outbox = connection.execute(
            "SELECT payload FROM knowledge.event_outbox WHERE payload->>'run_id' = %s",
            (str(envelope.run_id),),
        ).fetchall()
    assert requests == (1,)
    assert len(outbox) == 1
    completed = outbox[0][0]
    assert completed["event_type"] == "analysis.completed"
    assert completed["trace_id"] == envelope.trace_id
    assert completed["data"]["result_state"] == "BLOCKED"


class FlakyPublisher:
    def __init__(self) -> None:
        self.sent: list[dict[str, object]] = []
        self.fail = True

    def publish(self, topic: str, data: bytes, attributes: dict[str, str]) -> None:
        if self.fail:
            raise ConnectionError("broker unavailable")
        self.sent.append(json.loads(data))


def test_publish_failure_keeps_the_event_for_retry(
    settings: Settings, envelope: EventEnvelope
) -> None:
    _processor(settings).process(envelope)
    publisher = FlakyPublisher()

    with connect(settings) as connection:
        dispatch_pending(connection, publisher, batch_size=1000)
        row = connection.execute(
            "SELECT published_at, attempt_count, last_error_code FROM knowledge.event_outbox "
            "WHERE payload->>'run_id' = %s",
            (str(envelope.run_id),),
        ).fetchone()
        assert row is not None
        assert row[0] is None and row[1] == 1 and row[2] == "ConnectionError"

        publisher.fail = False
        connection.execute(
            "UPDATE knowledge.event_outbox SET available_at = now() WHERE payload->>'run_id' = %s",
            (str(envelope.run_id),),
        )
        dispatch_pending(connection, publisher, batch_size=1000)
    assert [e["run_id"] for e in publisher.sent].count(str(envelope.run_id)) == 1


def test_rejected_event_is_a_duplicate_on_replay(
    settings: Settings, envelope: EventEnvelope
) -> None:
    processor = _processor(settings)
    processor.process(envelope)
    second = envelope.model_copy(update={"event_id": uuid.uuid4()})  # same run, new event

    with pytest.raises(EventRejectedError, match="run_already_requested"):
        processor.process(second)
    assert processor.process(second) is Outcome.DUPLICATE  # e.g. replayed from the DLQ

    with connect(settings) as connection:
        inbox = connection.execute(
            "SELECT rejected_reason FROM knowledge.event_inbox WHERE event_id = %s",
            (second.event_id,),
        ).fetchone()
        outbox = connection.execute(
            "SELECT count(*) FROM knowledge.event_outbox WHERE payload->>'run_id' = %s",
            (str(envelope.run_id),),
        ).fetchone()
    assert inbox == ("run_already_requested",)
    assert outbox == (1,)  # the rejected event enqueued nothing
