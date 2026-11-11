"""LRN-06 acceptance CLI: actual committed files, dependencies and diff impact."""

import json
import subprocess
from pathlib import Path

from app.code_intelligence.verify_cli import main, report


def git(repo: Path, *args: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(repo), *args],
        capture_output=True, text=True, check=True,
    )
    return result.stdout.strip()


def make_repo(tmp_path: Path) -> Path:
    root = tmp_path / "demo"
    root.mkdir()
    git(root, "init", "-q")
    git(root, "config", "user.name", "Review")
    git(root, "config", "user.email", "review@example.test")
    (root / "module.py").write_text("def charge():\n    return 1\n", encoding="utf-8")
    (root / "consumer.py").write_text(
        "from module import charge\ndef run():\n    return charge()\n",
        encoding="utf-8",
    )
    (root / "misc.unknown").write_text("hello", encoding="utf-8")
    git(root, "add", ".")
    git(root, "commit", "-qm", "base")
    return root


def test_cli_report_proves_pinned_file_symbol_and_dependency_evidence(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    base = git(root, "rev-parse", "HEAD")
    (root / "module.py").write_text("def charge():\n    return 2\n", encoding="utf-8")
    git(root, "add", ".")
    git(root, "commit", "-qm", "change")
    head = git(root, "rev-parse", "HEAD")

    output = tmp_path / "evidence.json"
    rc = main([
        "--repo", str(root), "--cache", str(tmp_path / "cache"),
        "--repository-id", "approved-repo", "--base", base, "--head", head,
        "--expect-symbol", "charge", "--output", str(output),
    ])
    assert rc == 0
    data = json.loads(output.read_text(encoding="utf-8"))
    assert data["commit"] == head and data["base_commit"] == base
    assert data["status"] == "COMPLETE"
    assert data["counts"]["resolved_dependencies"] >= 1
    assert data["counts"]["impacted_symbols"] == 1
    assert data["file_only_paths"] == ["misc.unknown"]
    assert any("charge" in d["symbols"] for d in data["diff_impacts"])
    assert data["missing_expected_symbols"] == []
    assert not (tmp_path / "cache").exists()  # historical scan never updates HEAD last-good


def test_missing_expected_symbol_is_visible_and_a_failed_check(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    output = tmp_path / "missing.json"
    assert main([
        "--repo", str(root), "--cache", str(tmp_path / "cache"),
        "--repository-id", "approved", "--expect-symbol", "InventedFunction",
        "--output", str(output),
    ]) == 3
    assert json.loads(output.read_text(encoding="utf-8"))[
        "missing_expected_symbols"
    ] == ["InventedFunction"]


def test_cli_rejects_missing_git_reference_without_writing_a_report(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    output = tmp_path / "failure.json"
    assert main([
        "--repo", str(root), "--cache", str(tmp_path / "cache"),
        "--repository-id", "approved", "--head", "does-not-exist",
        "--output", str(output),
    ]) == 1
    assert not output.exists()


def test_cli_default_head_keeps_last_good_metadata(tmp_path: Path) -> None:
    root = make_repo(tmp_path)
    cache = tmp_path / "cache"
    output = tmp_path / "good.json"
    assert main([
        "--repo", str(root), "--cache", str(cache),
        "--repository-id", "approved", "--output", str(output),
    ]) == 0
    assert list(cache.glob("git-scan-*.json"))
    assert json.loads(output.read_text(encoding="utf-8"))["status"] == "COMPLETE"
