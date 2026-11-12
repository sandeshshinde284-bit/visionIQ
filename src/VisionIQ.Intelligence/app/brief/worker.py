"""The analysis worker: answers accepted ``analysis.requested`` events with a stored brief.

The push endpoint only stores the request (so Pub/Sub is acknowledged in milliseconds); this
worker does the slow part. A request is leased while it is processed, so replicas never work on
the same run. The brief, its advice, the model-call log and the ``analysis.completed`` event are
written in ONE transaction: either the run is answered and its event queued, or nothing happened
and the lease runs out. A run that keeps failing is answered BLOCKED instead of hanging forever.
"""

from __future__ import annotations

import asyncio
import uuid
from collections.abc import Callable
from dataclasses import dataclass
from datetime import datetime

import psycopg
import structlog
from psycopg.types.json import Jsonb

from app.brief.history import BriefSources
from app.brief.pipeline import BriefOutcome, BriefRequest, BriefSettings, build_brief
from app.brief.schema import Brief, Generation, Risk, RiskFactor, TicketRef, Timings
from app.events.envelope import AnalysisCompletedV1, EventEnvelope, follow_up
from app.events.outbox import enqueue
from app.events.processing import EventContext
from app.llm.gateway import Budget, ModelGateway, RunLedger
from app.llm.routing import ROUTING_REVISION

# Long enough for the whole model budget plus retrieval; a crashed worker's run is retried after.
LEASE_SECONDS = 300
# Six attempts with 15 s doubling to 4 min: a run survives an outage of about 8 minutes
# (a database failover, a provider incident) before it is answered BLOCKED.
MAX_ATTEMPTS = 6
ANALYSIS_FAILED = "analysis_failed"

CLAIM_SQL = """
UPDATE knowledge.analysis_requests AS q
SET available_at = now() + make_interval(secs => %(lease)s), attempt_count = q.attempt_count + 1
FROM (SELECT run_id FROM knowledge.analysis_requests
      WHERE status = 'pending' AND analysis_mode = 'brief' AND available_at <= now()
      ORDER BY available_at LIMIT %(batch)s FOR UPDATE SKIP LOCKED) AS due
WHERE q.run_id = due.run_id
RETURNING q.run_id, q.project_id, q.ticket_id, q.ticket_revision, q.security_scope_id,
          q.tenant_id, q.request_event, q.received_at, q.attempt_count
"""


@dataclass(frozen=True)
class ClaimedRequest:
    request: BriefRequest
    security_scope_id: str
    tenant_id: str
    event: EventEnvelope
    accepted_at: datetime
    attempt: int


def claim(connection: psycopg.Connection, batch: int = 1) -> list[ClaimedRequest]:
    """Lease due requests. One at a time by default: the lease then always outlasts the run
    it covers, so a second replica never starts the same run's model calls."""
    with connection.transaction():
        rows = connection.execute(CLAIM_SQL, {"lease": LEASE_SECONDS, "batch": batch}).fetchall()
    return [
        ClaimedRequest(
            BriefRequest(run_id, project_id, ticket_id, revision),
            scope,
            tenant,
            EventEnvelope.model_validate(event),
            accepted_at,
            attempt,
        )
        for (
            run_id,
            project_id,
            ticket_id,
            revision,
            scope,
            tenant,
            event,
            accepted_at,
            attempt,
        ) in rows
    ]


def store(
    connection: psycopg.Connection,
    claimed: ClaimedRequest,
    outcome: BriefOutcome,
    context: EventContext,
) -> bool:
    """Persist the brief and queue analysis.completed, once. False if already answered."""
    brief = outcome.brief
    with connection.transaction():
        answered = connection.execute(
            "UPDATE knowledge.analysis_requests SET status = 'completed', result_id = %s, "
            "result_state = %s, reason_codes = %s, completed_at = now() "
            "WHERE run_id = %s AND status = 'pending' RETURNING run_id",
            (brief.brief_id, brief.state, brief.reason_codes, brief.run_id),
        ).fetchone()
        if answered is None:
            return False
        connection.execute(
            "INSERT INTO knowledge.briefs (brief_id, run_id, tenant_id, project_id, ticket_id, "
            "ticket_revision, state, reason_codes, risk_band, schema_version, document, "
            "snapshot_id, routing_revision, accepted_at, retrieval_ms, generation_ms, total_ms) "
            "VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)",
            (
                brief.brief_id,
                brief.run_id,
                claimed.tenant_id,
                brief.ticket.project,
                brief.ticket.id,
                brief.ticket.revision,
                brief.state,
                brief.reason_codes,
                brief.risk.band,
                brief.schema_version,
                Jsonb(brief.model_dump(mode="json")),
                brief.snapshot_id,
                brief.generation.routing_revision,
                claimed.accepted_at,
                brief.timings.retrieval_ms,
                brief.timings.generation_ms,
                brief.timings.total_ms,
            ),
        )
        for item in brief.advice:
            connection.execute(
                "INSERT INTO knowledge.brief_advice (brief_id, advice_id, kind, statement, "
                "evidence_ids, evidence_state) VALUES (%s, %s, %s, %s, %s, %s)",
                (
                    brief.brief_id,
                    item.advice_id,
                    item.kind,
                    item.statement,
                    item.evidence_ids,
                    item.evidence_state,
                ),
            )
        for call in outcome.ledger.calls:
            result = call.result
            connection.execute(
                "INSERT INTO knowledge.llm_calls (invocation_id, run_id, task, role, provider, "
                "model_id, model_version, prompt_template_version, routing_revision, "
                "data_classification, evidence_snapshot_id, status, error_code, input_tokens, "
                "output_tokens, elapsed_ms) "
                "VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)",
                (
                    result.invocation_id,
                    brief.run_id,
                    call.task.value,
                    call.role,
                    result.provider,
                    result.model_id,
                    result.model_version,
                    result.prompt_template_version,
                    ROUTING_REVISION,
                    call.classification.value,
                    result.evidence_snapshot_id,
                    result.status,
                    result.error_code,
                    result.input_tokens,
                    result.output_tokens,
                    result.elapsed_ms,
                ),
            )
        completed = AnalysisCompletedV1(
            result_id=brief.brief_id,
            result_state=brief.state,
            evidence_ids=brief.evidence_ids(),
            advice_ids=brief.advice_ids(),
            reason_codes=brief.reason_codes,
        )
        enqueue(
            connection,
            follow_up(claimed.event, "analysis.completed", completed),
            context.contracts,
            context.topology,
        )
    return True


def failed_brief(claimed: ClaimedRequest) -> BriefOutcome:
    """The honest answer for a run the pipeline could not complete: BLOCKED, with the reason."""
    request = claimed.request
    brief = Brief(
        brief_id=uuid.uuid4(),
        run_id=request.run_id,
        ticket=TicketRef(
            project=request.project_id, id=request.ticket_id, revision=request.ticket_revision
        ),
        state="BLOCKED",
        reason_codes=[ANALYSIS_FAILED],
        risk=Risk(
            band="INSUFFICIENT_DATA",
            explanation="The analysis did not complete.",
            factors=[
                RiskFactor(
                    code="recent_fixes", label="Defect fixes in scope", value=None, missing=True
                )
            ],
        ),
        limitations=["The analysis failed repeatedly and was stopped. No advice was produced."],
        generation=Generation(
            provider=None,
            model_id=None,
            prompt_template_version=None,
            routing_revision=ROUTING_REVISION,
        ),
        timings=Timings(retrieval_ms=0, generation_ms=0, total_ms=0),
    )
    return BriefOutcome(
        brief, RunLedger(request.run_id, BriefSettings().classification, Budget(0, 0, 0))
    )


def retry_delay_seconds(attempt: int) -> int:
    return min(300, 15 * 2 ** (attempt - 1))


class AnalysisWorker:
    """Processes claimed requests one at a time. ``sources_for`` binds reads to a run's scope."""

    def __init__(
        self,
        connect: Callable[[], psycopg.Connection],
        sources_for: Callable[[str], BriefSources],
        gateway: ModelGateway,
        settings: BriefSettings,
        context: EventContext,
    ) -> None:
        self._connect = connect
        self._sources_for = sources_for
        self._gateway = gateway
        self._settings = settings
        self._context = context

    def run_once(self) -> int:
        """Claim and answer due requests; returns how many were claimed."""
        with self._connect() as connection:
            claimed = claim(connection)
        for item in claimed:
            self._process(item)
        return len(claimed)

    def _process(self, claimed: ClaimedRequest) -> None:
        log = structlog.get_logger()
        with claimed.event.logging_scope():
            try:
                outcome = build_brief(
                    claimed.request,
                    self._sources_for(claimed.security_scope_id),
                    self._gateway,
                    self._settings,
                )
            except Exception as error:
                # Type only: database and provider messages can carry connection details.
                if claimed.attempt < MAX_ATTEMPTS:
                    delay = retry_delay_seconds(claimed.attempt)
                    with self._connect() as connection, connection.transaction():
                        connection.execute(
                            "UPDATE knowledge.analysis_requests SET available_at = now() + "
                            "make_interval(secs => %s) WHERE run_id = %s AND status = 'pending'",
                            (delay, claimed.request.run_id),
                        )
                    log.warning(
                        "analysis_deferred",
                        error_type=type(error).__name__,
                        attempt=claimed.attempt,
                        retry_in_seconds=delay,
                    )
                    return
                log.error(
                    "analysis_gave_up", error_type=type(error).__name__, attempt=claimed.attempt
                )
                outcome = failed_brief(claimed)
            try:
                with self._connect() as connection:
                    stored = store(connection, claimed, outcome, self._context)
            except Exception as error:
                # The lease runs out and the run is claimed again; after the last attempt the
                # run is answered BLOCKED, so a brief that cannot be stored never loops forever.
                if claimed.attempt < MAX_ATTEMPTS:
                    log.warning(
                        "analysis_store_failed",
                        error_type=type(error).__name__,
                        attempt=claimed.attempt,
                    )
                    return
                log.error(
                    "analysis_gave_up", error_type=type(error).__name__, attempt=claimed.attempt
                )
                outcome = failed_brief(claimed)
                with self._connect() as connection:
                    stored = store(connection, claimed, outcome, self._context)
            log.info(
                "analysis_answered" if stored else "analysis_already_answered",
                result_state=outcome.brief.state,
                reason_codes=outcome.brief.reason_codes,
            )


async def run_analysis_worker(worker: AnalysisWorker, interval_seconds: float) -> None:
    """Poll for accepted requests until cancelled. A failed tick is logged and retried."""
    log = structlog.get_logger()
    log.info("analysis_worker_started", interval_seconds=interval_seconds)
    while True:
        try:
            claimed = await asyncio.to_thread(worker.run_once)
        except Exception as error:
            log.error("analysis_worker_failed", error_type=type(error).__name__)
            claimed = 0
        if not claimed:
            await asyncio.sleep(interval_seconds)
