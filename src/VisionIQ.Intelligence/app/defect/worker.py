"""The fix worker: answers accepted ``analysis.requested`` events of mode ``deep``.

It works like the brief's analysis worker and shares its queue (``knowledge.analysis_requests``),
taking only the deep requests. A request is leased while it runs, so replicas never work on the
same run. The fix result, its executions, the model-call log and the ``analysis.completed`` event
are written in ONE transaction. A run that keeps failing for infrastructure reasons is answered
BLOCKED instead of hanging forever.
"""

from __future__ import annotations

import asyncio
import uuid
from collections.abc import Callable
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

import psycopg
import structlog
from psycopg.types.json import Jsonb

from app.defect import gates as gate_rules
from app.defect.pipeline import FixOutcome, FixSettings, run_fix
from app.defect.policy import DefectPolicy, RunnerPolicy
from app.defect.runner import Runner
from app.defect.schema import (
    FixRequest,
    FixResult,
    Generation,
    Performance,
    Regression,
    Reproduction,
    TicketRef,
    Timings,
)
from app.defect.sources import FixSources
from app.events.envelope import AnalysisCompletedV1, EventEnvelope, follow_up
from app.events.outbox import enqueue
from app.events.processing import EventContext
from app.llm.gateway import Budget, ModelGateway, RunLedger
from app.llm.routing import ROUTING_REVISION

# A run clones, builds and tests several times; the lease must outlast the whole budget. The
# pipeline starts no execution that cannot end inside FixSettings.max_seconds, so the budget
# plus a margin (clone, export, storing the result) is always enough.
LEASE_SECONDS = 3600
LEASE_MARGIN_SECONDS = 900
MAX_ATTEMPTS = 3
ANALYSIS_FAILED = "analysis_failed"


def lease_seconds(settings: FixSettings) -> int:
    return max(LEASE_SECONDS, int(settings.max_seconds) + LEASE_MARGIN_SECONDS)


CLAIM_SQL = """
UPDATE knowledge.analysis_requests AS q
SET available_at = now() + make_interval(secs => %(lease)s), attempt_count = q.attempt_count + 1
FROM (SELECT run_id FROM knowledge.analysis_requests
      WHERE status = 'pending' AND analysis_mode = 'deep' AND available_at <= now()
      ORDER BY available_at LIMIT %(batch)s FOR UPDATE SKIP LOCKED) AS due
WHERE q.run_id = due.run_id
RETURNING q.run_id, q.project_id, q.ticket_id, q.ticket_revision, q.repository_id,
          q.base_commit_sha, q.security_scope_id, q.tenant_id, q.request_event, q.received_at,
          q.attempt_count
"""


@dataclass(frozen=True)
class ClaimedFix:
    request: FixRequest
    tenant_id: str
    event: EventEnvelope
    accepted_at: datetime
    attempt: int


def claim(
    connection: psycopg.Connection, batch: int = 1, lease: int = LEASE_SECONDS
) -> list[ClaimedFix]:
    """Lease due deep requests, one at a time by default."""
    with connection.transaction():
        rows = connection.execute(CLAIM_SQL, {"lease": lease, "batch": batch}).fetchall()
    return [
        ClaimedFix(
            FixRequest(
                run_id=run_id,
                ticket=TicketRef(project=project_id, id=ticket_id, revision=revision),
                repository_id=repository_id,
                base_commit_sha=base_sha,
                security_scope_id=scope,
            ),
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
            repository_id,
            base_sha,
            scope,
            tenant,
            event,
            accepted_at,
            attempt,
        ) in rows
    ]


def store(
    connection: psycopg.Connection,
    claimed: ClaimedFix,
    outcome: FixOutcome,
    context: EventContext,
) -> bool:
    """Persist the fix result and queue analysis.completed, once. False if already answered."""
    result = outcome.result
    with connection.transaction():
        answered = connection.execute(
            "UPDATE knowledge.analysis_requests SET status = 'completed', result_id = %s, "
            "result_state = %s, reason_codes = %s, completed_at = now() "
            "WHERE run_id = %s AND status = 'pending' RETURNING run_id",
            (result.fix_id, result.state, result.reason_codes, result.run_id),
        ).fetchone()
        if answered is None:
            return False
        connection.execute(
            "INSERT INTO knowledge.fix_runs (fix_id, run_id, tenant_id, project_id, ticket_id, "
            "ticket_revision, repository_id, base_commit_sha, candidate_commit_sha, "
            "candidate_tree_sha, state, verdict, reason_codes, reproducibility, gates_overall, "
            "schema_version, document, policy_revision, routing_revision, accepted_at, total_ms) "
            "VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, "
            "%s, %s)",
            (
                result.fix_id,
                result.run_id,
                claimed.tenant_id,
                result.ticket.project,
                result.ticket.id,
                result.ticket.revision,
                result.repository_id,
                result.base_sha,
                result.candidate_sha,
                result.patch.tree_sha if result.patch else None,
                result.state,
                result.verdict,
                result.reason_codes,
                result.reproduction.reproducibility,
                result.gate_summary.overall,
                result.schema_version,
                Jsonb(result.model_dump(mode="json")),
                result.policy_revision,
                result.generation.routing_revision,
                claimed.accepted_at,
                result.timings.total_ms,
            ),
        )
        for execution in result.executions:
            connection.execute(
                "INSERT INTO knowledge.fix_executions (fix_id, execution_id, manifest_id, "
                "purpose, code, commit_sha, outcome, test_id, exit_code, timed_out, duration_ms, "
                "log_uri, log_sha256, started_at) "
                "VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)",
                (
                    result.fix_id,
                    execution.execution_id,
                    execution.manifest_id,
                    execution.purpose,
                    execution.code,
                    execution.commit_sha,
                    execution.outcome,
                    execution.test_id,
                    execution.exit_code,
                    execution.timed_out,
                    execution.duration_ms,
                    execution.log_uri,
                    execution.log_sha256,
                    execution.started_at,
                ),
            )
        for call in outcome.ledger.calls:
            model = call.result
            connection.execute(
                "INSERT INTO knowledge.llm_calls (invocation_id, run_id, task, role, provider, "
                "model_id, model_version, prompt_template_version, routing_revision, "
                "data_classification, evidence_snapshot_id, status, error_code, input_tokens, "
                "output_tokens, elapsed_ms) "
                "VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)",
                (
                    model.invocation_id,
                    result.run_id,
                    call.task.value,
                    call.role,
                    model.provider,
                    model.model_id,
                    model.model_version,
                    model.prompt_template_version,
                    ROUTING_REVISION,
                    call.classification.value,
                    model.evidence_snapshot_id,
                    model.status,
                    model.error_code,
                    model.input_tokens,
                    model.output_tokens,
                    model.elapsed_ms,
                ),
            )
        completed = AnalysisCompletedV1(
            result_id=result.fix_id,
            result_state=result.state,
            evidence_ids=result.execution_ids(),
            advice_ids=[],
            reason_codes=result.reason_codes,
        )
        enqueue(
            connection,
            follow_up(claimed.event, "analysis.completed", completed),
            context.contracts,
            context.topology,
        )
    return True


def failed_fix(
    claimed: ClaimedFix,
    settings: FixSettings,
    reason: str = ANALYSIS_FAILED,
    limitation: str = "The fix run failed repeatedly and was stopped. Nothing was proposed.",
) -> FixOutcome:
    """The honest answer for a run that could not be completed: BLOCKED, with the reason."""
    request = claimed.request
    reproduction = Reproduction(reproducibility="INCONCLUSIVE", detail="The run did not complete.")
    regression = Regression(status="NOT_RUN", historical_found=False, detail="Not run.")
    performance = Performance(status="NOT_MEASURED", detail="Not run.")
    gates = gate_rules.build(reproduction, None, None, regression, None, False, performance, None)
    result = FixResult(
        fix_id=uuid.uuid4(),
        run_id=request.run_id,
        ticket=request.ticket,
        repository_id=request.repository_id,
        base_sha=request.base_commit_sha,
        state="BLOCKED",
        verdict="BLOCKED",
        reason_codes=[reason],
        reproduction=reproduction,
        regression=regression,
        performance=performance,
        gates=gates,
        gate_summary=gate_rules.summarize(gates, None),
        limitations=[limitation],
        generation=Generation(routing_revision=ROUTING_REVISION),
        timings=Timings(),
    )
    return FixOutcome(result, RunLedger(request.run_id, settings.classification, Budget(0, 0, 0)))


def retry_delay_seconds(attempt: int) -> int:
    return min(600, 60 * 2 ** (attempt - 1))


class FixWorker:
    """Processes claimed deep requests one at a time. ``sources_for`` binds reads to a scope."""

    def __init__(
        self,
        connect: Callable[[], psycopg.Connection],
        sources_for: Callable[[str], FixSources],
        gateway: ModelGateway,
        policy: DefectPolicy | None,
        settings: FixSettings,
        context: EventContext,
        runner_for: Callable[[RunnerPolicy], Runner] | None = None,
    ) -> None:
        self._connect = connect
        self._sources_for = sources_for
        self._gateway = gateway
        self._policy = policy
        self._settings = settings
        self._context = context
        self._runner_for = runner_for

    def run_once(self) -> int:
        """Claim and answer due requests; returns how many were claimed."""
        with self._connect() as connection:
            claimed = claim(connection, lease=lease_seconds(self._settings))
        for item in claimed:
            self._process(item)
        return len(claimed)

    def _process(self, claimed: ClaimedFix) -> None:
        log = structlog.get_logger()
        with claimed.event.logging_scope():
            log.info(
                "fix_claimed",
                attempt=claimed.attempt,
                max_attempts=MAX_ATTEMPTS,
                repository_id=claimed.request.repository_id,
                waited_ms=int(
                    (datetime.now(claimed.accepted_at.tzinfo) - claimed.accepted_at).total_seconds()
                    * 1000
                ),
            )
            if claimed.attempt > MAX_ATTEMPTS:
                # Earlier attempts ended without an answer and without reaching the handlers
                # below: the process died mid-run (out of memory, a restart). Running it again
                # would spend the budget again, so it is answered instead.
                log.error("fix_gave_up", error_type="AttemptsExhausted", attempt=claimed.attempt)
                outcome = failed_fix(claimed, self._settings)
                with self._connect() as connection:
                    stored = store(connection, claimed, outcome, self._context)
                log.info(
                    "fix_answered" if stored else "fix_already_answered",
                    fix_id=str(outcome.result.fix_id),
                    result_state=outcome.result.state,
                    verdict=outcome.result.verdict,
                    reason_codes=outcome.result.reason_codes,
                    executions=0,
                    model_calls=0,
                )
                return
            try:
                outcome = run_fix(
                    claimed.request,
                    self._sources_for(claimed.request.security_scope_id),
                    self._gateway,
                    self._policy,
                    self._settings,
                    self._runner_for,
                )
            except Exception as error:
                # Type only: database, git and provider messages can carry connection details.
                if claimed.attempt < MAX_ATTEMPTS:
                    delay = retry_delay_seconds(claimed.attempt)
                    with self._connect() as connection, connection.transaction():
                        connection.execute(
                            "UPDATE knowledge.analysis_requests SET available_at = now() + "
                            "make_interval(secs => %s) WHERE run_id = %s AND status = 'pending'",
                            (delay, claimed.request.run_id),
                        )
                    log.warning(
                        "fix_deferred",
                        error_type=type(error).__name__,
                        attempt=claimed.attempt,
                        retry_in_seconds=delay,
                    )
                    return
                log.error("fix_gave_up", error_type=type(error).__name__, attempt=claimed.attempt)
                outcome = failed_fix(claimed, self._settings)
            try:
                with self._connect() as connection:
                    stored = store(connection, claimed, outcome, self._context)
            except Exception as error:
                # The lease runs out and the run is claimed again; after the last attempt it is
                # answered BLOCKED, so a result that cannot be stored never loops forever.
                if claimed.attempt < MAX_ATTEMPTS:
                    log.warning(
                        "fix_store_failed", error_type=type(error).__name__, attempt=claimed.attempt
                    )
                    return
                log.error("fix_gave_up", error_type=type(error).__name__, attempt=claimed.attempt)
                outcome = failed_fix(claimed, self._settings)
                with self._connect() as connection:
                    stored = store(connection, claimed, outcome, self._context)
            log.info(
                "fix_answered" if stored else "fix_already_answered",
                fix_id=str(outcome.result.fix_id),
                result_state=outcome.result.state,
                verdict=outcome.result.verdict,
                reason_codes=outcome.result.reason_codes,
                executions=len(outcome.result.executions),
                model_calls=len(outcome.ledger.calls),
            )


def answer_stranded(connection: psycopg.Connection, context: EventContext, batch: int = 50) -> int:
    """With the fix pipeline off, answer deep requests that were accepted while it was on.

    Nothing claims them any more, so without this they (and the API's runs) would wait for ever.
    Each gets the answer a new deep request gets: BLOCKED / deep_analysis_not_implemented.
    """
    from app.events.processing import DEEP_ANALYSIS_NOT_IMPLEMENTED

    settings = FixSettings(work_dir=Path("."), artifact_dir=Path("."))
    answered = 0
    while True:
        # A lease of zero: the rows are only picked, not held; `store` answers each exactly once.
        claimed = claim(connection, batch=batch, lease=0)
        if not claimed:
            return answered
        for item in claimed:
            outcome = failed_fix(
                item,
                settings,
                DEEP_ANALYSIS_NOT_IMPLEMENTED,
                "The fix pipeline was switched off before this request was worked on.",
            )
            if store(connection, item, outcome, context):
                answered += 1


def answer_stranded_requests(settings: object, context: EventContext) -> None:
    """Start-up hook for a service whose fix pipeline is off. Never fails the service."""
    from app.db import connect

    log = structlog.get_logger()
    try:
        with connect(settings) as connection:  # type: ignore[arg-type]
            answered = answer_stranded(connection, context)
    except Exception as error:
        log.warning("fix_stranded_check_failed", error_type=type(error).__name__)
        return
    if answered:
        log.warning("fix_stranded_answered", requests=answered)


async def run_fix_worker(worker: FixWorker, interval_seconds: float) -> None:
    """Poll for accepted deep requests until cancelled. A failed tick is logged and retried."""
    log = structlog.get_logger()
    log.info("fix_worker_started", interval_seconds=interval_seconds)
    while True:
        try:
            claimed = await asyncio.to_thread(worker.run_once)
        except Exception as error:
            log.error("fix_worker_failed", error_type=type(error).__name__)
            claimed = 0
        if not claimed:
            await asyncio.sleep(interval_seconds)
