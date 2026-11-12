"""S11-04 / S11-05: ask a model for the reproduction test and for the patch, and check the answer.

The model proposes; code decides whether the proposal may be used. A test must be one new file
in an allowed place with a prediction that can be wrong. A patch is only a list of whole files;
what it may touch is decided in :mod:`app.defect.guardrails`. Text the model wrote for people
(cause, summary, reasons) is bounded and dropped if it carries instructions or secrets.
"""

from __future__ import annotations

import re
from collections.abc import Collection, Sequence
from dataclasses import dataclass

from app.defect.guardrails import ProposedChange, adds_secret
from app.defect.policy import RepositoryPolicy, matches
from app.defect.results import is_specific
from app.defect.schema import ExpectedFailure
from app.defect.workspace import is_safe_path
from app.knowledge.redaction import redact
from app.llm.gateway import ModelGateway, RunLedger
from app.llm.injection import looks_like_instruction
from app.llm.types import CodeFile, EvidenceContext, ModelResult, TaskKind

REPRODUCTION_UNAVAILABLE = "reproduction_unavailable"
TEST_INVALID = "reproduction_test_invalid"
MAX_TEXT_CHARS = 600
MAX_CHANGES = 40
_TEST_ID = re.compile(r"^[A-Za-z_][A-Za-z0-9_.\[\]-]{0,199}$")


@dataclass(frozen=True)
class Declined:
    """There is no usable proposal. ``code`` is a reason code; ``llm`` marks a gateway failure."""

    code: str
    detail: str
    llm: bool = False


@dataclass(frozen=True)
class TestProposal:
    __test__ = False  # not a pytest class

    path: str
    test_id: str
    content: str
    expected: ExpectedFailure
    rationale: str
    limitations: tuple[str, ...]
    result: ModelResult


@dataclass(frozen=True)
class PatchProposal:
    changes: tuple[ProposedChange, ...]
    cause: str
    summary: str
    limitations: tuple[str, ...]
    result: ModelResult


def clean(text: object, limit: int = MAX_TEXT_CHARS) -> str:
    """Model text for a person: one line, bounded, and empty if it is not safe to show."""
    line = " ".join(str(text or "").split())[:limit]
    if not line or looks_like_instruction(line) or redact(line).counts:
        return ""
    return line


def _limitations(output: dict) -> tuple[str, ...]:
    return tuple(
        line for line in (clean(text, 240) for text in output.get("limitations", [])[:3]) if line
    )


def context_for(
    ticket_title: str,
    ticket_text: str,
    facts: Sequence[str],
    code: Sequence[CodeFile],
    feedback: Sequence[str] = (),
) -> EvidenceContext:
    return EvidenceContext(
        snapshot_id=None,
        ticket_title=ticket_title,
        ticket_text=ticket_text,
        evidence=(),
        facts=tuple(facts),
        code=tuple(code),
        feedback=tuple(feedback),
    )


def propose_test(
    gateway: ModelGateway,
    ledger: RunLedger,
    context: EvidenceContext,
    policy: RepositoryPolicy,
    baseline_files: Collection[str],
) -> TestProposal | Declined:
    outcome = gateway.generate(TaskKind.REPRODUCTION_TEST, context, ledger)
    if outcome.result is None:
        return Declined(outcome.reason or "llm_unavailable", "No model produced a test.", llm=True)
    output = outcome.result.output
    if not output.get("reproducible", False):
        reason = clean(output.get("unavailable_reason")) or "The model gave no reason."
        return Declined(REPRODUCTION_UNAVAILABLE, reason)

    path = str(output.get("test_path", ""))
    test_id = str(output.get("test_id", ""))
    content = str(output.get("test_content", ""))
    expected_raw = output.get("expected_failure")

    def invalid(detail: str) -> Declined:
        return Declined(TEST_INVALID, detail)

    if not is_safe_path(path):
        return invalid("The test path is not a plain path inside the repository.")
    if not matches(path, policy.new_test_paths) or not matches(path, policy.test_file_patterns):
        return invalid("The test file is not in a place where this repository keeps its tests.")
    lowered = path.lower()
    if any(lowered == existing.lower() for existing in baseline_files):
        # Compared without case: on Windows and macOS the other spelling is the same file.
        return invalid("The test must be a new file; an existing file may not be edited.")
    if any(existing.lower().startswith(f"{lowered}/") for existing in baseline_files):
        return invalid("The test path is an existing directory.")
    if not _TEST_ID.match(test_id):
        return invalid("The test id is not a test name.")
    if not content.strip() or len(content.encode("utf-8")) > policy.limits.max_file_bytes:
        return invalid("The test file is empty or larger than the limit.")
    if test_id.rsplit(".", 1)[-1].split("[", 1)[0] not in content:
        return invalid("The test file does not define the test it names.")
    if adds_secret("", content):
        # The test is committed with the fix and shown on the pull request.
        return invalid("The test file contains text that looks like a credential.")
    if not isinstance(expected_raw, dict):
        return invalid("The expected failure on the current code was not stated.")
    expected = ExpectedFailure(
        exception=str(expected_raw["exception"])[:120] if expected_raw.get("exception") else None,
        message_contains=[str(fragment)[:200] for fragment in expected_raw["message_contains"]],
    )
    if not is_specific(expected):
        return invalid("The expected failure is too generic to tell this defect from any failure.")
    return TestProposal(
        path,
        test_id,
        content,
        expected,
        clean(output.get("rationale")),
        _limitations(output),
        outcome.result,
    )


def propose_patch(
    gateway: ModelGateway, ledger: RunLedger, context: EvidenceContext
) -> PatchProposal | Declined:
    outcome = gateway.generate(TaskKind.CODE_FIX, context, ledger)
    if outcome.result is None:
        return Declined(outcome.reason or "llm_unavailable", "No model produced a patch.", llm=True)
    output = outcome.result.output
    changes = tuple(
        ProposedChange(str(change["path"]), str(change["content"]))
        for change in output["changes"][:MAX_CHANGES]
    )
    return PatchProposal(
        changes,
        clean(output.get("cause")),
        clean(output.get("summary")),
        _limitations(output),
        outcome.result,
    )
