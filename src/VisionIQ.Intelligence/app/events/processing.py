"""Idempotent inbox (S03-07): the inbox row and the handler's writes commit in one transaction, so
an event is either fully applied exactly once or not at all (and Pub/Sub redelivers it). A rejected
event keeps its inbox row (with the reason) but none of the handler's writes.
"""

import uuid
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from datetime import UTC, datetime
from enum import StrEnum
from typing import Protocol

import psycopg
import structlog
from psycopg.types.json import Jsonb

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
    # BRIEF_PIPELINE_ENABLED: accepted brief requests are answered by the analysis worker.
    brief_pipeline_enabled: bool = False
    # DEFECT_PIPELINE_ENABLED: accepted deep requests are answered by the fix worker (DEF-01).
    defect_pipeline_enabled: bool = False


Handler = Callable[[psycopg.Connection, EventEnvelope, EventContext], None]


class EventProcessor(Protocol):
    def process(self, envelope: EventEnvelope) -> Outcome: ...


# With a pipeline switched off the honest answer is BLOCKED with a reason, never a fake result.
# Deep analysis is the defect fix pipeline (app/defect); it is answered only when that is on.
PIPELINE_DISABLED = "analysis_pipeline_disabled"
DEEP_ANALYSIS_NOT_IMPLEMENTED = "deep_analysis_not_implemented"

_INSERT_REQUEST = (
    "INSERT INTO knowledge.analysis_requests (run_id, event_id, tenant_id, project_id, "
    "ticket_id, ticket_revision, repository_id, base_commit_sha, analysis_mode, "
    "security_scope_id, status, request_event, result_id, result_state, reason_codes, "
    "completed_at) "
    "VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s) "
    "ON CONFLICT (run_id) DO NOTHING RETURNING run_id"
)


def handle_analysis_requested(
    connection: psycopg.Connection, envelope: EventEnvelope, context: EventContext
) -> None:
    """Accept a request. A brief is queued for the analysis worker (BRF-03) and a deep request
    for the fix worker (DEF-01); each answers with ``analysis.completed``. Anything this service
    is not set up to do is answered BLOCKED now."""
    request = AnalysisRequestedV1.model_validate(envelope.data)
    if request.analysis_mode != "brief":
        blocked: str | None = (
            None if context.defect_pipeline_enabled else DEEP_ANALYSIS_NOT_IMPLEMENTED
        )
    else:
        blocked = None if context.brief_pipeline_enabled else PIPELINE_DISABLED
    result = (
        None
        if blocked is None
        else AnalysisCompletedV1(
            result_id=uuid.uuid4(),
            result_state="BLOCKED",
            evidence_ids=[],
            advice_ids=[],
            reason_codes=[blocked],
        )
    )
    stored = connection.execute(
        _INSERT_REQUEST,
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
            "pending" if result is None else "completed",
            # Ids only (no ticket text): the worker builds analysis.completed from it.
            Jsonb(envelope.to_json_dict()),
            result.result_id if result else None,
            result.result_state if result else None,
            result.reason_codes if result else [],
            datetime.now(UTC) if result else None,
        ),
    ).fetchone()
    if stored is None:
        # Redeliveries of the same event stop at the inbox, so this is a different event for a
        # run that was already accepted.
        raise EventRejectedError("run_already_requested")
    if result is not None:
        completed = follow_up(envelope, "analysis.completed", result)
        enqueue(connection, completed, context.contracts, context.topology)
    structlog.get_logger().info(
        "analysis_request_accepted",
        result_state=result.result_state if result else "PENDING",
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
