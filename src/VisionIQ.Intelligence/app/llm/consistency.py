"""S08-11: two providers looked at the same evidence. Expose where they disagree.

A reviewer's verdict can only add doubt. It never restores advice the citation check removed,
never rewrites advice, and the two opinions are never averaged or voted on (master design 10.9,
example 3). A disagreement is diagnostic context for the engineer, not verified knowledge.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any

from app.knowledge.redaction import redact
from app.llm.injection import looks_like_instruction

_DOUBT = ("unsupported", "conflicting")
MAX_REASON_CHARS = 240


@dataclass(frozen=True)
class Disagreement:
    """The reviewer did not agree with one advice item (by its position in the draft)."""

    index: int
    drafted_by: str
    reviewed_by: str
    verdict: str
    reason: str


def _safe_reason(text: object) -> str:
    """The reviewer's reason is model output about untrusted text: bound it and scrub it."""
    reason = " ".join(str(text).split())[:MAX_REASON_CHARS]
    if not reason or looks_like_instruction(reason) or redact(reason).counts:
        return "The reviewer did not find this statement supported by its sources."
    return reason


def disagreements(
    advice_count: int, critique: dict[str, Any], drafted_by: str, reviewed_by: str
) -> list[Disagreement]:
    """Verdicts other than "supported", one per advice item, ignoring indexes that do not exist."""
    found: dict[int, Disagreement] = {}
    for verdict in critique.get("verdicts", []):
        index = verdict.get("index")
        if (
            isinstance(index, int)
            and not isinstance(index, bool)
            and 0 <= index < advice_count
            and verdict.get("verdict") in _DOUBT
            and index not in found
        ):
            found[index] = Disagreement(
                index,
                drafted_by,
                reviewed_by,
                verdict["verdict"],
                _safe_reason(verdict.get("reason")),
            )
    return [found[index] for index in sorted(found)]
