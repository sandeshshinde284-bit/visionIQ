"""Typed view of the shared event envelope and the event payloads this service handles (S03-06).

The JSON Schemas are the source of truth; these models only give handlers typed access after an
event has passed schema validation.
"""

import uuid
from collections.abc import Iterator
from contextlib import contextmanager
from datetime import UTC, datetime
from typing import Any, Literal

import structlog
from pydantic import BaseModel, ConfigDict

PRODUCER = "VisionIQ.Intelligence"


class EventEnvelope(BaseModel):
    model_config = ConfigDict(frozen=True, extra="forbid")

    event_id: uuid.UUID
    event_type: str
    schema_version: int
    occurred_at: datetime
    producer: str
    tenant_id: str
    correlation_id: str
    trace_id: str
    run_id: uuid.UUID | None
    source_revision: str | None
    data: dict[str, Any]

    def to_json_dict(self) -> dict[str, Any]:
        return self.model_dump(mode="json")

    @contextmanager
    def logging_scope(self) -> Iterator[None]:
        """Bind the correlation fields so every log line of this event carries them."""
        with structlog.contextvars.bound_contextvars(
            trace_id=self.trace_id,
            correlation_id=self.correlation_id,
            run_id=str(self.run_id) if self.run_id else None,
            event_id=str(self.event_id),
            event_type=self.event_type,
        ):
            yield


class AnalysisRequestedV1(BaseModel):
    model_config = ConfigDict(frozen=True, extra="forbid")

    project_id: str
    ticket_id: str
    ticket_revision: int
    repository_id: str
    base_commit_sha: str
    analysis_mode: Literal["brief", "deep"]
    security_scope_id: str


class AnalysisCompletedV1(BaseModel):
    model_config = ConfigDict(frozen=True, extra="forbid")

    result_id: uuid.UUID
    result_state: Literal["PUBLISHED", "PARTIAL", "BLOCKED"]
    evidence_ids: list[str]
    advice_ids: list[str]
    reason_codes: list[str]


def follow_up(cause: EventEnvelope, event_type: str, data: BaseModel) -> EventEnvelope:
    """A new event caused by ``cause``: same tenant, correlation, trace, run and source revision."""
    return EventEnvelope(
        event_id=uuid.uuid4(),
        event_type=event_type,
        schema_version=1,
        occurred_at=datetime.now(UTC),
        producer=PRODUCER,
        tenant_id=cause.tenant_id,
        correlation_id=cause.correlation_id,
        trace_id=cause.trace_id,
        run_id=cause.run_id,
        source_revision=cause.source_revision,
        data=data.model_dump(mode="json"),
    )
