"""S10-07: every advice item must be backed by the source it cites, or it is removed.

A model's draft is only a proposal. An item stays in the brief when, for at least one cited
evidence id, (1) that id is in the evidence given to the model, (2) the quote the model gave is
found word for word in that evidence, and (3) the statement is about what that evidence says
(it shares its specific terms). Checking only that the id exists would let a model cite a real
source for a claim the source never makes. Items are also dropped when they carry instructions,
unknown links or secrets (S15-10). Advice ids are assigned here, after the checks.
"""

from __future__ import annotations

import re
from collections import Counter
from collections.abc import Collection, Mapping
from dataclasses import dataclass, field
from typing import Any

from app.brief.schema import Advice, Support
from app.knowledge.redaction import redact
from app.llm.injection import looks_like_instruction

MAX_ADVICE = 8
MIN_QUOTE_CHARS = 12
MAX_STATEMENT_CHARS = 1000
_KINDS = ("RISK", "DO", "DONT", "APPROACH")
_URL = re.compile(r"(?i)\b(?:https?://|www\.)\S+")
_WORD = re.compile(r"[a-z][a-z0-9_]{3,}")
# Words that say nothing about a specific defect, so sharing them proves nothing.
_COMMON = frozenset(
    {
        "about",
        "after",
        "again",
        "also",
        "before",
        "been",
        "being",
        "change",
        "changed",
        "changes",
        "check",
        "code",
        "could",
        "does",
        "done",
        "each",
        "fixed",
        "from",
        "have",
        "into",
        "past",
        "same",
        "should",
        "start",
        "that",
        "their",
        "them",
        "then",
        "there",
        "these",
        "this",
        "ticket",
        "using",
        "when",
        "where",
        "which",
        "while",
        "with",
        "without",
        "would",
        "your",
    }
)

# Typographic quotes and the ellipsis, as models and editors like to write them.
_QUOTES = (
    (chr(0x2018), "'"),
    (chr(0x2019), "'"),
    (chr(0x201C), '"'),
    (chr(0x201D), '"'),
    (chr(0x2026), " "),
)

# Why an item was removed (fixed codes, reported on the brief and in the log).
MALFORMED = "malformed"
UNSAFE = "unsafe_content"
UNKNOWN_EVIDENCE = "unknown_evidence"
QUOTE_NOT_FOUND = "quote_not_in_source"
NOT_GROUNDED = "statement_not_about_source"
DUPLICATE = "duplicate"
OVER_LIMIT = "over_limit"


@dataclass(frozen=True)
class EvidenceText:
    """What the model was shown for one evidence id, and where that source lives."""

    title: str
    text: str
    url: str | None = None


@dataclass
class ValidationResult:
    advice: list[Advice] = field(default_factory=list)
    removed: Counter[str] = field(default_factory=Counter)

    @property
    def removed_total(self) -> int:
        return sum(self.removed.values())


def normalize(text: str) -> str:
    """Lower case, straight quotes, single spaces: how quotes and sources are compared."""
    for fancy, plain in _QUOTES:
        text = text.replace(fancy, plain)
    return " ".join(text.casefold().split()).strip(" \"'.")


def _terms(text: str) -> set[str]:
    return {word for word in _WORD.findall(text.casefold()) if word not in _COMMON}


def _grounded(statement: str, source: EvidenceText) -> bool:
    """The statement shares specific terms with the source it cites."""
    stated = _terms(statement)
    shared = stated & _terms(f"{source.title} {source.text}")
    return len(shared) >= (2 if len(stated) >= 4 else 1)


def _unsafe(statement: str, evidence: Mapping[str, EvidenceText]) -> bool:
    if looks_like_instruction(statement) or redact(statement).counts:
        return True
    known = {source.url.rstrip("/").casefold() for source in evidence.values() if source.url}
    return any(link.rstrip(".,;)/").casefold() not in known for link in _URL.findall(statement))


def validate(
    draft: Mapping[str, Any],
    evidence: Mapping[str, EvidenceText],
    sections: Collection[str] | None = None,
    limit: int = MAX_ADVICE,
    per_section: int | None = None,
) -> ValidationResult:
    """Keep the supported advice of a model draft, in order, and number it ADV-001, ADV-002...

    With ``sections`` (ENH-01) every item must name one of them; an item without a known section
    is malformed, and at most ``per_section`` items are kept for each.
    """
    result = ValidationResult()
    seen: set[str] = set()
    kept_in: Counter[str] = Counter()
    for item in draft.get("advice", []):
        if not isinstance(item, Mapping):
            result.removed[MALFORMED] += 1
            continue
        kind, statement = item.get("kind"), item.get("statement")
        section = item.get("section") if sections is not None else None
        if (
            (sections is not None and section not in sections)
            or kind not in _KINDS
            or not isinstance(statement, str)
            or not statement.strip()
            or len(statement) > MAX_STATEMENT_CHARS
        ):
            result.removed[MALFORMED] += 1
            continue
        statement = " ".join(statement.split())
        if _unsafe(statement, evidence):
            result.removed[UNSAFE] += 1
            continue
        supported: list[Support] = []
        reason = UNKNOWN_EVIDENCE
        for support in item.get("support") or []:
            if not isinstance(support, Mapping):
                continue
            evidence_id, quote = support.get("evidence_id"), support.get("quote")
            source = evidence.get(evidence_id) if isinstance(evidence_id, str) else None
            if source is None or not isinstance(quote, str):
                continue  # a fabricated or missing id supports nothing
            if looks_like_instruction(quote):
                reason = UNSAFE  # never carried on to a reviewer as if it were our text
                continue
            wanted = normalize(quote)
            if len(wanted) < MIN_QUOTE_CHARS or wanted not in normalize(source.text):
                reason = QUOTE_NOT_FOUND
                continue
            if not _grounded(statement, source):
                reason = NOT_GROUNDED
                continue
            if all(kept.evidence_id != evidence_id for kept in supported):
                supported.append(Support(evidence_id=evidence_id, quote=" ".join(quote.split())))
        if not supported:
            result.removed[reason] += 1
            continue
        key = normalize(statement)
        if key in seen:
            result.removed[DUPLICATE] += 1
            continue
        if len(result.advice) >= limit or (
            per_section is not None and section is not None and kept_in[section] >= per_section
        ):
            result.removed[OVER_LIMIT] += 1
            continue
        if section is not None:
            kept_in[section] += 1
        seen.add(key)
        result.advice.append(
            Advice(
                advice_id=f"ADV-{len(result.advice) + 1:03d}",
                kind=kind,
                statement=statement,
                evidence_ids=[kept.evidence_id for kept in supported],
                support=supported,
                section=section,
            )
        )
    return result
