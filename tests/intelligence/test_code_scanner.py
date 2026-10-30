"""Contract tests for the isolated LRN-06 Git scanner; no network, secrets or ADO calls."""

import subprocess
from pathlib import Path

import pytest

from app.code_intelligence import (
    ParserRegistry,
    ParserResult,
    SymbolReference,
    scan_repository,
)


def git(root: Path, *args: str) -> None:
    subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True)


def make_repo(tmp_path: Path) -> Path:
    repo = tmp_path / "demo"
    repo.mkdir()
    git(repo, "init")
    git(repo, "config", "user.name", "CI Test")
    git(repo, "config", "user.email", "ci@example.test")
    (repo / "src").mkdir()
    (repo / "src" / "hello.py").write_text("def hello():\n    return 1\n", encoding="utf-8")
    (repo / "src" / "unknown.foo").write_text("not parsed", encoding="utf-8")
    (repo / "requirements.txt").write_text("pytest\n", encoding="utf-8")
    git(repo, "add", ".")
    git(repo, "commit", "-m", "initial")
    return repo


class FakeParser:
    name = "fake-python"
    version = "1.0"
    extensions = frozenset({".py"})
    capabilities = frozenset({"symbols"})
    crash = False

    def parse(self, path: str, content: str) -> ParserResult:
        if self.crash:
            raise RuntimeError("example sensitive file content")
        assert path == "src/hello.py"
        assert "def hello()" in content
        return ParserResult(
            symbols=(SymbolReference(name="hello", kind="function", start_line=1, end_line=2),)
        )


def test_inventory_is_commit_pinned_and_manifest_aware(tmp_path: Path) -> None:
    repo = make_repo(tmp_path)
    original = scan_repository(repo)
    assert original.status == "COMPLETE"
    assert len(original.commit_sha) == 40
    assert [item.path for item in original.files] == [
        "requirements.txt",
        "src/hello.py",
        "src/unknown.foo",
    ]
    assert original.files[0].is_manifest
    assert original.files[1].language_hint == "python"

    # Working-tree edits must not silently change SHA-pinned historical evidence.
    (repo / "src" / "hello.py").write_text("MUTATED CONTENT", encoding="utf-8")
    assert scan_repository(repo) == original


def test_unrecognized_files_never_have_fabricated_symbols(tmp_path: Path) -> None:
    scan = scan_repository(make_repo(tmp_path))
    assert all(analysis.symbol_analysis == "UNAVAILABLE" for analysis in scan.analyses)
    assert all(not analysis.symbols for analysis in scan.analyses)
    assert all(not analysis.imports for analysis in scan.analyses)
    assert all(not analysis.calls for analysis in scan.analyses)


def test_registered_adapter_reports_real_symbols_and_version(tmp_path: Path) -> None:
    registry = ParserRegistry()
    parser = FakeParser()
    registry.register(parser)
    scan = scan_repository(make_repo(tmp_path), registry)
    assert scan.status == "COMPLETE"
    py = next(item for item in scan.analyses if item.path.endswith(".py"))
    assert py.symbol_analysis == "AVAILABLE"
    assert py.adapter_name == "fake-python"
    assert py.adapter_version == "1.0"
    assert py.symbols[0].name == "hello"
    unknown = next(item for item in scan.analyses if item.path.endswith(".foo"))
    assert unknown.symbol_analysis == "UNAVAILABLE"
    assert unknown.symbols == ()


def test_parser_crash_returns_partial_and_preserves_last_good(tmp_path: Path) -> None:
    repo = make_repo(tmp_path)
    parser = FakeParser()
    registry = ParserRegistry()
    registry.register(parser)
    good = scan_repository(repo, registry)
    parser.crash = True
    partial = scan_repository(repo, registry, previous=good)
    assert partial.status == "PARTIAL"
    assert partial.last_good == good
    failed = next(item for item in partial.analyses if item.path.endswith(".py"))
    assert failed.symbol_analysis == "FAILED"
    assert failed.symbols == ()
    assert any("src/hello.py" in message for message in partial.errors)
    assert not any("sensitive" in message for message in partial.errors)

    again = scan_repository(repo, registry, previous=partial)
    assert again.last_good == good


def test_parser_failure_does_not_leak_last_good_across_repos(tmp_path: Path) -> None:
    first = make_repo(tmp_path)
    good = scan_repository(first)
    other = tmp_path / "other"
    other.mkdir()
    git(other, "init")
    git(other, "config", "user.name", "CI Test")
    git(other, "config", "user.email", "ci@example.test")
    (other / "crash.py").write_text("broken", encoding="utf-8")
    git(other, "add", ".")
    git(other, "commit", "-m", "initial")
    parser = FakeParser()
    parser.crash = True
    registry = ParserRegistry()
    registry.register(parser)
    partial = scan_repository(other, registry, previous=good)
    assert partial.status == "PARTIAL"
    assert partial.last_good is None


def test_registry_rejects_duplicate_extensions() -> None:
    registry = ParserRegistry()
    registry.register(FakeParser())
    with pytest.raises(ValueError, match="already registered"):
        registry.register(FakeParser())


def test_source_size_guard_preserves_file_inventory(tmp_path: Path) -> None:
    registry = ParserRegistry()
    registry.register(FakeParser())
    scan = scan_repository(make_repo(tmp_path), registry, max_source_bytes=1)
    py = next(item for item in scan.analyses if item.path.endswith(".py"))
    assert py.symbol_analysis == "UNAVAILABLE"
    assert py.symbols == ()
    assert scan.status == "COMPLETE"


def test_invalid_source_limit_rejected(tmp_path: Path) -> None:
    with pytest.raises(ValueError, match="positive"):
        scan_repository(make_repo(tmp_path), max_source_bytes=0)
