"""LRN-07 committed-code relationship and PostgreSQL graph acceptance tests."""

import os
from datetime import UTC, datetime

import pytest

from app.code_intelligence.adapters import SymbolReference
from app.code_intelligence.canonical import (
    PostgresScanEvidenceStore,
    ScanEvidenceIdentity,
    build_scan_evidence,
)
from app.code_intelligence.graph import persist_code_graph, project_scan_edges
from app.code_intelligence.scanner import FileAnalysis, GitFile, RepositoryScan


def _scan(status: str = "COMPLETE") -> RepositoryScan:
    """Build a pinned fixture containing one real structural symbol declaration."""
    return RepositoryScan(
        "/approved/repo",
        "a" * 40,
        status,
        (GitFile("app.py", "b" * 40, "python", False),),
        (
            FileAnalysis(
                "app.py",
                "b" * 40,
                "AVAILABLE" if status == "COMPLETE" else "FAILED",
                symbols=(
                    SymbolReference("run", "function", 3, 5, "Service.run"),
                ),
            ),
        ),
    )


def _identity() -> ScanEvidenceIdentity:
    """Use an isolated approved test project/repository scope."""
    return ScanEvidenceIdentity(
        "https://github.com/approved/graph-test", "project-sandbox", "repo-sandbox"
    )


def _evidence(scan: RepositoryScan) -> object:
    """Construct canonical metadata for a specific committed Git snapshot."""
    return build_scan_evidence(
        scan, _identity(), "https://github.com/approved/graph-test",
        datetime(2026, 10, 30, tzinfo=UTC),
    )


def test_structural_graph_has_commit_file_and_declared_symbol_only() -> None:
    """Generate exactly two source-backed edges with no guessed caller links."""
    scan = _scan()
    edges = project_scan_edges(scan, _evidence(scan))
    assert len(edges) == 2
    assert {edge.kind for edge in edges} == {"commit_file", "file_symbol"}
    assert edges[0].source.kind == "commit"
    assert edges[1].target.kind == "symbol"
    assert "Service.run" in edges[1].target.native_id
    assert edges[0].target.key() == edges[1].source.key()


def test_partial_code_scan_never_projects_new_graph_edges() -> None:
    """Parser crash must not promote guessed symbol or file graph revisions."""
    scan = _scan("PARTIAL")
    complete = _scan()
    with pytest.raises(ValueError, match="complete Git revisions"):
        project_scan_edges(scan, _evidence(complete))


def test_source_scoping_separates_same_filename_across_repositories() -> None:
    """Node keys must include the source's canonical SHA-256 identity."""
    scan = _scan()
    normal = project_scan_edges(scan, _evidence(scan))[0].target.key()
    other = ScanEvidenceIdentity(
        "https://github.com/approved/graph-test", "project-two", "repo-two"
    )
    altered = build_scan_evidence(
        scan, other, "https://github.com/approved/graph-test",
        datetime(2026, 10, 30, tzinfo=UTC),
    )
    assert project_scan_edges(scan, altered)[0].target.key() != normal


@pytest.mark.skipif(
    os.environ.get("VISIONIQ_SCAN_DB_INTEGRATION") != "1",
    reason="Requires disposable CI PostgreSQL and migration head 0006",
)
def test_code_graph_is_durable_idempotent_and_revision_bound() -> None:
    """Persist code graph with a canonical source revision, then replay safely."""
    import psycopg
    from psycopg.conninfo import make_conninfo

    conninfo = make_conninfo(
        "",
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ.get("DB_API_USER", "visioniq_api"),
        password=os.environ["DB_API_PASSWORD"],
        sslmode="disable",
    )
    scan = _scan()
    identity = ScanEvidenceIdentity(
        "https://github.com/approved/graph-test",
        "test-" + os.urandom(8).hex(),
        "repo-" + os.urandom(8).hex(),
    )
    evidence = build_scan_evidence(
        scan, identity, "https://github.com/approved/graph-test",
        datetime(2026, 10, 30, tzinfo=UTC),
    )
    store = PostgresScanEvidenceStore(conninfo)
    assert store.save(evidence, "gs://ci-fixture/" + evidence.item_key) == "CREATED"
    assert persist_code_graph(scan, evidence, conninfo) == 2
    assert persist_code_graph(scan, evidence, conninfo) == 2

    with psycopg.connect(conninfo) as db, db.cursor() as cursor:
        cursor.execute(
            """
            SELECT count(*), bool_and(provenance_revision_id = %s)
            FROM evidence.graph_assertions WHERE provenance_item_key = %s
            """,
            (scan.commit_sha, evidence.item_key),
        )
        assert cursor.fetchone() == (2, True)
        cursor.execute(
            """
            SELECT count(*) FROM evidence.graph_nodes WHERE anchor_item_key = %s
            """,
            (evidence.item_key,),
        )
        assert cursor.fetchone()[0] == 3
