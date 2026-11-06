"""LRN-07: test methods become test-case nodes linked from their file."""

import os
import re
import subprocess
import uuid
from datetime import UTC, datetime
from pathlib import Path

import pytest

from app.code_intelligence.canonical import ScanEvidenceIdentity, build_scan_evidence
from app.code_intelligence.graph import GraphCodeNode, project_scan_edges
from app.code_intelligence.parsers import PythonAstAdapter, default_parser_registry
from app.code_intelligence.scanner import scan_repository

REPO = Path(__file__).resolve().parents[2]
CSHARP = """public class RetryTests {
    [Fact] public void Stops_after_three() {}
    [Theory, InlineData(1)] public void Backoff(int n) {}
    [DatabaseFact("X")] public void Persists() {}
    [NUnit.Framework.Test] public void Nunit_style() {}
    public void Helper() {}
}"""


def test_csharp_test_attributes_mark_test_methods() -> None:
    pytest.importorskip("tree_sitter_c_sharp")
    from app.code_intelligence.parsers import CSharpTreeSitterAdapter

    symbols = CSharpTreeSitterAdapter().parse("RetryTests.cs", CSHARP).symbols
    kinds = {s.name: s.kind for s in symbols}
    assert kinds == {
        "RetryTests": "class",
        "Stops_after_three": "test",
        "Backoff": "test",
        "Persists": "test",
        "Nunit_style": "test",
        "Helper": "method",
    }


def test_python_test_functions_in_test_files_are_tests() -> None:
    source = "def test_retry():\n    pass\n\ndef helper():\n    pass\n"
    in_test_file = {
        s.name: s.kind for s in PythonAstAdapter().parse("tests/test_retry.py", source).symbols
    }
    in_app_file = {s.name: s.kind for s in PythonAstAdapter().parse("app/retry.py", source).symbols}
    assert in_test_file == {"test_retry": "test", "helper": "function"}
    assert in_app_file == {"test_retry": "function", "helper": "function"}


def _repo(tmp_path: Path) -> Path:
    root = tmp_path / "repo"
    (root / "tests").mkdir(parents=True)
    (root / "tests" / "test_retry.py").write_text(
        "def test_stops():\n    assert True\n", encoding="utf-8"
    )
    env = {
        **os.environ,
        "GIT_AUTHOR_NAME": "t",
        "GIT_AUTHOR_EMAIL": "t@t",
        "GIT_COMMITTER_NAME": "t",
        "GIT_COMMITTER_EMAIL": "t@t",
    }
    for args in (["init", "-q"], ["add", "."], ["commit", "-qm", "init"]):
        subprocess.run(["git", "-C", str(root), *args], check=True, env=env)
    return root


def test_test_symbols_become_file_test_edges(tmp_path: Path) -> None:
    scan = scan_repository(_repo(tmp_path), default_parser_registry())
    evidence = build_scan_evidence(
        scan,
        ScanEvidenceIdentity("https://example.com/r.git", "p", "r"),
        "https://example.com/r.git",
        datetime.now(UTC),
    )
    edges = [e for e in project_scan_edges(scan, evidence) if e.source.kind == "file"]
    assert [(e.target.kind, e.kind) for e in edges] == [("test_case", "file_test")]


@pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
def test_a_file_to_test_edge_is_traversable(tmp_path: Path) -> None:
    psycopg = pytest.importorskip("psycopg")
    from psycopg.conninfo import make_conninfo

    from app.code_intelligence.canonical import PostgresScanEvidenceStore, ingest_complete_scan
    from app.code_intelligence.graph import persist_code_graph

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
            return f"gs://test-nodes/{evidence.item_key}"

    scan = scan_repository(_repo(tmp_path), default_parser_registry())
    url = "https://example.com/r.git"
    identity = ScanEvidenceIdentity(url, "p-" + uuid.uuid4().hex[:8], "r-" + uuid.uuid4().hex[:8])
    committed = datetime.now(UTC)
    evidence = build_scan_evidence(scan, identity, url, committed)
    intel = conninfo("INTELLIGENCE")
    ingest_complete_scan(
        scan,
        identity,
        url,
        committed,
        Archive(),
        PostgresScanEvidenceStore(intel),
        evidence=evidence,
    )
    persist_code_graph(scan, evidence, intel)

    store_cs = REPO / "src/VisionIQ.Api/Features/Graph/PostgresRelationshipGraphStore.cs"
    pattern = r'const string sql = """\s*(SELECT latest\.to_node_key.*?)\s*""";'
    sql = re.search(pattern, store_cs.read_text(), re.S).group(1)
    sql = sql.replace("@node", "%(node)s").replace("@limit", "%(limit)s")
    file_key = GraphCodeNode(evidence.item_key, "file", "tests/test_retry.py").key()
    with psycopg.connect(conninfo("API")) as connection:
        rows = connection.execute(sql, {"node": file_key, "limit": 100}).fetchall()
    assert [row[4] for row in rows] == ["file_test"]
