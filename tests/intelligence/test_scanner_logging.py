"""Logging phases 3-4: the code scanner logs every step, and never secrets or source content."""

import json
import os
import subprocess
from pathlib import Path

import pytest

pytest.importorskip("tree_sitter_c_sharp")

from app.code_intelligence import graph, ingest_cli

PASSWORD = "DB-PASSWORD-must-never-be-logged"
BUCKET = "private-bucket-must-never-be-logged"
PLANTED = "PLANTED-SECRET-inside-source-code"
BAD_CS = (
    "public class Bad { string[] F(string field) => string.IsNullOrEmpty(field) ? [] : [field]; }\n"
)
ENV = {
    "GIT_AUTHOR_NAME": "t",
    "GIT_AUTHOR_EMAIL": "t@t",
    "GIT_COMMITTER_NAME": "t",
    "GIT_COMMITTER_EMAIL": "t@t",
}


def _repo(tmp_path: Path, bad_files: int = 1) -> Path:
    root = tmp_path / "repo"
    root.mkdir()
    (root / "app.py").write_text(f'API_KEY = "{PLANTED}"\n\ndef handler():\n    return 1\n')
    (root / "Good.cs").write_text("namespace Shop;\npublic class Cart { public void Add() {} }\n")
    for index in range(bad_files):
        (root / f"Bad{index}.cs").write_text(BAD_CS)
    for args in (["init", "-q"], ["add", "."], ["commit", "-qm", "init"]):
        subprocess.run(["git", "-C", str(root), *args], check=True, env={**os.environ, **ENV})
    return root


@pytest.fixture
def scanner(monkeypatch, tmp_path):
    """Real scanner and CLI; only the database and Cloud Storage writes are faked."""
    for key, value in {
        "DB_HOST": "db.internal",
        "DB_NAME": "visioniq",
        "DB_INTELLIGENCE_USER": "visioniq_intelligence",
        "DB_INTELLIGENCE_PASSWORD": PASSWORD,
        "GCS_EVIDENCE_BUCKET": BUCKET,
        "LOG_LEVEL": "DEBUG",
        "LOG_FORMAT": "json",
    }.items():
        monkeypatch.setenv(key, value)
    calls: dict[str, object] = {}
    monkeypatch.setattr(ingest_cli, "GcsScanArchive", lambda bucket: object())
    monkeypatch.setattr(ingest_cli, "PostgresScanEvidenceStore", lambda conninfo: object())
    monkeypatch.setattr(graph, "latest_scanned_commit", lambda conninfo, key: None)

    def persist(*args, **kwargs):
        calls["graph"] = True
        return 5

    monkeypatch.setattr(graph, "persist_code_graph", persist)
    monkeypatch.setattr(ingest_cli, "ingest_complete_scan", lambda *a, **k: "CREATED")

    def run(bad_files: int = 1) -> int:
        repo = _repo(tmp_path, bad_files)
        return ingest_cli.main(
            [
                "--repo",
                str(repo),
                "--cache",
                str(tmp_path / "cache.json"),
                "--project-id",
                "Payments",
                "--repository-id",
                "repo-1",
                "--source-url",
                "https://dev.azure.com/contoso/Payments/_git/shop",
            ]
        )

    return run, calls


def _events(output: str) -> list[dict]:
    return [json.loads(line) for line in output.splitlines() if line.startswith("{")]


def _assert_no_leaks(output: str) -> None:
    for secret in (PASSWORD, BUCKET, PLANTED, "password="):
        assert secret not in output, f"{secret!r} leaked into the log"


def test_a_scan_logs_every_step_with_one_scan_id_and_no_secrets(scanner, capsys) -> None:
    run, calls = scanner

    assert run() == 0
    output = capsys.readouterr().out
    events = _events(output)
    names = [event["event"] for event in events]

    for expected in (
        "scan.started",
        "scan.grammar_gap",
        "scan.completed",
        "scan.previous_commit",
        "evidence.stored",
        "graph.persisted",
        "scan.finished",
    ):
        assert expected in names
    assert len({event["scan_id"] for event in events}) == 1
    assert all(event["project_id"] == "Payments" for event in events)
    completed = next(e for e in events if e["event"] == "scan.completed")
    assert (completed["status"], completed["grammar_gaps"], completed["files"]) == (
        "COMPLETE",
        1,
        3,
    )
    gap = next(e for e in events if e["event"] == "scan.grammar_gap")
    assert (gap["level"], gap["path"]) == ("warning", "Bad0.cs")
    assert next(e for e in events if e["event"] == "graph.persisted")["edges"] == 5
    assert calls["graph"] is True
    _assert_no_leaks(output)


def test_a_partial_scan_logs_why_and_writes_nothing(scanner, capsys) -> None:
    run, calls = scanner

    assert run(bad_files=3) == 2  # systematic grammar gaps: PARTIAL, last good kept
    output = capsys.readouterr().out
    partial = next(e for e in _events(output) if e["event"] == "scan.partial")

    assert partial["level"] == "warning"
    assert partial["last_good_kept"] is True
    assert any("Grammar could not fully parse" in reason for reason in partial["reasons"])
    assert "graph" not in calls
    _assert_no_leaks(output)


def test_a_failure_logs_the_error_type_only(scanner, monkeypatch, capsys) -> None:
    run, _ = scanner

    def explode(*args, **kwargs):
        raise RuntimeError(f"connection failed for password={PASSWORD}")

    monkeypatch.setattr(ingest_cli, "ingest_complete_scan", explode)
    with pytest.raises(RuntimeError):
        run()
    output = capsys.readouterr().out
    failed = next(e for e in _events(output) if e["event"] == "scan.failed")

    assert (failed["level"], failed["error_type"]) == ("error", "RuntimeError")
    _assert_no_leaks(output)


def test_text_format_gives_readable_lines(scanner, monkeypatch, capsys) -> None:
    run, _ = scanner
    monkeypatch.setenv("LOG_FORMAT", "text")

    assert run() == 0
    output = capsys.readouterr().out

    assert "scan.started" in output
    assert not _events(output)  # not JSON
    _assert_no_leaks(output)
