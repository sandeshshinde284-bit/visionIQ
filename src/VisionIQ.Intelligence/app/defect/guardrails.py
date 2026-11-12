"""S11-06: layer and restriction checks on a proposed patch (master design 8.6.4).

These are executable rules, not a sentence in a prompt. A ``block`` finding rejects the
proposal (the model may try again inside its budget). A ``review`` finding stops the run for an
engineer whatever the size of the change: security, payment, identity, schema migration and
hardware-control areas, secrets, dependency versions, and a change broader than the repository's
budget. There is no universal "five files is safe" number; the budget is the repository's own.
"""

from __future__ import annotations

import difflib
from collections.abc import Callable, Collection, Sequence
from dataclasses import dataclass
from typing import Literal

from app.defect.policy import RepositoryPolicy, matches
from app.defect.schema import FileChange, PolicyFinding
from app.defect.workspace import is_safe_path, sha256_text
from app.knowledge.redaction import redact

_SECRET_KINDS = ("private_key", "token", "secret", "auth_header")

PolicyStatus = Literal["PASS", "REVIEW_REQUIRED", "BLOCKED"]


@dataclass(frozen=True)
class ProposedChange:
    path: str
    content: str


@dataclass(frozen=True)
class PolicyResult:
    status: PolicyStatus
    findings: tuple[PolicyFinding, ...]
    files: tuple[FileChange, ...]

    def feedback(self) -> str:
        """What the model is told about a rejected proposal: rule codes and paths only."""
        return "; ".join(
            f"{finding.code}{f' ({finding.path})' if finding.path else ''}"
            for finding in self.findings
        )


def _line_changes(old: str, new: str) -> tuple[int, int]:
    added = removed = 0
    for line in difflib.unified_diff(old.splitlines(), new.splitlines(), lineterm="", n=0):
        if line.startswith("+") and not line.startswith("+++"):
            added += 1
        elif line.startswith("-") and not line.startswith("---"):
            removed += 1
    return added, removed


def _secret_count(text: str) -> int:
    counts = redact(text).counts
    return sum(counts.get(kind, 0) for kind in _SECRET_KINDS)


def adds_secret(old: str, new: str) -> bool:
    """Whether ``new`` has credential-like text that ``old`` did not have.

    Judged line by line, so replacing one key with another counts: the number of secrets stays
    the same, but the line that carries the new one is new.
    """
    if _secret_count(new) == 0:
        return False
    before = {line.strip() for line in old.splitlines()}
    return any(
        line.strip() not in before and _secret_count(line) > 0 for line in new.splitlines()
    ) or _secret_count(new) > _secret_count(old)


def check(
    changes: Sequence[ProposedChange],
    policy: RepositoryPolicy,
    read_baseline: Callable[[str], str | None],
    reproduction_test: str,
    shown: Collection[str],
    baseline_files: Collection[str] = (),
) -> PolicyResult:
    """Judge a proposal against the repository's policy before anything is written or run.

    ``shown`` are the files the model was given in full. A patch replaces whole files, so an
    existing file the model did not see (or saw cut short) may not be changed: the rest of it
    would be lost. ``baseline_files`` are the files of the baseline commit: whether a path
    exists is decided from that list, not from whether the file could be read as text.
    """
    existing = set(baseline_files)
    folded = {name.lower(): name for name in existing}
    directories = {
        name[:index].lower() for name in existing for index, char in enumerate(name) if char == "/"
    }
    findings: list[PolicyFinding] = []
    files: list[FileChange] = []

    def block(code: str, path: str | None, detail: str) -> None:
        findings.append(PolicyFinding(code=code, severity="block", path=path, detail=detail))

    def review(code: str, path: str | None, detail: str) -> None:
        findings.append(PolicyFinding(code=code, severity="review", path=path, detail=detail))

    if not changes:
        block("no_change", None, "The proposal changes no file.")
    seen: set[str] = set()
    for change in changes:
        path = change.path
        if not is_safe_path(path):
            # The path is model text: it is not echoed back.
            block("unsafe_path", None, "A path is absolute, leaves the repository or is malformed.")
            continue
        if path in seen:
            block("duplicate_path", path, "The same file is changed twice.")
            continue
        seen.add(path)
        if len(change.content.encode("utf-8")) > policy.limits.max_file_bytes * 4:
            block("file_too_large", path, "The new file content is larger than the limit.")
            continue
        lowered = path.lower()
        parents = [lowered[:index] for index, char in enumerate(lowered) if char == "/"]
        if lowered in directories or any(parent in folded for parent in parents):
            block("path_not_a_file", path, "The path is a directory, or lies under a file.")
            continue
        if lowered in folded and folded[lowered] != path:
            # On a case-insensitive file system this would overwrite the existing file.
            block("path_case_conflict", path, "The path differs from an existing file by case.")
            continue
        old = read_baseline(path)
        if old is None and path in existing:
            # Binary or not UTF-8: it cannot have been shown, so replacing it would destroy it.
            block("file_not_text", path, "The existing file is not text and may not be replaced.")
            continue
        if old is not None and old == change.content:
            block("unchanged_file", path, "The proposed content equals the baseline.")
            continue
        if path == reproduction_test or matches(path, policy.test_file_patterns):
            block("test_changed", path, "A fix may not add, edit or weaken tests.")
            continue
        if old is not None and path not in shown:
            block("file_not_shown", path, "Only files that were provided in full may be edited.")
            continue
        if matches(path, policy.generated_paths):
            block("generated_artifact", path, "Generated or binary output is not source.")
            continue
        if matches(path, policy.secret_paths):
            review("secret_change", path, "The path holds secrets or credentials.")
        elif not matches(path, policy.writable_paths):
            block("path_not_writable", path, "The path is outside the writable paths.")
            continue
        if matches(path, policy.dependency_files):
            review("dependency_change", path, "Dependency versions need an owner's approval.")
        for area in policy.restricted_areas:
            if matches(path, area.patterns):
                review(
                    f"restricted_area_{area.area}",
                    path,
                    f"The path is in the {area.area} area, which needs an engineer's review.",
                )
        if adds_secret(old or "", change.content):
            review("secret_content", path, "The change adds text that looks like a credential.")
        added, removed = _line_changes(old or "", change.content)
        files.append(
            FileChange(
                path=path,
                change="added" if old is None else "modified",
                sha256=sha256_text(change.content),
                added_lines=added,
                removed_lines=removed,
                content=change.content,
            )
        )

    limits = policy.limits
    lines = sum(file.added_lines + file.removed_lines for file in files)
    if len(files) > limits.max_changed_files or lines > limits.max_changed_lines:
        review(
            "patch_too_broad",
            None,
            f"{len(files)} file(s) and {lines} changed line(s); this repository's budget is "
            f"{limits.max_changed_files} file(s) and {limits.max_changed_lines} line(s).",
        )

    if any(finding.severity == "block" for finding in findings):
        status: PolicyStatus = "BLOCKED"
    elif findings:
        status = "REVIEW_REQUIRED"
    else:
        status = "PASS"
    return PolicyResult(status, tuple(findings), tuple(files))
