"""Map actual Git diff hunks to parser-reported symbol line ranges.

No arbitrary nearest-function guessing. Changes outside known symbol spans are
FILE_ONLY. Deleted lines that have no new-file line never claim symbol impact.
"""

import difflib
import subprocess
from dataclasses import dataclass
from pathlib import Path

from app.code_intelligence.scanner import RepositoryScan


@dataclass(frozen=True)
class DiffImpact:
    source_commit: str
    target_commit: str
    old_path: str | None
    new_path: str | None
    change: str
    changed_new_lines: tuple[int, ...]
    symbols: tuple[str, ...]
    confidence: str


def _git(root: str, *args: str) -> bytes:
    result = subprocess.run(
        ["git", "-C", root, *args],
        check=False,
        capture_output=True,
        timeout=60,
    )
    if result.returncode:
        raise ValueError(f"Git diff operation failed: {args[0]}")
    return result.stdout


def _diff_names(root: str, base: str, head: str) -> tuple[tuple[str, str | None, str | None], ...]:
    parts = _git(root, "diff", "--no-ext-diff", "--find-renames", "--name-status", "-z", base, head)
    values = parts.split(b"\x00")
    output: list[tuple[str, str | None, str | None]] = []
    i = 0
    while i < len(values) and values[i]:
        change = values[i].decode("ascii", errors="replace")
        i += 1
        old = values[i].decode("utf-8", errors="surrogateescape")
        i += 1
        if change.startswith(("R", "C")):
            new = values[i].decode("utf-8", errors="surrogateescape")
            i += 1
        else:
            new = None if change.startswith("D") else old
            if change.startswith("A"):
                old = None
        output.append((change, old, new))
    return tuple(output)


def _lines(root: str, commit: str, path: str | None, max_bytes: int) -> list[str] | None:
    if path is None:
        return []
    raw = _git(root, "show", f"{commit}:{path}")
    if len(raw) > max_bytes or b"\x00" in raw:
        return None
    try:
        return raw.decode("utf-8").splitlines()
    except UnicodeDecodeError:
        return None


def map_diff_to_symbols(
    repository: str | Path,
    base_commit: str,
    head: RepositoryScan,
    max_source_bytes: int = 1_048_576,
) -> tuple[DiffImpact, ...]:
    """Compare a validated base Git commit to the exact scan revision."""
    if max_source_bytes < 1:
        raise ValueError("max_source_bytes must be positive")
    root = str(Path(repository).resolve())
    # Object-identity hashes prevent user-supplied revs from selecting options.
    base = _git(root, "rev-parse", "--verify", f"{base_commit}^{{commit}}").decode().strip()
    if base != base_commit:
        raise ValueError("base_commit must be a resolved commit SHA")
    if str(Path(head.repo_root).resolve()) != str(Path(root).resolve()):
        raise ValueError("Scan belongs to a different repository")
    indexed = {analysis.path: analysis for analysis in head.analyses}
    results: list[DiffImpact] = []
    for change, old_path, new_path in _diff_names(root, base, head.commit_sha):
        old_lines = _lines(root, base, old_path, max_source_bytes)
        new_lines = _lines(root, head.commit_sha, new_path, max_source_bytes)
        if old_lines is None or new_lines is None:
            results.append(
                DiffImpact(base, head.commit_sha, old_path, new_path, change, (), (), "FILE_ONLY")
            )
            continue
        changed: set[int] = set()
        for kind, _a1, _a2, b1, b2 in difflib.SequenceMatcher(
            None, old_lines, new_lines, autojunk=False
        ).get_opcodes():
            if kind in {"insert", "replace"}:
                changed.update(range(b1 + 1, b2 + 1))
        analysis = indexed.get(new_path or "")
        matching = (
            tuple(
                sorted(
                    {
                        sym.qualified_name or sym.name
                        for sym in analysis.symbols
                        if any(sym.start_line <= line <= sym.end_line for line in changed)
                    }
                )
            )
            if analysis is not None and analysis.symbol_analysis == "AVAILABLE"
            else ()
        )
        results.append(
            DiffImpact(
                base,
                head.commit_sha,
                old_path,
                new_path,
                change,
                tuple(sorted(changed)),
                matching,
                "EXACT_LINE_OVERLAP" if matching else "FILE_ONLY",
            )
        )
    return tuple(results)
