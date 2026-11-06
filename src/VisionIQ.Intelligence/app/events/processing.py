"""Idempotent inbox (S03-07): the inbox row and the handler's writes commit in one transaction, so
an event is either fully applied exactly once or not at all (and Pub/Sub redelivers it). A rejected
event keeps its inbox row (with the reason) but none of the handler's writes.
"""

import uuid
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from enum import StrEnum
from typing import Protocol

import psycopg
import structlog

from app.config import Settings
from app.db import connect
from app.events.contracts import ContractRegistry, EventContractError
from app.events.envelope import AnalysisCompletedV1, AnalysisRequestedV1, EventEnvelope, follow_up
from app.events.outbox import enqueue
from app.events.topology import Topology


class EventRejectedError(Exception):
    """A valid event that conflicts with stored state; retrying cannot fix it.

    The push endpoint rejects it so it ends in the dead-letter topic instead of being retried.
    """

    def __init__(self, reason: str) -> None:
        super().__init__(reason)
        self.reason = reason


class Outcome(StrEnum):
    PROCESSED = "processed"
    DUPLICATE = "duplicate"


@dataclass(frozen=True)
class EventContext:
    contracts: ContractRegistry
    topology: Topology


Handler = Callable[[psycopg.Connection, EventEnvelope, EventContext], None]


class EventProcessor(Protocol):
    def process(self, envelope: EventEnvelope) -> Outcome: ...


# Until the BRIEF pipeline exists the honest answer is BLOCKED with a reason, never a fake result.
PIPELINE_NOT_IMPLEMENTED = "analysis_pipeline_not_implemented"


def handle_analysis_requested(
    connection: psycopg.Connection, envelope: EventEnvelope, context: EventContext
) -> None:
    request = AnalysisRequestedV1.model_validate(envelope.data)
    result = AnalysisCompletedV1(
        result_id=uuid.uuid4(),
        result_state="BLOCKED",
        evidence_ids=[],
        advice_ids=[],
        reason_codes=[PIPELINE_NOT_IMPLEMENTED],
    )
    stored = connection.execute(
        "INSERT INTO knowledge.analysis_requests (run_id, event_id, tenant_id, project_id, "
        "ticket_id, ticket_revision, repository_id, base_commit_sha, analysis_mode, "
        "security_scope_id, result_id, result_state, reason_codes) "
        "VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s) "
        "ON CONFLICT (run_id) DO NOTHING RETURNING run_id",
        (
            envelope.run_id,
            envelope.event_id,
            envelope.tenant_id,
            request.project_id,
            request.ticket_id,
            request.ticket_revision,
            request.repository_id,
            request.base_commit_sha,
            request.analysis_mode,
            request.security_scope_id,
            result.result_id,
            result.result_state,
            result.reason_codes,
        ),
    ).fetchone()
    if stored is None:
        # Redeliveries of the same event stop at the inbox, so this is a different event for an
        # answered run.
        raise EventRejectedError("run_already_requested")
    completed = follow_up(envelope, "analysis.completed", result)
    enqueue(connection, completed, context.contracts, context.topology)
    structlog.get_logger().info(
        "analysis_request_accepted",
        result_state=result.result_state,
        analysis_mode=request.analysis_mode,
    )


HANDLERS: Mapping[tuple[str, int], Handler] = {
    ("analysis.requested", 1): handle_analysis_requested,
}


class PostgresEventProcessor:
    def __init__(
        self,
        settings: Settings,
        context: EventContext,
        handlers: Mapping[tuple[str, int], Handler] = HANDLERS,
    ) -> None:
        self._settings = settings
        self._context = context
        self._handlers = handlers

    def process(self, envelope: EventEnvelope) -> Outcome:
        handler = self._handlers.get((envelope.event_type, envelope.schema_version))
        if handler is None:
            raise EventContractError(
                f"{envelope.event_type} v{envelope.schema_version} is not consumed here"
            )
        rejected: EventRejectedError | None = None
        with connect(self._settings) as connection, connection.transaction():
            # A concurrent redelivery blocks on the primary key until this commits, then sees it.
            inserted = connection.execute(
                "INSERT INTO knowledge.event_inbox "
                "(event_id, event_type, schema_version, trace_id) VALUES (%s, %s, %s, %s) "
                "ON CONFLICT (event_id) DO NOTHING RETURNING event_id",
                (
                    envelope.event_id,
                    envelope.event_type,
                    envelope.schema_version,
                    envelope.trace_id,
                ),
            ).fetchone()
            if inserted is None:
                return Outcome.DUPLICATE
            try:
                # Savepoint: a rejection undoes the handler's writes but keeps the inbox row, so a
                # replay of this event (e.g. from the dead-letter topic) is a duplicate.
                with connection.transaction():
                    handler(connection, envelope, self._context)
            except EventRejectedError as error:
                rejected = error
                connection.execute(
                    "UPDATE knowledge.event_inbox SET rejected_reason = %s WHERE event_id = %s",
                    (error.reason, envelope.event_id),
                )
        if rejected is not None:
            raise rejected  # only after the inbox row is committed
        return Outcome.PROCESSED
