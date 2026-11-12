"""S08-12: compare providers on labelled cases, with numbers instead of impressions.

Each case is a ticket, its evidence and what a good answer must and must not do. A provider is
run through the same gateway and the same per-advice evidence check as production, and scored on
the measures the design asks for (10.9 evaluation gate): output validity, citation validity,
grounded-advice recall, harmful advice, abstention when there is nothing to say, latency and
tokens. Routing changes are justified by these numbers, never by which model "seems better".
"""

from __future__ import annotations

from collections.abc import Mapping, Sequence
from dataclasses import asdict, dataclass, field
from typing import Any

from app.brief.validate import EvidenceText, validate
from app.llm.gateway import Budget, ModelGateway, RunLedger
from app.llm.routing import RoutingPolicy
from app.llm.types import (
    DataClassification,
    EvidenceContext,
    EvidenceItem,
    ModelProvider,
    TaskKind,
)


@dataclass(frozen=True)
class EvaluationCase:
    """One labelled case. ``expect_evidence`` are ids good advice should cite; a case with
    ``expect_abstention`` has nothing to ground advice on; ``forbidden`` are phrases that would
    make advice harmful (for example obeying an instruction hidden in the ticket)."""

    name: str
    ticket_title: str
    ticket_text: str
    evidence: tuple[EvidenceItem, ...]
    expect_evidence: tuple[str, ...] = ()
    expect_abstention: bool = False
    forbidden: tuple[str, ...] = ()

    @classmethod
    def from_dict(cls, data: Mapping[str, Any]) -> EvaluationCase:
        return cls(
            name=data["name"],
            ticket_title=data["ticket_title"],
            ticket_text=data["ticket_text"],
            evidence=tuple(EvidenceItem(**item) for item in data.get("evidence", [])),
            expect_evidence=tuple(data.get("expect_evidence", [])),
            expect_abstention=bool(data.get("expect_abstention", False)),
            forbidden=tuple(data.get("forbidden", [])),
        )


@dataclass
class CaseResult:
    name: str
    output_valid: bool
    advice_proposed: int
    advice_kept: int
    expected_cited: int
    expected_total: int
    harmful: int
    abstained: bool
    abstention_correct: bool
    input_tokens: int
    output_tokens: int
    elapsed_ms: int


@dataclass
class EvaluationReport:
    provider: str
    model_id: str
    cases: list[CaseResult] = field(default_factory=list)

    def _rate(self, numerator: int, denominator: int) -> float | None:
        return round(numerator / denominator, 3) if denominator else None

    def summary(self) -> dict[str, Any]:
        cases = self.cases
        return {
            "provider": self.provider,
            "model_id": self.model_id,
            "cases": len(cases),
            "output_validity": self._rate(sum(c.output_valid for c in cases), len(cases)),
            # Of the advice a model proposed, how much survived the evidence check.
            "citation_validity": self._rate(
                sum(c.advice_kept for c in cases), sum(c.advice_proposed for c in cases)
            ),
            # Of the sources good advice should use, how many the kept advice cites.
            "grounded_recall": self._rate(
                sum(c.expected_cited for c in cases), sum(c.expected_total for c in cases)
            ),
            "harmful_advice": sum(c.harmful for c in cases),
            "abstention_accuracy": self._rate(sum(c.abstention_correct for c in cases), len(cases)),
            "input_tokens": sum(c.input_tokens for c in cases),
            "output_tokens": sum(c.output_tokens for c in cases),
            "elapsed_ms": sum(c.elapsed_ms for c in cases),
        }

    def to_dict(self) -> dict[str, Any]:
        return {"summary": self.summary(), "cases": [asdict(case) for case in self.cases]}


def evaluate(provider: ModelProvider, cases: Sequence[EvaluationCase]) -> EvaluationReport:
    """Run every case through ``provider`` alone and score what survives validation."""
    gateway = ModelGateway(RoutingPolicy({}, {}, override=provider))
    report = EvaluationReport(provider.name, provider.model_id)
    for case in cases:
        ledger = RunLedger(None, DataClassification.PUBLIC, Budget(1, 1_000_000, 300.0))
        context = EvidenceContext(
            snapshot_id="evaluation",
            ticket_title=case.ticket_title,
            ticket_text=case.ticket_text,
            evidence=case.evidence,
        )
        outcome = gateway.generate(TaskKind.BRIEF_DRAFT, context, ledger)
        call = ledger.calls[-1].result if ledger.calls else None
        output = outcome.result.output if outcome.result else {}
        checked = validate(
            output,
            {
                item.evidence_id: EvidenceText(item.title, f"{item.title}\n{item.text}")
                for item in case.evidence
            },
        )
        cited = {evidence_id for advice in checked.advice for evidence_id in advice.evidence_ids}
        abstained = not checked.advice
        report.cases.append(
            CaseResult(
                name=case.name,
                output_valid=outcome.result is not None,
                advice_proposed=len(output.get("advice", [])),
                advice_kept=len(checked.advice),
                expected_cited=len(cited.intersection(case.expect_evidence)),
                expected_total=len(case.expect_evidence),
                harmful=sum(
                    any(phrase.lower() in advice.statement.lower() for phrase in case.forbidden)
                    for advice in checked.advice
                ),
                abstained=abstained,
                abstention_correct=abstained == case.expect_abstention,
                input_tokens=call.input_tokens if call else 0,
                output_tokens=call.output_tokens if call else 0,
                elapsed_ms=call.elapsed_ms if call else 0,
            )
        )
    return report
