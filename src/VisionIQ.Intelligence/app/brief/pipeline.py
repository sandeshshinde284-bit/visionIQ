"""S10-05 / S10-10: build the brief for one ticket.

    ticket text -> retrieval (LRN-09) -> scope -> risk factors and band -> model draft
    -> per-advice evidence check -> optional second opinion -> brief

ENH-01: an enhancement goes through the same steps and gets guidance in the eight sections of
master design 8.5 instead of a flat list (see :mod:`app.brief.guidance`). Recorded references
to the files in scope are added as evidence, "contracts and APIs touched" is written by code
from them, and a section nothing supports is reported as missing.

Evidence and risk are computed by code; the model only words the advice, and only the advice the
evidence supports is kept. Every way of falling short has its own state and reason code:

* BLOCKED   - nothing useful can be said (the ticket is not readable in the knowledge base).
* PARTIAL   - evidence and risk are shown, but advice is missing or the history is thin.
* PUBLISHED - grounded advice on well-supported history.

There is no path that produces advice without supporting evidence.
"""

from __future__ import annotations

import time
import uuid
from collections.abc import Callable
from dataclasses import dataclass

import structlog

from app.brief import guidance as guidance_rules
from app.brief import risk as risk_rules
from app.brief.history import BriefSources, HistoryFacts
from app.brief.schema import (
    Advice,
    Brief,
    DisagreementNote,
    Evidence,
    Generation,
    InjectionFlag,
    Risk,
    RiskFactor,
    TicketRef,
    Timings,
)
from app.brief.validate import EvidenceText, validate
from app.knowledge.redaction import redact
from app.knowledge.retrieval import EvidenceBundle, extract_signals
from app.llm import consistency
from app.llm.gateway import Budget, ModelGateway, RunLedger
from app.llm.injection import InjectionFinding, looks_like_instruction, scan
from app.llm.routing import ESCALATE_EVIDENCE_CONFLICT, ESCALATE_HIGH_IMPACT, ROUTING_REVISION
from app.llm.types import DataClassification, EvidenceContext, EvidenceItem, TaskKind

# Reason codes (analysis.completed carries them; the ticket comment explains them).
TICKET_NOT_READABLE = "ticket_not_readable"
TICKET_TOO_VAGUE = "ticket_too_vague"
NO_HISTORY_FOUND = "no_history_found"
WEAK_EVIDENCE = "weak_evidence"
NO_SUPPORTED_ADVICE = "no_supported_advice"

MAX_LIMITATIONS = 8
MAX_MODEL_LIMITATION_CHARS = 240


@dataclass(frozen=True)
class BriefRequest:
    run_id: uuid.UUID
    project_id: str
    ticket_id: str
    ticket_revision: int
    # ENH-01: from the request event. The pickup policy decides what an enhancement is.
    ticket_kind: str = "defect"
    repository_id: str = ""


@dataclass(frozen=True)
class BriefSettings:
    classification: DataClassification = DataClassification.INTERNAL
    evidence_limit: int = 8
    risk_window_days: int = 90
    max_calls: int = 3
    max_tokens: int = 60_000
    max_seconds: float = 90.0


@dataclass(frozen=True)
class BriefOutcome:
    brief: Brief
    ledger: RunLedger


def _insufficient(explanation: str) -> Risk:
    return Risk(
        band="INSUFFICIENT_DATA",
        explanation=explanation,
        factors=[
            RiskFactor(code="recent_fixes", label="Defect fixes in scope", value=None, missing=True)
        ],
    )


def _evidence(bundle: EvidenceBundle) -> tuple[list[Evidence], dict[str, str]]:
    """Numbered evidence for the brief (E1, E2, ...) and the item key -> id map.

    Only sources with a link an engineer can open are citable. Lessons are derived knowledge:
    they are given to the model as facts and shown as a risk factor, never cited on their own.
    """
    evidence: list[Evidence] = []
    ids: dict[str, str] = {}
    for result in bundle.results:
        evidence_id = f"E{len(evidence) + 1}"
        ids[result.item_key] = evidence_id
        strong = bool(result.lanes.get("code_path")) or (
            "keyword" in result.lanes and "vector" in result.lanes
        )
        evidence.append(
            Evidence(
                id=evidence_id,
                item_key=result.item_key,
                kind=result.kind,
                title=result.title,
                url=result.citation.get("url"),
                revision=result.citation.get("revision"),
                snippet=result.snippet,
                relevance="strong" if strong else "related",
                notes=[*result.counterevidence, *result.warnings],
            )
        )
    return evidence, ids


def _model_limitations(output: dict) -> list[str]:
    """What the model said it could not establish; bounded and scrubbed, as it is model text."""
    kept = []
    for text in output.get("limitations", [])[:3]:
        line = " ".join(str(text).split())[:MAX_MODEL_LIMITATION_CHARS]
        if line and not looks_like_instruction(line) and not redact(line).counts:
            kept.append(line)
    return kept


def build_brief(
    request: BriefRequest,
    sources: BriefSources,
    gateway: ModelGateway,
    settings: BriefSettings,
    clock: Callable[[], float] = time.perf_counter,
) -> BriefOutcome:
    """Run the pipeline for one accepted request. Raises only on infrastructure failures."""
    log = structlog.get_logger()
    started = clock()
    ledger = RunLedger(
        request.run_id,
        settings.classification,
        Budget(settings.max_calls, settings.max_tokens, settings.max_seconds),
    )
    reasons: list[str] = []
    limitations: list[str] = []
    flags: list[InjectionFinding] = []
    advice: list[Advice] = []
    notes: list[DisagreementNote] = []
    evidence: list[Evidence] = []
    scope = []
    snapshot_id: str | None = None
    drafted_by = reviewed_by = prompt_version = model_id = None
    retrieval_ms = generation_ms = 0
    title = ""
    enhancement = request.ticket_kind == "enhancement"
    cross_repository = False

    def finish(state: str, risk: Risk) -> BriefOutcome:
        brief = Brief(
            brief_id=uuid.uuid4(),
            run_id=request.run_id,
            ticket=TicketRef(
                project=request.project_id,
                id=request.ticket_id,
                revision=request.ticket_revision,
                title=title,
            ),
            kind="enhancement" if enhancement else "defect",
            state=state,  # type: ignore[arg-type]
            reason_codes=reasons,
            snapshot_id=snapshot_id,
            predicted_scope=scope,
            risk=risk,
            advice=advice,
            guidance=guidance_rules.sections(advice) if enhancement else [],
            evidence=evidence,
            disagreements=notes,
            limitations=limitations[:MAX_LIMITATIONS],
            injection_flags=[InjectionFlag(source=f.source, pattern=f.pattern) for f in flags],
            generation=Generation(
                provider=drafted_by,
                model_id=model_id,
                prompt_template_version=prompt_version,
                routing_revision=ROUTING_REVISION,
                reviewed_by=reviewed_by,
                calls=len(ledger.calls),
                input_tokens=sum(call.result.input_tokens for call in ledger.calls),
                output_tokens=sum(call.result.output_tokens for call in ledger.calls),
            ),
            timings=Timings(
                retrieval_ms=retrieval_ms,
                generation_ms=generation_ms,
                total_ms=int((clock() - started) * 1000),
            ),
        )
        log.info(
            "brief_built",
            state=brief.state,
            reason_codes=reasons,
            risk_band=risk.band,
            advice=len(advice),
            evidence=len(evidence),
            injection_flags=len(flags),
            retrieval_ms=retrieval_ms,
            generation_ms=generation_ms,
            total_ms=brief.timings.total_ms,
        )
        return BriefOutcome(brief, ledger)

    ticket = sources.load_ticket(request.project_id, request.ticket_id)
    if ticket is None:
        reasons.append(TICKET_NOT_READABLE)
        limitations.append(
            "The ticket is not in the knowledge base under this project's access scope yet, "
            "so no history could be matched."
        )
        return finish("BLOCKED", _insufficient("The ticket could not be read."))
    title = ticket.title
    if ticket.truncated:
        limitations.append("The ticket is long; only its first part was analysed.")

    # S08-07: instructions inside the ticket are recorded, not followed.
    flags.extend(scan("ticket", f"{ticket.title}\n{ticket.text}"))

    step = clock()
    bundle = sources.search(
        f"{ticket.title}\n{ticket.text}", exclude=[ticket.item_key], limit=settings.evidence_limit
    )
    retrieval_ms = int((clock() - step) * 1000)
    snapshot_id = bundle.snapshot_id
    evidence, evidence_ids = _evidence(bundle)
    for item in evidence:
        flags.extend(scan(item.id, f"{item.title}\n{item.snippet}"))
    if flags:
        log.warning(
            "prompt_injection_flagged",
            sources=sorted({flag.source for flag in flags}),
            patterns=sorted({flag.pattern for flag in flags}),
        )
        limitations.append(
            "Some source text contains instructions addressed to an AI assistant. "
            "It was treated as data and not followed."
        )

    # S10-02 / S10-03 / S10-13: scope, factors and band come from recorded history only.
    signals = extract_signals(ticket.text)
    changed = sources.changed_files([result.item_key for result in bundle.results])
    scope = risk_rules.predict_scope(signals, bundle.results, changed, evidence_ids)
    names = risk_rules.scope_file_names(scope)
    facts = sources.history(ticket, names, settings.risk_window_days) if names else HistoryFacts()
    risk = risk_rules.assess(
        scope,
        facts,
        bundle.lessons,
        evidence_ids,
        settings.risk_window_days,
        lessons_known=not any(reason.startswith("lessons search") for reason in bundle.reasons),
    )
    limitations.extend(bundle.reasons)

    if enhancement:
        # ENH-01 / PROP-02: recorded references to the files in scope become citable evidence.
        paths = guidance_rules.lookup_paths(
            [item.path for item in scope], (path for files in changed.values() for path in files)
        )
        found = sources.dependents(request.repository_id, paths)
        references = guidance_rules.dependency_evidence(found, len(evidence) + 1)
        evidence = [*evidence, *references]
        cross_repository = any(item.cross_repository and item.dependents for item in found)
        if not scope:
            limitations.append(
                "The files this change touches could not be predicted from the ticket and its "
                "history, so contracts and dependents could not be checked."
            )
        elif not references:
            limitations.append(
                "The code scan records no reference to the files in scope, so no dependent is "
                "listed. That is not proof that nothing depends on them."
            )

    # S10-10: with nothing to ground advice on, say so and stop before any model is asked.
    if bundle.status == "insufficient_query":
        reasons.append(TICKET_TOO_VAGUE)
        limitations.extend(bundle.suggestions)
        return finish("PARTIAL", risk)
    if not evidence:
        reasons.append(NO_HISTORY_FOUND)
        limitations.append("No similar past ticket, fix or design page was found.")
        return finish("PARTIAL", risk)

    context = EvidenceContext(
        snapshot_id=snapshot_id,
        ticket_title=ticket.title,
        ticket_text=ticket.text,
        evidence=tuple(
            EvidenceItem(
                evidence_id=item.id,
                kind=item.kind,
                title=item.title,
                text=item.snippet,
                relevance=item.relevance,
                counterevidence=tuple(item.notes),
            )
            for item in evidence
        ),
        facts=(
            f"Risk band: {risk.band}. {risk.explanation}",
            *(f"{factor.label}: {factor.value}" for factor in risk.factors if not factor.missing),
            *(
                f"Recorded recurring pattern ({lesson.status}): {lesson.title}"
                for lesson in bundle.lessons
            ),
        ),
    )
    step = clock()
    task = TaskKind.ENHANCEMENT_APPROACH if enhancement else TaskKind.BRIEF_DRAFT
    draft = gateway.generate(task, context, ledger)
    if draft.result is None:
        generation_ms = int((clock() - step) * 1000)
        reasons.append(draft.reason or "llm_unavailable")
        limitations.append(
            "No advice could be generated for this run; the evidence and risk factors found "
            "are listed as they are."
        )
        if not enhancement:
            return finish("PARTIAL", risk)
        # ENH-01: what code can state from recorded references needs no model.
        output: dict = {"advice": [], "limitations": []}
    else:
        drafted_by = draft.result.provider
        model_id = draft.result.model_version or draft.result.model_id
        prompt_version = draft.result.prompt_template_version
        output = draft.result.output
    texts = {
        item.id: EvidenceText(
            item.title, "\n".join([item.title, item.snippet, *item.notes]), item.url
        )
        for item in evidence
    }
    if enhancement:
        drafted = output.get("advice", [])
        items = [
            *guidance_rules.contract_items(evidence),
            *(drafted if isinstance(drafted, list) else []),
        ]
        checked = validate(
            {"advice": guidance_rules.in_section_order(items)},
            texts,
            guidance_rules.SECTION_CODES,
            guidance_rules.MAX_ITEMS,
            guidance_rules.MAX_PER_SECTION,
        )
    else:
        checked = validate(output, texts)
    advice = checked.advice
    if checked.removed_total:
        log.warning("advice_removed", removed=dict(checked.removed))
        limitations.append(
            f"{checked.removed_total} generated statement(s) were removed because their "
            "sources did not support them."
        )
    limitations.extend(_model_limitations(output))

    # S08-10 / S08-11: a second opinion only for conflicting evidence or a high-impact ticket.
    conflicted = {
        item.id for item in evidence if any("revert" in note.lower() for note in item.notes)
    }
    signals_for_review = set()
    if any(conflicted.intersection(item.evidence_ids) for item in advice) or any(
        lesson.status == "disputed" for lesson in bundle.lessons
    ):
        signals_for_review.add(ESCALATE_EVIDENCE_CONFLICT)
    if risk.band == "HIGH":
        signals_for_review.add(ESCALATE_HIGH_IMPACT)
    if cross_repository:
        # v1.11 routing: a second opinion on guidance when other repositories depend on this one.
        signals_for_review.add(guidance_rules.ESCALATE_CROSS_REPOSITORY)
    if advice and signals_for_review and drafted_by is not None:
        review = gateway.escalate(
            task,
            TaskKind.EVIDENCE_CRITIQUE,
            context.model_copy(
                update={
                    "draft": tuple(
                        item.model_dump(include={"kind", "statement", "support"}) for item in advice
                    )
                }
            ),
            ledger,
            signals_for_review,
            drafted_by,
        )
        if review.result is not None:
            reviewed_by = review.result.provider
            for found in consistency.disagreements(
                len(advice), review.result.output, drafted_by, reviewed_by
            ):
                advice[found.index].evidence_state = "DISPUTED"
                notes.append(
                    DisagreementNote(
                        advice_id=advice[found.index].advice_id,
                        drafted_by=found.drafted_by,
                        reviewed_by=found.reviewed_by,
                        verdict=found.verdict,  # type: ignore[arg-type]
                        reason=found.reason,
                    )
                )
    generation_ms = int((clock() - step) * 1000)

    if not advice:
        reasons.append(NO_SUPPORTED_ADVICE)
        return finish("PARTIAL", risk)
    if draft.result is None:
        # ENH-01: only the code-written items exist; the model's part is missing, so not complete.
        return finish("PARTIAL", risk)
    if bundle.status != "answered":
        # Advice exists but rests on thin history: publish it as PARTIAL and say so.
        reasons.append(WEAK_EVIDENCE)
        return finish("PARTIAL", risk)
    return finish("PUBLISHED", risk)
