"""S11-04 / S11-05: ask a model for the reproduction test and for the patch, and check the answer.

The model proposes; code decides whether the proposal may be used. A test must be one new file
in an allowed place with a prediction that can be wrong. A patch is only a list of whole files;
what it may touch is decided in :mod:`app.defect.guardrails`. Text the model wrote for people
(cause, summary, reasons) is bounded and dropped if it carries instructions or secrets.
"""

from __future__ import annotations

import re
from collections.abc import Callable, Collection, Sequence
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
# A test is named as its runner reports it: a method, "Class.Method" or "test_name[param]" (xUnit,
# NUnit, pytest), or the title given to it() / test() (vitest, Jest), describe titles joined by
# " > ". A title is any printable text without leading or trailing space.
_TEST_ID = re.compile(r"^[A-Za-z_][A-Za-z0-9_.\[\]-]{0,199}$")
_TEST_TITLE = re.compile(r"^(?=\S)[^\x00-\x1f\x7f]{1,200}(?<=\S)$")


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
    # Whether the model states that the test asserts the result the ticket reports. False when
    # it asserts an internal step, or does not say: such a test cannot prove the defect is fixed.
    direct: bool = False


@dataclass(frozen=True)
class PatchProposal:
    changes: tuple[ProposedChange, ...]
    cause: str
    summary: str
    limitations: tuple[str, ...]
    result: ModelResult
    # Existing tests the model states expect the behaviour the ticket reports as a defect:
    # (path, why). Stated with the fix, before anything is run.
    conflicts: tuple[tuple[str, str], ...] = ()


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


# Adding a test to an existing test file. Existing lines may not change, so the tests already
# there cannot be edited or removed; new lines may go in two places only, so they cannot be
# slipped into the body of an existing test (a `return` there would switch it off): import lines
# anywhere, and a block at the end of the file, where only closing brackets may follow it.
_CLOSERS = re.compile(r"[\s})\];,]*")
_IMPORT_STATEMENT = re.compile(
    r"""\s*(?:"""
    r"""import\s+(?:type\s+)?(?:[\w*{}\s,$]+?\s+from\s+)?["'][^"']+["']\s*;?"""  # JS / TS
    r"""|import\s+(?:static\s+)?[\w.*]+(?:\s+as\s+\w+)?\s*;?"""  # Python, Java, Kotlin
    r"""|from\s+[\w.]+\s+import\s+[\w*,\s()]+"""  # Python
    r"""|(?:global\s+)?using\s+(?:static\s+)?[\w.]+(?:\s*=\s*[\w.<>,\s]+)?\s*;"""  # C#
    r""")\s*"""
)


def _only_imports(lines: Sequence[str]) -> bool:
    written = [line for line in lines if line.strip()]
    if all(_IMPORT_STATEMENT.fullmatch(line) for line in written):
        return True
    # One import written over several lines: judge the statements, not the lines.
    joined = " ".join(line.strip() for line in written)
    statements = [part for part in re.split(r"(?<=;)\s*", joined) if part]
    return bool(statements) and all(_IMPORT_STATEMENT.fullmatch(part) for part in statements)


def add_to_existing(before: str, after: str) -> tuple[str | None, str | None]:
    """``after`` as an addition to the test file ``before``: (the file to write, None), or
    (None, why it is refused).

    The file written keeps every existing line byte for byte (and the file's line endings), so
    the pull request shows only the lines that were added.
    """
    mark = "\ufeff" if before.startswith("\ufeff") else ""
    newline = "\r\n" if "\r\n" in before else "\n"
    old = before.removeprefix("\ufeff").splitlines()
    merged: list[str] = []
    added: list[tuple[int, list[str]]] = []  # (existing lines before it, the lines added)
    kept = 0
    for line in after.removeprefix("\ufeff").splitlines():
        if kept < len(old) and line.rstrip() == old[kept].rstrip():
            merged.append(old[kept])
            kept += 1
            continue
        if added and added[-1][0] == kept:
            added[-1][1].append(line)
        else:
            added.append((kept, [line]))
        merged.append(line.rstrip())
    if kept < len(old):
        return None, (
            "An existing test file may only be added to: every existing line must stay, "
            f"unchanged and in order (line {kept + 1} of the existing file is missing or changed)."
        )
    if not any(line.strip() for _, lines in added for line in lines):
        return None, "The existing test file was returned unchanged: no test was added."
    for position, lines in added:
        at_the_end = all(_CLOSERS.fullmatch(rest) for rest in old[position:])
        if not at_the_end and not _only_imports(lines):
            return None, (
                "New lines may be added to an existing test file only as import lines, or as a "
                "new test at the end of the file (before its closing brackets); nothing may be "
                "inserted between or inside the existing tests."
            )
    return mark + newline.join(merged) + newline, None


def propose_test(
    gateway: ModelGateway,
    ledger: RunLedger,
    context: EvidenceContext,
    policy: RepositoryPolicy,
    baseline_files: Collection[str],
    tier: str | None = None,
    read_existing: Callable[[str], str | None] | None = None,
) -> TestProposal | Declined:
    """A reproduction test: a new test file, or a test added to an existing test file.

    ``read_existing`` returns a file at the baseline; without it only a new file is accepted.
    """
    outcome = gateway.generate(TaskKind.REPRODUCTION_TEST, context, ledger, tier)
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
    # Compared without case: on Windows and macOS the other spelling is the same file.
    existing_file = next((known for known in baseline_files if known.lower() == lowered), None)
    before = ""
    if existing_file is not None:
        # The path already passed the test-file rules above, so this is an existing test file.
        previous = read_existing(existing_file) if read_existing and existing_file == path else None
        if previous is None:
            return invalid(
                "The test must be a new file, or an existing test file with the test added to it."
            )
        merged, problem = add_to_existing(previous, content)
        if merged is None:
            return invalid(problem or "The existing test file could not be added to.")
        before, content = previous, merged
    if any(existing.lower().startswith(f"{lowered}/") for existing in baseline_files):
        return invalid("The test path is an existing directory.")
    titled = not _TEST_ID.match(test_id)
    if titled and not _TEST_TITLE.match(test_id):
        return invalid("The test id is not a test name.")
    if not content.strip() or len(content.encode("utf-8")) > policy.limits.max_file_bytes:
        return invalid("The test file is empty or larger than the limit.")
    named = test_id.split(" > ")[-1] if titled else test_id.rsplit(".", 1)[-1].split("[", 1)[0]
    if named not in content:
        return invalid("The test file does not define the test it names.")
    if adds_secret(before, content):
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
        direct=output.get("checks") == "reported_behaviour",
    )


def propose_patch(
    gateway: ModelGateway, ledger: RunLedger, context: EvidenceContext, tier: str | None = None
) -> PatchProposal | Declined:
    outcome = gateway.generate(TaskKind.CODE_FIX, context, ledger, tier)
    if outcome.result is None:
        return Declined(outcome.reason or "llm_unavailable", "No model produced a patch.", llm=True)
    output = outcome.result.output
    changes = tuple(
        ProposedChange(str(change["path"]), str(change["content"]))
        for change in output["changes"][:MAX_CHANGES]
    )
    conflicts = tuple(
        (str(item["path"]), clean(item.get("reason")))
        for item in (output.get("conflicting_tests") or [])[:MAX_CHANGES]
        if isinstance(item, dict) and item.get("path")
    )
    return PatchProposal(
        changes,
        clean(output.get("cause")),
        clean(output.get("summary")),
        _limitations(output),
        outcome.result,
        conflicts,
    )
