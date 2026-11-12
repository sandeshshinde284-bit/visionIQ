"""A deterministic stand-in for a model, for tests and offline demos (LLM_MODE=offline).

Like the hashing embedder, it is never used in production. It does not reason: it restates what
the evidence items already say (title, a quote, whether a fix was reverted), so a brief built
with it is still grounded and still passes the same validation as a real model's output.
"""

from __future__ import annotations

import re
from collections.abc import Mapping
from typing import Any

from app.llm.types import (
    EvidenceContext,
    EvidenceItem,
    ModelPolicy,
    ModelResult,
    TaskKind,
    TaskSpec,
)

MAX_ADVICE = 4
_QUOTE_LENGTH = 110


def _quote(text: str) -> str:
    """A verbatim piece of the evidence text: its first sentence, or its first words."""
    body = text.strip().strip("…").strip()
    sentence = re.split(r"(?<=[.!?])\s", body, maxsplit=1)[0]
    if len(sentence) > _QUOTE_LENGTH:
        sentence = sentence[:_QUOTE_LENGTH].rsplit(" ", 1)[0]
    return sentence.strip()


def _reverted(item: EvidenceItem) -> bool:
    return any("reverted" in note.lower() for note in item.counterevidence)


class OfflineProvider:
    """``code_script`` (DEFECT_OFFLINE_SCRIPT) holds prepared answers for the code tasks:
    ``{"reproduction_test": {...}, "code_fix": [{...}, ...]}``. A stand-in cannot write a test
    or a patch, so a demo supplies them; the runner still executes them for real. Without a
    script the code tasks return nothing and the run reports that no model was available.
    """

    name = "offline"
    model_id = "offline-extractive-1"

    def __init__(self, code_script: Mapping[str, Any] | None = None) -> None:
        self._code_script = dict(code_script or {})
        self._code_calls: dict[str, int] = {}

    def generate(
        self, task: TaskSpec, context: EvidenceContext, policy: ModelPolicy
    ) -> ModelResult:
        if task.kind is TaskKind.EVIDENCE_CRITIQUE:
            output: dict = {"verdicts": self._verdicts(context)}
        elif task.kind is TaskKind.BRIEF_DRAFT:
            output = self._draft(context)
        elif task.kind in (TaskKind.REPRODUCTION_TEST, TaskKind.CODE_FIX):
            output = self._scripted(task.kind)
        else:
            output = {}
        cited = sorted(
            {
                support["evidence_id"]
                for advice in output.get("advice", [])
                for support in advice["support"]
            }
        )
        if not output and task.kind in (TaskKind.REPRODUCTION_TEST, TaskKind.CODE_FIX):
            # No prepared answer: say so, instead of an empty object that fails the schema.
            return ModelResult(
                provider=self.name,
                model_id=self.model_id,
                prompt_template_version=task.prompt_template_version,
                evidence_snapshot_id=context.snapshot_id,
                status="ERROR",
                error_code="offline_no_answer",
            )
        return ModelResult(
            provider=self.name,
            model_id=self.model_id,
            prompt_template_version=task.prompt_template_version,
            evidence_snapshot_id=context.snapshot_id,
            output=output,
            cited_evidence_ids=cited,
            # Rough sizes, so budgets behave as they would with a real model.
            input_tokens=(len(task.system_prompt) + len(task.user_prompt)) // 4,
            output_tokens=len(str(output)) // 4,
            elapsed_ms=0,
        )

    def _scripted(self, kind: TaskKind) -> dict:
        """The next prepared answer for ``kind``; the last one repeats when the list runs out."""
        answers = self._code_script.get(kind.value)
        if isinstance(answers, Mapping):
            answers = [answers]
        if not answers:
            return {}
        index = self._code_calls.get(kind.value, 0)
        self._code_calls[kind.value] = index + 1
        return dict(answers[min(index, len(answers) - 1)])

    @staticmethod
    def _draft(context: EvidenceContext) -> dict:
        advice: list[dict] = []
        first_case = True
        # Loosely related results are shown as evidence but are no basis for advice.
        strong = [item for item in context.evidence if item.relevance == "strong"]
        for item in strong:
            quote = _quote(item.text)
            if not quote:
                continue
            title = item.title.strip() or item.kind
            if item.kind == "lesson":
                kind, statement = "RISK", f'This area has a recurring pattern: "{title}".'
            elif _reverted(item):
                kind = "DONT"
                statement = f'Do not repeat the fix recorded for "{title}": it was later reverted.'
            elif first_case:
                first_case = False
                kind = "APPROACH"
                statement = f'Start from "{title}": it is the closest past case to this ticket.'
            else:
                kind = "DO"
                statement = f'Check how "{title}" was resolved before changing the same code.'
            advice.append(
                {
                    "kind": kind,
                    "statement": statement,
                    "support": [{"evidence_id": item.evidence_id, "quote": quote}],
                }
            )
            if len(advice) == MAX_ADVICE:
                break
        limitations = ["Advice was assembled without a language model (offline mode)."]
        return {"advice": advice, "limitations": limitations}

    @staticmethod
    def _verdicts(context: EvidenceContext) -> list[dict]:
        by_id = {item.evidence_id: item for item in context.evidence}
        verdicts = []
        for index, draft in enumerate(context.draft):
            cited = [by_id.get(s.get("evidence_id", "")) for s in draft.get("support", [])]
            conflict = draft.get("kind") == "DO" and any(item and _reverted(item) for item in cited)
            verdicts.append(
                {
                    "index": index,
                    "verdict": "conflicting" if conflict else "supported",
                    "reason": "A cited fix was later reverted."
                    if conflict
                    else "The cited evidence matches the statement.",
                }
            )
        return verdicts
