"""M-16: file renames keep history; rename edges stay visible after later scans."""

import os
import re
import subprocess
import uuid
from datetime import UTC, datetime
from pathlib import Path

import pytest

from app.code_intelligence.canonical import ScanEvidenceIdentity, build_scan_evidence
from app.code_intelligence.graph import GraphCodeNode, project_rename_edges
from app.code_intelligence.scanner import scan_repository

REPO = Path(__file__).resolve().parents[2]
ENV = {
    "GIT_AUTHOR_NAME": "t",
    "GIT_AUTHOR_EMAIL": "t@t",
    "GIT_COMMITTER_NAME": "t",
    "GIT_COMMITTER_EMAIL": "t@t",
}


def _git(root: Path, *args: str) -> str:
    return subprocess.run(
        ["git", "-C", str(root), *args],
        check=True,
        capture_output=True,
        text=True,
        env={**os.environ, **ENV},
    ).stdout.strip()


def _repo_with_rename(tmp_path: Path) -> tuple[Path, str, str, str]:
    root = tmp_path / "repo"
    (root / "payments").mkdir(parents=True)
    (root / "payments" / "retry.py").write_text("def retry():\n    return 3\n", encoding="utf-8")
    _git(root, "init", "-q")
    _git(root, "add", ".")
    _git(root, "commit", "-qm", "first")
    first = _git(root, "rev-parse", "HEAD")
    (root / "payments" / "resilience").mkdir()
    _git(root, "mv", "payments/retry.py", "payments/resilience/retry.py")
    _git(root, "commit", "-qm", "move")
    second = _git(root, "rev-parse", "HEAD")
    (root / "README.md").write_text("later change\n", encoding="utf-8")
    _git(root, "add", ".")
    _git(root, "commit", "-qm", "later")
    third = _git(root, "rev-parse", "HEAD")
    return root, first, second, third


def _scan_at(root: Path, commit: str):
    _git(root, "checkout", "-q", commit)
    return scan_repository(root)


IDENTITY = ScanEvidenceIdentity("https://example.com/r.git", "proj-r", "repo-r")


def test_a_moved_file_gets_an_old_to_new_rename_edge(tmp_path: Path) -> None:
    root, first, second, _ = _repo_with_rename(tmp_path)
    scan = _scan_at(root, second)
    evidence = build_scan_evidence(scan, IDENTITY, "https://example.com/r.git", datetime.now(UTC))

    edges = project_rename_edges(scan, evidence, first)

    assert [(e.source.native_id, e.target.native_id, e.kind) for e in edges] == [
        ("payments/retry.py", "payments/resilience/retry.py", "file_renamed")
    ]


def test_an_unknown_or_missing_previous_commit_yields_no_rename_edges(tmp_path: Path) -> None:
    root, _, second, _ = _repo_with_rename(tmp_path)
    scan = _scan_at(root, second)
    evidence = build_scan_evidence(scan, IDENTITY, "https://example.com/r.git", datetime.now(UTC))

    assert project_rename_edges(scan, evidence, None) == ()
    assert project_rename_edges(scan, evidence, "f" * 40) == ()  # not in this clone
    assert project_rename_edges(scan, evidence, second) == ()


@pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
def test_rename_edge_stays_visible_after_a_later_scan(tmp_path: Path) -> None:
    psycopg = pytest.importorskip("psycopg")
    from psycopg.conninfo import make_conninfo

    from app.code_intelligence.canonical import PostgresScanEvidenceStore, ingest_complete_scan
    from app.code_intelligence.graph import latest_scanned_commit, persist_code_graph

    def conninfo(prefix: str) -> str:
        return make_conninfo(
            "",
            host=os.environ.get("DB_HOST", "127.0.0.1"),
            port=int(os.environ.get("DB_PORT", "5432")),
            dbname=os.environ.get("DB_NAME", "visioniq"),
            user=os.environ[f"DB_{prefix}_USER"],
            password=os.environ[f"DB_{prefix}_PASSWORD"],
        )

    class Archive:
        def store(self, evidence) -> str:
            return f"gs://rename-test/{evidence.item_key}/{evidence.revision}"

    root, first, second, third = _repo_with_rename(tmp_path)
    identity = ScanEvidenceIdentity(
        "https://example.com/r.git", "p-" + uuid.uuid4().hex[:8], "r-" + uuid.uuid4().hex[:8]
    )
    intel = conninfo("INTELLIGENCE")
    old_file_key = None
    for commit, when in ((first, "2026-01-01"), (second, "2026-01-02"), (third, "2026-01-03")):
        scan = _scan_at(root, commit)
        evidence = build_scan_evidence(
            scan,
            identity,
            "https://example.com/r.git",
            datetime.fromisoformat(when + "T00:00:00+00:00"),
        )
        previous = latest_scanned_commit(intel, evidence.item_key)
        assert (
            ingest_complete_scan(
                scan,
                identity,
                "https://example.com/r.git",
                evidence.source_updated_at,
                Archive(),
                PostgresScanEvidenceStore(intel),
                evidence=evidence,
            )
            != "PARTIAL"
        )
        persist_code_graph(scan, evidence, intel, previous_commit=previous)
        old_file_key = GraphCodeNode(evidence.item_key, "file", "payments/retry.py").key()

    # Run the exact .NET traversal statement from the old file node, after the third scan.
    source = (
        REPO / "src/VisionIQ.Api/Features/Graph/PostgresRelationshipGraphStore.cs"
    ).read_text()
    sql = re.search(
        r'const string sql = """\s*(SELECT latest\.to_node_key.*?)\s*""";', source, re.S
    ).group(1)
    sql = sql.replace("@node", "%(node)s").replace("@limit", "%(limit)s")
    with psycopg.connect(conninfo("API")) as connection:
        rows = connection.execute(sql, {"node": old_file_key, "limit": 100}).fetchall()
    assert [row[4] for row in rows] == ["file_renamed"]
