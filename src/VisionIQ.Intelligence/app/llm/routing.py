"""S08-04 / S08-10: which provider gets which task (master design 10.9 routing matrix).

One provider per task by default; a second provider is used only for a named reason (a provider
error, or an escalation signal on a brief) and only when the data may go to it. The table and
its revision are recorded with every model call, so a change in routing is always traceable.
"""

from __future__ import annotations

from collections.abc import Collection, Mapping
from dataclasses import dataclass

from app.llm.types import CallRole, DataClassification, ModelProvider, TaskKind

ROUTING_REVISION = "routing-2026-11-10.2"

# S08-10: the only reasons a brief gets a second opinion.
ESCALATE_EVIDENCE_CONFLICT = "evidence_conflict"
ESCALATE_HIGH_IMPACT = "high_impact"


@dataclass(frozen=True)
class Route:
    primary: str | None
    escalation: str | None = None
    escalate_on: frozenset[str] = frozenset()
    # May the escalation provider take over when the primary fails or is not available?
    fallback: bool = False


ROUTING_TABLE: Mapping[TaskKind, Route] = {
    TaskKind.TICKET_CLASSIFICATION: Route("gemini", "claude", frozenset({"ambiguous_labels"})),
    TaskKind.BRIEF_DRAFT: Route(
        "gemini",
        "claude",
        frozenset({ESCALATE_EVIDENCE_CONFLICT, ESCALATE_HIGH_IMPACT}),
        fallback=True,
    ),
    # The critique is the escalation step of a brief: Claude reviews what Gemini drafted.
    TaskKind.EVIDENCE_CRITIQUE: Route("claude"),
    TaskKind.ROOT_CAUSE: Route("gemini", "claude", frozenset({"disputed", "safety_critical"})),
    # Gemini reviews code only if a benchmark justifies it (not yet): no escalation, no fallback.
    TaskKind.REPRODUCTION_TEST: Route("claude"),
    TaskKind.CODE_FIX: Route("claude"),
    TaskKind.ENHANCEMENT_APPROACH: Route("gemini", "claude", frozenset({"cross_repo_contract"})),
    TaskKind.KNOWLEDGE_EXTRACTION: Route("gemini", "claude", frozenset({"unresolved_cluster"})),
    # SQL and verified outcomes first; no model by default.
    TaskKind.ADVICE_ANALYTICS: Route(None),
}


@dataclass(frozen=True)
class RoutingDecision:
    provider: ModelProvider | None
    role: CallRole
    # Why this provider (or none): a fixed code, logged with the call.
    reason: str


class RoutingPolicy:
    """Applies the table to the providers that are configured and allowed to see the data.

    ``access`` is the highest data classification each provider may receive (decision D04 for
    Claude); a provider missing from it receives nothing. ``override`` sends every task to one
    provider and is only for offline mode, where no data leaves the process.
    """

    def __init__(
        self,
        providers: Mapping[str, ModelProvider],
        access: Mapping[str, DataClassification],
        table: Mapping[TaskKind, Route] = ROUTING_TABLE,
        override: ModelProvider | None = None,
    ) -> None:
        self._providers = providers
        self._access = access
        self._table = table
        self._override = override

    def _usable(self, name: str | None, classification: DataClassification) -> ModelProvider | None:
        if name is None or name not in self._providers:
            return None
        limit = self._access.get(name)
        if limit is None or classification.level > limit.level:
            return None  # evaluated before any prompt is built (10.9 router rule 1)
        return self._providers[name]

    def primary(self, task: TaskKind, classification: DataClassification) -> RoutingDecision:
        route = self._table[task]
        if route.primary is None:
            return RoutingDecision(None, "primary", "no_model_for_task")
        if self._override is not None:
            return RoutingDecision(self._override, "primary", "offline_mode")
        provider = self._usable(route.primary, classification)
        if provider is not None:
            return RoutingDecision(provider, "primary", "routing_table")
        if route.fallback:
            provider = self._usable(route.escalation, classification)
            if provider is not None:
                return RoutingDecision(provider, "primary", "primary_not_available")
        return RoutingDecision(None, "primary", "no_allowed_provider")

    def fallback(
        self, task: TaskKind, classification: DataClassification, failed: str
    ) -> RoutingDecision | None:
        """The one provider that may retry after ``failed`` returned an error, if any."""
        route = self._table[task]
        if not route.fallback or self._override is not None:
            return None
        other = route.escalation if failed == route.primary else route.primary
        provider = self._usable(other, classification)
        if provider is None or provider.name == failed:
            return None
        return RoutingDecision(provider, "fallback", "provider_error")

    def escalation(
        self,
        task: TaskKind,
        classification: DataClassification,
        signals: Collection[str],
        drafted_by: str,
    ) -> RoutingDecision | None:
        """A second opinion on ``task``, only for a listed signal and never from the same model."""
        route = self._table[task]
        matched = sorted(route.escalate_on.intersection(signals))
        if not matched:
            return None
        if self._override is not None:
            return RoutingDecision(self._override, "escalation", matched[0])
        provider = self._usable(route.escalation, classification)
        if provider is None or provider.name == drafted_by:
            return None
        return RoutingDecision(provider, "escalation", matched[0])
