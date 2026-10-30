"""Commit-pinned Git inventory and fail-closed, language-neutral code analysis.

Never executes repository code. git ls-tree reads committed content, not the working
tree. A parser failure returns PARTIAL with a separate last_good result; it does
not replace reliable evidence or invent symbols.
"""

import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Literal

from app.code_intelligence.adapters import (
    CallReference,
    ImportReference,
    ParserRegistry,
    SymbolReference,
)

ScanStatus = Literal["COMPLETE", "PARTIAL"]
AnalysisStatus = Literal["AVAILABLE", "UNAVAILABLE", "FAILED"]

_LANGUAGE_BY_SUFFIX = {
    ".cs": "csharp",
    ".go": "go",
    ".java": "java",
    ".js": "javascript",
    ".jsx": "javascript",
    ".py": "python",
    ".rs": "rust",
    ".sql": "sql",
    ".ts": "typescript",
    ".tsx": "typescript",
    ".vue": "vue",
}
_MANIFEST_NAMES = frozenset(
    {
        "cargo.toml",
        "dockerfile",
        "go.mod",
        "package.json",
        "pom.xml",
        "pyproject.toml",
        "requirements.txt",
    }
)


@dataclass(frozen=True)
class GitFile:
    path: str
    blob_sha: str
    language_hint: str | None
    is_manifest: bool


@dataclass(frozen=True)
class FileAnalysis:
    path: str
    blob_sha: str
    symbol_analysis: AnalysisStatus
    adapter_name: str | None = None
    adapter_version: str | None = None
    symbols: tuple[SymbolReference, ...] = ()
    imports: tuple[ImportReference, ...] = ()
    calls: tuple[CallReference, ...] = ()
    diagnostics: tuple[str, ...] = ()


@dataclass(frozen=True)
class RepositoryScan:
    repo_root: str
    commit_sha: str
    status: ScanStatus
    files: tuple[GitFile, ...]
    analyses: tuple[FileAnalysis, ...]
    errors: tuple[str, ...] = ()
    last_good: "RepositoryScan | None" = None


def _git(root: Path, *args: str, check: bool = True) -> bytes:
    result = subprocess.run(
        ["git", "-C", str(root), *args],
        capture_output=True,
        check=False,
        timeout=60,
    )
    if check and result.returncode:
        message = result.stderr.decode("utf-8", errors="replace").strip()
        raise ValueError(f"Git operation failed ({args[0]}): {message[:300]}")
    return result.stdout


def _inventory(root: Path) -> tuple[str, tuple[GitFile, ...]]:
    commit = _git(root, "rev-parse", "--verify", "HEAD^{commit}").decode("ascii").strip()
    output = _git(root, "ls-tree", "-rz", "--full-tree", commit)
    files: list[GitFile] = []
    for record in output.split(b"\x00"):
        if not record:
            continue
        metadata, path_bytes = record.split(b"\t", maxsplit=1)
        mode, kind, sha = metadata.decode("ascii").split(" ")
        # Never follow symlinks or scan submodules; they are independent sources.
        if kind != "blob" or mode == "120000":
            continue
        path = path_bytes.decode("utf-8", errors="surrogateescape")
        name = path.rsplit("/", maxsplit=1)[-1]
        suffix = Path(name).suffix.lower()
        files.append(
            GitFile(
                path=path,
                blob_sha=sha,
                language_hint=_LANGUAGE_BY_SUFFIX.get(suffix),
                is_manifest=name.lower() in _MANIFEST_NAMES or suffix == ".csproj",
            )
        )
    return commit, tuple(files)


def scan_repository(
    repository: str | Path,
    registry: ParserRegistry | None = None,
    previous: RepositoryScan | None = None,
    max_source_bytes: int = 1_048_576,
) -> RepositoryScan:
    """Scan a committed Git revision without consulting network/ADO/database.

    No registered parser means honest file-level fallback. Each parser exception
    is isolated and marks the scan PARTIAL. The last COMPLETE snapshot is retained
    for callers to serve without replacing it with broken parser output.
    """
    if max_source_bytes < 1:
        raise ValueError("max_source_bytes must be positive")
    root = Path(repository).resolve()
    root = Path(
        _git(root, "rev-parse", "--show-toplevel").decode("utf-8", errors="surrogateescape").strip()
    )
    commit, files = _inventory(root)
    registry = registry or ParserRegistry()
    analyses: list[FileAnalysis] = []
    errors: list[str] = []
    for item in files:
        adapter = registry.for_path(item.path)
        if adapter is None:
            analyses.append(
                FileAnalysis(
                    path=item.path,
                    blob_sha=item.blob_sha,
                    symbol_analysis="UNAVAILABLE",
                    diagnostics=("No approved structural parser; file-level evidence only.",),
                )
            )
            continue
        try:
            size = int(_git(root, "cat-file", "-s", item.blob_sha).decode("ascii").strip())
            if size > max_source_bytes:
                analyses.append(
                    FileAnalysis(
                        path=item.path,
                        blob_sha=item.blob_sha,
                        symbol_analysis="UNAVAILABLE",
                        diagnostics=("Source exceeds configured parser size limit.",),
                    )
                )
                continue
            content = _git(root, "cat-file", "blob", item.blob_sha)
            if b"\x00" in content:
                raise ValueError("Binary source cannot be structurally parsed")
            parsed = adapter.parse(item.path, content.decode("utf-8"))
            analyses.append(
                FileAnalysis(
                    path=item.path,
                    blob_sha=item.blob_sha,
                    symbol_analysis="AVAILABLE",
                    adapter_name=adapter.name,
                    adapter_version=adapter.version,
                    symbols=parsed.symbols,
                    imports=parsed.imports,
                    calls=parsed.calls,
                    diagnostics=parsed.diagnostics,
                )
            )
        except (Exception) as exc:
            # Never include source contents or credentials in scanner diagnostics.
            errors.append(f"Parser failed for {item.path} ({adapter.name}): {type(exc).__name__}")
            analyses.append(
                FileAnalysis(
                    path=item.path,
                    blob_sha=item.blob_sha,
                    symbol_analysis="FAILED",
                    adapter_name=adapter.name,
                    adapter_version=adapter.version,
                    diagnostics=("Parser failed; structural evidence unavailable.",),
                )
            )

    status: ScanStatus = "PARTIAL" if errors else "COMPLETE"
    last_good = None
    if status == "PARTIAL" and previous is not None:
        last_good = previous if previous.status == "COMPLETE" else previous.last_good
        if last_good is not None and last_good.repo_root != str(root):
            last_good = None
    return RepositoryScan(
        repo_root=str(root),
        commit_sha=commit,
        status=status,
        files=files,
        analyses=tuple(analyses),
        errors=tuple(errors),
        last_good=last_good,
    )
