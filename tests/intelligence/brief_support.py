"""In-memory stand-ins for the brief pipeline tests: no database, no model provider."""

import uuid
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from typing import Any

from app.brief.history import FixRecord, HistoryFacts, HotspotHit, TicketText
from app.brief.pipeline import BriefRequest
from app.knowledge.retrieval import EvidenceBundle, Lesson, Result
from app.llm.gateway import ModelGateway
from app.llm.providers.offline import OfflineProvider
from app.llm.routing import RoutingPolicy
from app.llm.types import (
    DataClassification,
    EvidenceContext,
    ModelPolicy,
    ModelProvider,
    ModelResult,
    TaskSpec,
)

ORG = "https://dev.azure.com/contoso"
TICKET_TEXT = (
    "Kiosk payment captured, barcode print failed after retry.\n"
    "   at Shop.Payments.RetryPolicy.Execute(Int32 attempt) in /src/Payments/RetryPolicy.cs:line 42"
)


def key(name: str) -> str:
    return (name.encode().hex() + "0" * 64)[:64]


def result(
    name: str,
    title: str,
    snippet: str,
    lanes: dict[str, int] | None = None,
    counterevidence: Sequence[str] = (),
    warnings: Sequence[str] = (),
) -> Result:
    return Result(
        item_key=key(name),
        kind="work_item_current",
        title=title,
        score=0.05,
        lanes=dict(lanes if lanes is not None else {"keyword": 1, "vector": 1, "code_path": 1}),
        snippet=snippet,
        citation={
            "item_key": key(name),
            "revision": "3",
            "section": "Repro steps",
            "chunk_key": key(name + "c"),
            "url": f"{ORG}/Payments/_workitems/edit/{name}",
        },
        counterevidence=list(counterevidence),
        warnings=list(warnings),
    )


PRINTED = result(
    "101",
    "Bug 101: Barcode print fails after payment retry",
    "Pay at kiosk, payment captured, retry printing: no barcode on the receipt. "
    "Fixed by making the retry policy idempotent.",
)
DUPLICATE = result(
    "102",
    "Bug 102: Duplicate receipt printed on retry",
    "Retrying a captured payment prints twice. The retry counter fix was merged.",
    counterevidence=["A fix linked to this ticket was later reverted."],
)
RETRY_FILES = ("/src/Payments/RetryPolicy.cs", "/src/Kiosk/ReceiptPrinter.cs")


def bundle(
    results: Sequence[Result] = (PRINTED, DUPLICATE),
    lessons: Sequence[Lesson] = (),
    status: str = "answered",
    reasons: Sequence[str] = (),
    suggestions: Sequence[str] = (),
) -> EvidenceBundle:
    return EvidenceBundle(
        status,
        list(reasons),
        list(suggestions),
        list(results),
        list(lessons),
        "test-hashing-768",
        3.0,
        snapshot_id="snap-test",
        snapshot_age_seconds=10.0,
    )


def fix(ticket: str, pr: str, reverted: bool = False) -> FixRecord:
    return FixRecord(ticket, key(ticket), "pull_request", pr, reverted, RETRY_FILES)


@dataclass
class FakeSources:
    """What the database would return for one ticket."""

    ticket: TicketText | None = field(
        default_factory=lambda: TicketText(
            key("4711"), ORG, "Bug 4711: Barcode not printed after payment retry", TICKET_TEXT
        )
    )
    evidence: EvidenceBundle = field(default_factory=bundle)
    changed: dict[str, tuple[str, ...]] = field(
        default_factory=lambda: {key("101"): RETRY_FILES, key("102"): RETRY_FILES}
    )
    facts: HistoryFacts = field(
        default_factory=lambda: HistoryFacts(
            fixes=(fix("101", "201"), fix("102", "202", reverted=True)), activity=14, hotspots=()
        )
    )
    searches: list[tuple[str, tuple[str, ...]]] = field(default_factory=list)

    def load_ticket(self, project_id: str, ticket_id: str) -> TicketText | None:
        return self.ticket

    def search(self, query: str, exclude: Sequence[str], limit: int) -> EvidenceBundle:
        self.searches.append((query, tuple(exclude)))
        return self.evidence

    def changed_files(self, tickets: Sequence[str]) -> dict[str, tuple[str, ...]]:
        return {ticket: self.changed[ticket] for ticket in tickets if ticket in self.changed}

    def history(self, ticket: TicketText, files: Sequence[str], window_days: int) -> HistoryFacts:
        return self.facts


class ScriptedProvider:
    """A provider that answers from a script: outputs, statuses or a function of the context."""

    def __init__(
        self,
        name: str,
        script: Sequence[Any] | Callable[[TaskSpec, EvidenceContext], Any],
        model_id: str = "scripted-1",
        tokens: tuple[int, int] = (900, 200),
    ) -> None:
        self.name = name
        self.model_id = model_id
        self._script = script
        self._tokens = tokens
        self.tasks: list[TaskSpec] = []
        self.policies: list[ModelPolicy] = []

    def generate(
        self, task: TaskSpec, context: EvidenceContext, policy: ModelPolicy
    ) -> ModelResult:
        self.tasks.append(task)
        self.policies.append(policy)
        step = (
            self._script(task, context)
            if callable(self._script)
            else self._script[min(len(self.tasks), len(self._script)) - 1]
        )
        if isinstance(step, Exception):
            raise step
        status, output = (step, {}) if isinstance(step, str) else ("OK", step)
        return ModelResult(
            provider=self.name,
            model_id=self.model_id,
            prompt_template_version=task.prompt_template_version,
            evidence_snapshot_id=context.snapshot_id,
            output=output,
            input_tokens=self._tokens[0],
            output_tokens=self._tokens[1] if status == "OK" else 0,
            elapsed_ms=120,
            status=status,  # type: ignore[arg-type]
            error_code=None if status == "OK" else "scripted",
        )


def gateway_for(
    *providers: ModelProvider,
    claude_limit: DataClassification | None = DataClassification.INTERNAL,
    override: ModelProvider | None = None,
) -> ModelGateway:
    access = {"gemini": DataClassification.RESTRICTED}
    if claude_limit is not None:
        access["claude"] = claude_limit
    return ModelGateway(
        RoutingPolicy(
            {provider.name: provider for provider in providers}, access, override=override
        )
    )


def offline_gateway() -> ModelGateway:
    return gateway_for(override=OfflineProvider())


def request() -> BriefRequest:
    return BriefRequest(uuid.uuid4(), "Payments", "4711", 12)


def advice(
    statement: str, evidence_id: str = "E1", quote: str = "no barcode on the receipt", kind="DO"
) -> dict:
    return {
        "kind": kind,
        "statement": statement,
        "support": [{"evidence_id": evidence_id, "quote": quote}],
    }


GOOD_DRAFT = {
    "advice": [
        advice(
            "Make the payment retry idempotent before touching the receipt printer.",
            "E1",
            "Fixed by making the retry policy idempotent",
            "APPROACH",
        ),
        advice(
            "Do not reuse the retry counter fix: it was reverted.",
            "E2",
            "The retry counter fix was merged",
            "DONT",
        ),
    ],
    "limitations": [],
}

__all__ = [
    "DUPLICATE",
    "GOOD_DRAFT",
    "PRINTED",
    "FakeSources",
    "HotspotHit",
    "ScriptedProvider",
    "advice",
    "bundle",
    "fix",
    "gateway_for",
    "key",
    "offline_gateway",
    "request",
    "result",
]
