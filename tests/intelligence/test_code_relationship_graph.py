"""LRN-07 committed-code relationship and PostgreSQL graph acceptance tests."""

import hashlib
import os
from datetime import UTC, datetime

import pytest

from app.code_intelligence.adapters import SymbolReference
from app.code_intelligence.canonical import (
    PostgresScanEvidenceStore,
    ScanEvidenceIdentity,
    build_scan_evidence,
)
from app.code_intelligence.graph import (
    GraphCodeNode,
    persist_code_graph,
    project_declared_dependencies,
    project_scan_edges,
)
from app.code_intelligence.relationships import DependencyLink
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
                symbols=(SymbolReference("run", "function", 3, 5, "Service.run"),),
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
        scan,
        _identity(),
        "https://github.com/approved/graph-test",
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
        scan,
        other,
        "https://github.com/approved/graph-test",
        datetime(2026, 10, 30, tzinfo=UTC),
    )
    assert project_scan_edges(scan, altered)[0].target.key() != normal


def test_declared_cross_project_dependency_requires_approved_target() -> None:
    """Explicit Git dependencies link only to an approved immutable target scan."""
    source = _scan()
    origin = _evidence(source)
    target_identity = ScanEvidenceIdentity(
        "https://github.com/approved/another-project",
        "another-project",
        "other-repository",
    )
    target = build_scan_evidence(
        source,
        target_identity,
        "https://github.com/approved/another-project",
        datetime(2026, 10, 30, tzinfo=UTC),
    )
    link = DependencyLink(
        source_repo=origin.identity.repository_id,
        source_commit=source.commit_sha,
        source_path="app.py",
        target_repo="other-repository",
        target_commit=target.revision,
        target_path=None,
        declared_reference="https://github.com/approved/another-project",
        kind="DECLARED_GIT_DEPENDENCY",
        status="DECLARED_TARGET_REPOSITORY",
    )
    assert project_declared_dependencies(source, origin, (link,), {}) == ()
    edges = project_declared_dependencies(source, origin, (link,), {"other-repository": target})
    assert len(edges) == 1
    assert edges[0].kind == "repository_dependency"
    assert edges[0].target.anchor == target.item_key

    untrusted = link.__class__(
        source_repo="unapproved-project",
        source_commit=link.source_commit,
        source_path=link.source_path,
        target_repo=link.target_repo,
        target_commit=link.target_commit,
        target_path=None,
        declared_reference=link.declared_reference,
        kind=link.kind,
        status=link.status,
    )
    with pytest.raises(ValueError, match="unapproved source identity"):
        project_declared_dependencies(source, origin, (untrusted,), {"other-repository": target})


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
        scan,
        identity,
        "https://github.com/approved/graph-test",
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


@pytest.mark.skipif(
    os.environ.get("VISIONIQ_SCAN_DB_INTEGRATION") != "1",
    reason="Requires disposable CI PostgreSQL and migration head 0020",
)
def test_fix_change_edges_reach_the_same_file_node_a_scan_would_use() -> None:
    """Review R7-01/R7-02: a historical fix's changed file reaches the same file node identity
    project_scan_edges uses for its commit_file edge, without needing that exact commit scanned."""
    import psycopg
    from psycopg.conninfo import make_conninfo

    from app.code_intelligence.graph import project_fix_change_edges

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
        scan, identity, "https://github.com/approved/graph-test", datetime(2026, 10, 30, tzinfo=UTC)
    )
    PostgresScanEvidenceStore(conninfo).save(evidence, "gs://ci-fixture/" + evidence.item_key)
    expected_file_node = GraphCodeNode(evidence.item_key, "file", "app.py").key()

    ado_instance = "https://dev.azure.com/t" + os.urandom(6).hex()
    historical_sha = "c" * 40  # a commit that was never the HEAD of any scan
    commit_item_key = hashlib.sha256(
        b"".join(
            len(v).to_bytes(4, "little") + v.encode()
            for v in (
                "azure_devops",
                ado_instance,
                identity.project_id,
                identity.repository_id,
                "commit",
                historical_sha,
            )
        )
    ).hexdigest()
    with psycopg.connect(conninfo) as db, db.cursor() as cursor:
        cursor.execute(
            """
            INSERT INTO evidence.source_items (item_key, provider, source_instance, project_id,
                repository_id, kind, external_id, latest_revision_id, latest_source_updated_at,
                first_observed_at, last_observed_at)
            VALUES (%s, 'azure_devops', %s, %s, %s, 'commit', %s, %s, now(), now(), now())
            ON CONFLICT (item_key) DO NOTHING
            """,
            (
                commit_item_key,
                ado_instance,
                identity.project_id,
                identity.repository_id,
                historical_sha,
                historical_sha,
            ),
        )
        cursor.execute(
            """
            INSERT INTO evidence.source_revisions (item_key, provider_revision_id,
                source_updated_at, observed_at, content_sha256, source_url,
                retrieved_under_principal, redacted_payload, redaction_version,
                normalizer_version, raw_snapshot_uri, archive_status, acl_verified, acl_principals)
            VALUES (%s, %s, now(), now(), %s, %s, 't', '{}'::jsonb, 'v', 'v', %s,
                    'ARCHIVED', true, '[]'::jsonb)
            ON CONFLICT DO NOTHING
            """,
            (
                commit_item_key,
                historical_sha,
                "d" * 64,
                f"{ado_instance}/{identity.project_id}/_git/repo/commit/{historical_sha}",
                "mem://" + commit_item_key,
            ),
        )
        cursor.execute(
            """
            INSERT INTO evidence.fix_change_sets (source_instance, project_id, repository_id,
                fix_kind, fix_id, paths, truncated, provenance_item_key, observed_at)
            VALUES (%s, %s, %s, 'commit', %s, %s, false, %s, now())
            ON CONFLICT (source_instance, project_id, repository_id, fix_kind, fix_id)
            DO UPDATE SET paths = EXCLUDED.paths
            """,
            (
                ado_instance,
                identity.project_id,
                identity.repository_id,
                historical_sha,
                ["app.py"],
                commit_item_key,
            ),
        )
        db.commit()

    written = project_fix_change_edges(
        conninfo, evidence.item_key, identity.project_id, identity.repository_id, ado_instance
    )
    assert written == 1
    # Idempotent: re-running does not duplicate the assertion.
    assert (
        project_fix_change_edges(
            conninfo, evidence.item_key, identity.project_id, identity.repository_id, ado_instance
        )
        == 1
    )

    with psycopg.connect(conninfo) as db, db.cursor() as cursor:
        cursor.execute(
            """
            SELECT to_node_key, relation_type FROM evidence.graph_assertions
            WHERE provenance_item_key = %s
            """,
            (commit_item_key,),
        )
        rows = cursor.fetchall()
        assert rows == [(expected_file_node, "commit_changed_file")]
