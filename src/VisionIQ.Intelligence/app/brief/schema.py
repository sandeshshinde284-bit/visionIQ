"""S10-04: the structured brief (master design 7.4, 7.4.1, 7.5).

This model is the contract between Intelligence, which stores the brief, and the API, which
reads it by id and posts it on the ticket. ``contracts/briefs/brief.v1.schema.json`` is generated
from it (``python -m app.brief.schema``) and a test fails when the two drift apart.
"""

from __future__ import annotations

import json
import sys
import uuid
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field

SCHEMA_VERSION = 1

BriefState = Literal["PUBLISHED", "PARTIAL", "BLOCKED"]
RiskBand = Literal["LOW", "MODERATE", "HIGH", "INSUFFICIENT_DATA"]
AdviceKind = Literal["RISK", "DO", "DONT", "APPROACH"]
# What checks say about the advice. A new brief only ever has these two: engineers' ratings
# (workflow.advice_feedback) are a separate dimension and never make advice "correct" (8.10).
EvidenceState = Literal["UNVERIFIED", "DISPUTED"]


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid")


class TicketRef(_Model):
    system: Literal["ado"] = "ado"
    project: str
    id: str
    revision: int = Field(ge=1)
    title: str = ""


class Evidence(_Model):
    """One source the brief cites. ``url`` opens it in ADO, Git or Confluence."""

    id: str = Field(pattern=r"^E[0-9]{1,3}$")
    item_key: str
    kind: str
    title: str
    url: str | None
    revision: str | None
    snippet: str
    relevance: Literal["strong", "related"]
    notes: list[str] = Field(default_factory=list)


class ScopeItem(_Model):
    """S10-02: a file or area this ticket probably touches, and why we think so."""

    path: str
    basis: Literal["named_in_ticket", "stack_trace_symbol", "changed_by_past_fix"]
    confidence: Literal["exact", "inferred"]
    evidence_ids: list[str] = Field(default_factory=list)


class RiskFactor(_Model):
    """S10-03: one observed count behind the band. ``missing`` means the data was not there."""

    code: str = Field(pattern=r"^[a-z_]{1,40}$")
    label: str
    value: int | None
    window_days: int | None = None
    missing: bool = False
    # Ticket, PR and commit ids the count is made of, plus brief evidence ids where they match.
    references: list[str] = Field(default_factory=list)
    evidence_ids: list[str] = Field(default_factory=list)


class Risk(_Model):
    """S10-13: an ordinal band with its factors. Never a probability."""

    band: RiskBand
    explanation: str
    factors: list[RiskFactor]


class Support(_Model):
    evidence_id: str
    quote: str


class Advice(_Model):
    """7.4.1: one atomic, individually rateable piece of advice."""

    advice_id: str = Field(pattern=r"^ADV-[0-9]{3}$")
    kind: AdviceKind
    statement: str = Field(min_length=1, max_length=1000)
    evidence_ids: list[str] = Field(min_length=1)
    support: list[Support] = Field(min_length=1)
    evidence_state: EvidenceState = "UNVERIFIED"


class DisagreementNote(_Model):
    advice_id: str
    drafted_by: str
    reviewed_by: str
    verdict: Literal["unsupported", "conflicting"]
    reason: str


class InjectionFlag(_Model):
    """Instruction-like text found in a source. It was treated as data and not followed."""

    source: str
    pattern: str


class Generation(_Model):
    """Which model wrote the advice, with which prompt and routing (10.9 rule 4)."""

    provider: str | None
    model_id: str | None
    prompt_template_version: str | None
    routing_revision: str
    reviewed_by: str | None = None
    calls: int = 0
    input_tokens: int = 0
    output_tokens: int = 0


class Timings(_Model):
    """S10-11: stage times inside Intelligence, in milliseconds."""

    retrieval_ms: int = Field(ge=0)
    generation_ms: int = Field(ge=0)
    total_ms: int = Field(ge=0)


class Brief(_Model):
    schema_version: Literal[1] = SCHEMA_VERSION
    brief_id: uuid.UUID
    run_id: uuid.UUID
    ticket: TicketRef
    state: BriefState
    reason_codes: list[str] = Field(default_factory=list)
    snapshot_id: str | None = None
    predicted_scope: list[ScopeItem] = Field(default_factory=list)
    risk: Risk
    advice: list[Advice] = Field(default_factory=list)
    evidence: list[Evidence] = Field(default_factory=list)
    disagreements: list[DisagreementNote] = Field(default_factory=list)
    # S10-10: what the brief could not establish, said plainly.
    limitations: list[str] = Field(default_factory=list)
    injection_flags: list[InjectionFlag] = Field(default_factory=list)
    generation: Generation
    timings: Timings

    def advice_ids(self) -> list[str]:
        return [item.advice_id for item in self.advice]

    def evidence_ids(self) -> list[str]:
        return [item.id for item in self.evidence]


def json_schema() -> dict:
    schema = Brief.model_json_schema()
    schema["$schema"] = "https://json-schema.org/draft/2020-12/schema"
    schema["$id"] = "https://visioniq.internal/schemas/briefs/brief.v1.schema.json"
    schema["title"] = "Ticket brief v1 (S10-04)"
    return schema


def schema_text() -> str:
    return json.dumps(json_schema(), indent=2, sort_keys=True) + "\n"


def find_schema_file(start: Path | None = None) -> Path:
    for directory in (start or Path(__file__).resolve()).parents:
        if (directory / "contracts" / "events").is_dir():
            return directory / "contracts" / "briefs" / "brief.v1.schema.json"
    raise FileNotFoundError("contracts directory not found")


if __name__ == "__main__":  # regenerate the committed schema after changing the model
    target = find_schema_file()
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(schema_text(), encoding="utf-8", newline="\n")
    sys.stdout.write(f"wrote {target}\n")
