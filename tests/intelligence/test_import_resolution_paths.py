"""B-12 / B-14 regressions for Python import and manifest resolution."""

import subprocess

from app.code_intelligence.adapters import ImportReference
from app.code_intelligence.relationships import (
    ApprovedRepository,
    _python_targets,
    resolve_dependencies,
)
from app.code_intelligence.scanner import FileAnalysis, GitFile, RepositoryScan

COMMIT = "a" * 40


def _scan(files: dict[str, tuple[str, ...]]) -> RepositoryScan:
    """files maps path -> imports declared in that file."""
    return RepositoryScan(
        repo_root="/repo",
        commit_sha=COMMIT,
        status="COMPLETE",
        files=tuple(
            GitFile(path=p, blob_sha="b" * 40, language_hint="python", is_manifest=False)
            for p in files
        ),
        analyses=tuple(
            FileAnalysis(
                path=p,
                blob_sha="b" * 40,
                symbol_analysis="AVAILABLE",
                imports=tuple(ImportReference(target=t, line=1) for t in imports),
            )
            for p, imports in files.items()
        ),
        errors=(),
    )


def _resolved(files: dict[str, tuple[str, ...]]) -> dict[str, str | None]:
    links = resolve_dependencies(ApprovedRepository("r", "https://example.com/r.git", _scan(files)))
    return {link.declared_reference: link.target_path for link in links}


def test_relative_import_inside_a_folder_whose_name_contains_dots() -> None:
    assert _python_targets("src/My.Service/app/pkg/__init__.py", ".mod")[0] == (
        "src/My.Service/app/pkg/mod.py"
    )


def test_relative_and_src_layout_imports_resolve_to_the_real_file() -> None:
    resolved = _resolved(
        {
            "src/My.Service/app/__init__.py": (),
            "src/My.Service/app/config.py": (),
            "src/My.Service/app/api/health.py": ("app.config", "..config"),
            "src/My.Service/app/pkg/__init__.py": (".mod",),
            "src/My.Service/app/pkg/mod.py": (),
        }
    )
    assert resolved["app.config"] == "src/My.Service/app/config.py"
    assert resolved["..config"] == "src/My.Service/app/config.py"
    assert resolved[".mod"] == "src/My.Service/app/pkg/mod.py"


def test_ambiguous_absolute_import_stays_unresolved() -> None:
    resolved = _resolved(
        {
            "a/util.py": (),
            "a/b/util.py": (),
            "a/b/main.py": ("util",),
        }
    )
    assert resolved["util"] is None


def test_third_party_import_is_not_resolved() -> None:
    assert _resolved({"app/main.py": ("requests",)})["requests"] is None


def test_unreadable_manifest_from_failed_git_show_is_skipped_not_fatal() -> None:
    scan = RepositoryScan(
        repo_root="/repo",
        commit_sha=COMMIT,
        status="COMPLETE",
        files=(
            GitFile(path="pyproject.toml", blob_sha="c" * 40, language_hint=None, is_manifest=True),
        ),
        analyses=(),
        errors=(),
    )

    def read_blob(path: str) -> str:
        raise subprocess.CalledProcessError(128, ["git", "show", f"{COMMIT}:{path}"])

    links = resolve_dependencies(
        ApprovedRepository("r", "https://example.com/r.git", scan), read_blob=read_blob
    )
    assert links == ()
