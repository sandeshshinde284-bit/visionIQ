"""S03-07/S03-09/S03-10: push delivery semantics and trace propagation, without a database.

The real PostgreSQL inbox is exercised by test_event_reliability_db.py (opt-in).
"""

import base64
import json
from pathlib import Path

import pytest
import structlog
from fastapi.testclient import TestClient

from app.events.contracts import find_contracts_dir
from app.events.envelope import EventEnvelope
from app.events.processing import EventRejectedError, Outcome

FIXTURES = find_contracts_dir() / "fixtures"


class FakeProcessor:
    """Mimics the inbox: first delivery of an event_id is processed, later ones are duplicates."""

    def __init__(self, fail: Exception | None = None) -> None:
        self.applied: list[str] = []
        self.fail = fail

    def process(self, envelope: EventEnvelope) -> Outcome:
        if self.fail:
            raise self.fail
        structlog.get_logger().info("handler_ran")
        if str(envelope.event_id) in self.applied:
            return Outcome.DUPLICATE
        self.applied.append(str(envelope.event_id))
        return Outcome.PROCESSED


def push_body(event: object, attempt: int = 1) -> dict[str, object]:
    data = base64.b64encode(json.dumps(event).encode()).decode()
    return {
        "message": {"data": data, "messageId": "1", "attributes": {}},
        "subscription": "projects/visioniq-local/subscriptions/test",
        "deliveryAttempt": attempt,
    }


def fixture(path: str) -> dict[str, object]:
    return json.loads((FIXTURES / path).read_text(encoding="utf-8"))


@pytest.fixture
def processor(client: TestClient) -> FakeProcessor:
    fake = FakeProcessor()
    client.app.state.event_processor = fake  # type: ignore[attr-defined]
    return fake


def test_duplicate_delivery_is_acked_and_applied_once(
    client: TestClient, processor: FakeProcessor
) -> None:
    event = fixture("valid/analysis.requested.v1.json")

    first = client.post("/events/pubsub", json=push_body(event))
    second = client.post("/events/pubsub", json=push_body(event, attempt=2))

    assert (first.status_code, second.status_code) == (204, 204)
    assert processor.applied == [event["event_id"]]


@pytest.mark.parametrize(
    "path", sorted(p.name for p in (FIXTURES / "invalid").glob("analysis.requested*.json"))
)
def test_contract_violation_is_nacked_for_dead_lettering(
    client: TestClient, processor: FakeProcessor, path: str
) -> None:
    response = client.post("/events/pubsub", json=push_body(fixture(f"invalid/{path}")))

    assert response.status_code == 400
    assert processor.applied == []


@pytest.mark.parametrize(
    "body",
    [{}, {"message": {}}, {"message": {"data": "not base64!"}}, {"message": {"data": "e30="}}],
)
def test_malformed_push_is_rejected(
    client: TestClient, processor: FakeProcessor, body: dict[str, object]
) -> None:
    assert client.post("/events/pubsub", json=body).status_code == 400


def test_transient_failure_asks_pubsub_to_retry(client: TestClient) -> None:
    client.app.state.event_processor = FakeProcessor(fail=OSError("db down"))  # type: ignore[attr-defined]

    response = client.post(
        "/events/pubsub", json=push_body(fixture("valid/analysis.requested.v1.json"))
    )

    assert response.status_code == 503


def test_conflicting_event_is_rejected_for_dead_lettering(client: TestClient) -> None:
    client.app.state.event_processor = FakeProcessor(  # type: ignore[attr-defined]
        fail=EventRejectedError("run_already_requested")
    )

    response = client.post(
        "/events/pubsub", json=push_body(fixture("valid/analysis.requested.v1.json"))
    )

    assert response.status_code == 400
    assert response.json() == {"status": "rejected", "reason": "run_already_requested"}


def test_every_log_line_of_an_event_carries_its_trace_id(
    client: TestClient, processor: FakeProcessor, capsys: pytest.CaptureFixture[str]
) -> None:
    event = fixture("valid/analysis.requested.v1.json")
    capsys.readouterr()

    client.post("/events/pubsub", json=push_body(event))

    lines = [
        json.loads(line) for line in capsys.readouterr().out.splitlines() if line.startswith("{")
    ]
    by_event = {line["event"]: line for line in lines}
    for name in ("event_received", "handler_ran", "event_processed"):
        assert by_event[name]["trace_id"] == event["trace_id"]
        assert by_event[name]["run_id"] == event["run_id"]
    assert "VisionIQ-Demo" not in json.dumps(lines)  # payload values are never logged


def test_contracts_dir_is_shipped_next_to_the_app() -> None:
    dockerfile = Path(__file__).resolve().parents[2] / "src/VisionIQ.Intelligence/Dockerfile"
    assert "COPY contracts/events" in dockerfile.read_text(encoding="utf-8")
