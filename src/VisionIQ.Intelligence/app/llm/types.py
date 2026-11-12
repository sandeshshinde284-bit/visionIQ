"""S08-01: the provider-neutral model interface (master design 10.9).

Every provider takes the same task, bounded evidence and policy and returns the same result
shape, so routing, budgets, logging and validation do not depend on which model answered. A model
only proposes: whether an evidence id exists, whether access is allowed and whether advice is
supported are decided by code, never by the model.
"""

from __future__ import annotations

import uuid
from enum import StrEnum
from typing import Any, Literal, Protocol

from pydantic import BaseModel, ConfigDict, Field


class TaskKind(StrEnum):
    """The rows of the v1.11 routing matrix."""

    TICKET_CLASSIFICATION = "ticket_classification"
    BRIEF_DRAFT = "brief_draft"
    EVIDENCE_CRITIQUE = "evidence_critique"
    ROOT_CAUSE = "root_cause"
    # DEF-01: the failing test that reproduces a defect. Code leaves the process, as for a fix.
    REPRODUCTION_TEST = "reproduction_test"
    CODE_FIX = "code_fix"
    ENHANCEMENT_APPROACH = "enhancement_approach"
    KNOWLEDGE_EXTRACTION = "knowledge_extraction"
    ADVICE_ANALYTICS = "advice_analytics"


class DataClassification(StrEnum):
    PUBLIC = "PUBLIC"
    INTERNAL = "INTERNAL"
    RESTRICTED = "RESTRICTED"

    @property
    def level(self) -> int:
        return {"PUBLIC": 0, "INTERNAL": 1, "RESTRICTED": 2}[self.value]


CallStatus = Literal["OK", "REFUSED", "ERROR", "TIMEOUT"]
CallRole = Literal["primary", "fallback", "escalation"]


class EvidenceItem(BaseModel):
    """One retrieved, access-checked source excerpt. Its text is data, never instructions."""

    model_config = ConfigDict(frozen=True)

    evidence_id: str
    kind: str
    title: str
    text: str
    # "strong": several searches or a code-path match agree; "related": one search only.
    relevance: Literal["strong", "related"] = "strong"
    counterevidence: tuple[str, ...] = ()


class CodeFile(BaseModel):
    """One file of the pinned workspace given to a code task. Its content is data."""

    model_config = ConfigDict(frozen=True)

    path: str
    content: str
    truncated: bool = False


class EvidenceContext(BaseModel):
    """The bounded evidence a model may use for one task (the design's EvidenceBundle)."""

    model_config = ConfigDict(frozen=True)

    snapshot_id: str | None
    ticket_title: str
    ticket_text: str
    evidence: tuple[EvidenceItem, ...]
    # Deterministic facts computed by code (risk band, factor counts); the model may restate
    # them but cannot change them.
    facts: tuple[str, ...] = ()
    # Only for a critique: the draft advice being reviewed.
    draft: tuple[dict[str, Any], ...] = ()
    # Only for the code tasks (DEF-01, DEF-02): files at the pinned baseline, and what the
    # runner reported about earlier attempts of this run.
    code: tuple[CodeFile, ...] = ()
    feedback: tuple[str, ...] = ()


class TaskSpec(BaseModel):
    model_config = ConfigDict(frozen=True)

    kind: TaskKind
    run_id: uuid.UUID | None
    classification: DataClassification
    system_prompt: str
    user_prompt: str
    prompt_template_version: str
    output_schema: dict[str, Any]


class ModelPolicy(BaseModel):
    """Per-call limits, set by the gateway from what is left of the run's budget."""

    model_config = ConfigDict(frozen=True)

    timeout_seconds: float = Field(gt=0)
    max_output_tokens: int = Field(gt=0)


class ModelResult(BaseModel):
    provider: str
    model_id: str
    model_version: str | None = None
    prompt_template_version: str
    evidence_snapshot_id: str | None = None
    output: dict[str, Any] = Field(default_factory=dict)
    cited_evidence_ids: list[str] = Field(default_factory=list)
    input_tokens: int = 0
    output_tokens: int = 0
    elapsed_ms: int = 0
    invocation_id: uuid.UUID = Field(default_factory=uuid.uuid4)
    status: CallStatus = "OK"
    # A fixed code or exception type name; never provider message text (it can echo the prompt).
    error_code: str | None = None


class ModelProvider(Protocol):
    """A model behind the gateway. ``generate`` never raises: failures are a result status."""

    name: str
    model_id: str

    def generate(
        self, task: TaskSpec, context: EvidenceContext, policy: ModelPolicy
    ) -> ModelResult: ...


def failed(
    provider: ModelProvider,
    task: TaskSpec,
    context: EvidenceContext,
    status: CallStatus,
    error_code: str,
    elapsed_ms: int = 0,
) -> ModelResult:
    """The result for a call that produced no usable output."""
    return ModelResult(
        provider=provider.name,
        model_id=provider.model_id,
        prompt_template_version=task.prompt_template_version,
        evidence_snapshot_id=context.snapshot_id,
        elapsed_ms=elapsed_ms,
        status=status,
        error_code=error_code[:120],
    )
