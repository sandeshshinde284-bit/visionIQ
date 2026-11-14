"""S08-05 and the single entry point for model calls: budget, routing, logging, one fallback.

Every call, whatever its outcome, is logged with provider, model, prompt version and tokens and
kept in the run's ledger (stored in ``knowledge.llm_calls``). When the budget is spent or no
provider may see the data, the gateway returns no result and a reason code: the caller then
publishes a PARTIAL brief with the evidence it found, never invented advice.
"""

from __future__ import annotations

import time
import uuid
from collections.abc import Callable, Collection
from dataclasses import dataclass, field

import structlog
from jsonschema import Draft202012Validator

from app.llm.prompts import TEMPLATES, render_user_prompt
from app.llm.routing import (
    REASON_TIER_FALLBACK,
    REASON_TIER_PREFIX,
    ROUTING_REVISION,
    RoutingDecision,
    RoutingPolicy,
)
from app.llm.types import (
    DataClassification,
    EvidenceContext,
    ModelPolicy,
    ModelProvider,
    ModelResult,
    TaskKind,
    TaskSpec,
    failed,
)

REASON_BUDGET = "llm_budget_exhausted"
REASON_NO_PROVIDER = "llm_no_allowed_provider"
REASON_REFUSED = "llm_refused"
REASON_UNAVAILABLE = "llm_unavailable"


@dataclass
class Budget:
    """Bounds for one run: number of calls, total tokens and wall-clock time (10.9 rule 3)."""

    max_calls: int
    max_tokens: int
    max_seconds: float
    clock: Callable[[], float] = time.monotonic
    calls: int = 0
    tokens: int = 0
    _started: float = field(init=False)

    def __post_init__(self) -> None:
        self._started = self.clock()

    def remaining_seconds(self) -> float:
        return self.max_seconds - (self.clock() - self._started)

    def exhausted(self) -> str | None:
        """Which limit is spent ("calls", "tokens" or "time"), or None."""
        if self.calls >= self.max_calls:
            return "calls"
        if self.tokens >= self.max_tokens:
            return "tokens"
        if self.remaining_seconds() <= 0:
            return "time"
        return None

    def charge(self, result: ModelResult) -> None:
        self.calls += 1
        self.tokens += result.input_tokens + result.output_tokens


@dataclass(frozen=True)
class CallRecord:
    """One row of the call log: who was asked what, with which prompt, and how it went."""

    result: ModelResult
    task: TaskKind
    role: str
    routing_reason: str
    classification: DataClassification


@dataclass
class RunLedger:
    run_id: uuid.UUID | None
    classification: DataClassification
    budget: Budget
    calls: list[CallRecord] = field(default_factory=list)


@dataclass(frozen=True)
class GatewayOutcome:
    """A usable result, or the fixed reason there is none."""

    result: ModelResult | None
    reason: str | None = None


class ModelGateway:
    def __init__(
        self,
        routing: RoutingPolicy,
        call_timeout_seconds: float = 45.0,
        max_output_tokens: int = 16_000,
    ) -> None:
        self._routing = routing
        self._call_timeout = call_timeout_seconds
        self._max_output_tokens = max_output_tokens

    def generate(
        self,
        kind: TaskKind,
        context: EvidenceContext,
        ledger: RunLedger,
        tier: str | None = None,
    ) -> GatewayOutcome:
        """Run ``kind`` on its routed provider; after a provider error, try one fallback.

        ``tier`` (model selection by complexity) asks for that tier's provider on a code task.
        If that provider returns an error, the task's normal route is tried once.
        """
        decision = self._routing.primary(kind, ledger.classification, tier)
        if decision.provider is None:
            structlog.get_logger().warning(
                "llm_not_routed", task=kind.value, reason=decision.reason
            )
            return GatewayOutcome(None, REASON_NO_PROVIDER)
        outcome = self._call(kind, context, ledger, decision)
        if outcome.reason == REASON_UNAVAILABLE and decision.reason.startswith(REASON_TIER_PREFIX):
            usual = self._routing.primary(kind, ledger.classification)
            if usual.provider is not None and usual.provider is not decision.provider:
                retry = self._call(
                    kind,
                    context,
                    ledger,
                    RoutingDecision(usual.provider, "fallback", REASON_TIER_FALLBACK),
                )
                return retry if retry.reason is None else GatewayOutcome(None, retry.reason)
        failed_result = outcome.result
        if outcome.reason != REASON_UNAVAILABLE or failed_result is None:
            return outcome if outcome.reason is None else GatewayOutcome(None, outcome.reason)
        fallback = self._routing.fallback(kind, ledger.classification, failed_result.provider)
        if fallback is None:
            return GatewayOutcome(None, REASON_UNAVAILABLE)
        retry = self._call(kind, context, ledger, fallback)
        return retry if retry.reason is None else GatewayOutcome(None, retry.reason)

    def escalate(
        self,
        kind: TaskKind,
        review: TaskKind,
        context: EvidenceContext,
        ledger: RunLedger,
        signals: Collection[str],
        drafted_by: str,
    ) -> GatewayOutcome:
        """S08-10: a second opinion (``review``) on a ``kind`` result, if a signal justifies it."""
        decision = self._routing.escalation(kind, ledger.classification, signals, drafted_by)
        log = structlog.get_logger()
        if decision is None or decision.provider is None:
            if signals:
                log.info("llm_escalation_skipped", task=kind.value, reason="no_allowed_provider")
            return GatewayOutcome(None, REASON_NO_PROVIDER)
        log.info(
            "llm_escalation",
            task=kind.value,
            reason=decision.reason,
            provider=decision.provider.name,
        )
        outcome = self._call(review, context, ledger, decision)
        return outcome if outcome.reason is None else GatewayOutcome(None, outcome.reason)

    def _call(
        self, kind: TaskKind, context: EvidenceContext, ledger: RunLedger, decision: RoutingDecision
    ) -> GatewayOutcome:
        provider = decision.provider
        assert provider is not None
        log = structlog.get_logger()
        spent = ledger.budget.exhausted()
        if spent is not None:
            log.warning("llm_budget_exhausted", task=kind.value, limit=spent, role=decision.role)
            return GatewayOutcome(None, REASON_BUDGET)
        template = TEMPLATES[kind]
        task = TaskSpec(
            kind=kind,
            run_id=ledger.run_id,
            classification=ledger.classification,
            system_prompt=template.system,
            user_prompt=render_user_prompt(kind, context),
            prompt_template_version=template.template_version,
            output_schema=template.output_schema,
        )
        policy = ModelPolicy(
            timeout_seconds=max(0.1, min(self._call_timeout, ledger.budget.remaining_seconds())),
            max_output_tokens=self._max_output_tokens,
        )
        result = self._generate(provider, task, context, policy)
        ledger.budget.charge(result)
        ledger.calls.append(
            CallRecord(result, kind, decision.role, decision.reason, task.classification)
        )
        # The completion evidence for BRF-02: provider, model, prompt version and tokens per call.
        log.info(
            "llm_call",
            invocation_id=str(result.invocation_id),
            task=kind.value,
            role=decision.role,
            routing_reason=decision.reason,
            routing_revision=ROUTING_REVISION,
            provider=result.provider,
            model_id=result.model_id,
            model_version=result.model_version,
            prompt_template_version=result.prompt_template_version,
            data_classification=task.classification.value,
            status=result.status,
            error_code=result.error_code,
            input_tokens=result.input_tokens,
            output_tokens=result.output_tokens,
            elapsed_ms=result.elapsed_ms,
        )
        if result.status == "OK":
            return GatewayOutcome(result)
        reason = REASON_REFUSED if result.status == "REFUSED" else REASON_UNAVAILABLE
        return GatewayOutcome(result, reason)

    @staticmethod
    def _generate(
        provider: ModelProvider, task: TaskSpec, context: EvidenceContext, policy: ModelPolicy
    ) -> ModelResult:
        started = time.perf_counter()
        try:
            result = provider.generate(task, context, policy)
        except Exception as error:  # a provider bug must not take the run down with it
            elapsed = int((time.perf_counter() - started) * 1000)
            return failed(provider, task, context, "ERROR", type(error).__name__, elapsed)
        if result.status == "OK" and any(
            Draft202012Validator(task.output_schema).iter_errors(result.output)
        ):
            # Structure is checked here for every provider; what the advice says is checked later.
            return result.model_copy(
                update={"status": "ERROR", "error_code": "schema_violation", "output": {}}
            )
        return result
