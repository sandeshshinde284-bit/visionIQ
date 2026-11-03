"""Opt-in PostgreSQL integration test for LRN-06 canonical Git metadata.

Runs only in disposable CI PostgreSQL with migration 0005 and API-role grants.
No cloud buckets or Git repositories are required.
"""

import os
from datetime import UTC, datetime

import pytest

from app.code_intelligence.canonical import (
    PostgresScanEvidenceStore,
    ScanEvidenceIdentity,
    build_scan_evidence,
)
from app.code_intelligence.scanner import FileAnalysis, GitFile, RepositoryScan


@pytest.mark.skipif(
    os.environ.get("VISIONIQ_SCAN_DB_INTEGRATION") != "1",
    reason="Run only against the disposable integration PostgreSQL container",
)
def test_git_scan_replay_and_immutable_revisions_use_existing_evidence_schema() -> None:
    """Verify CREATED/UNCHANGED/NEW_REVISION/CONFLICT and unverified source ACL."""
    import psycopg
    from psycopg.conninfo import make_conninfo

    password = os.environ["DB_API_PASSWORD"]
    conninfo = make_conninfo(
        "",
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ.get("DB_API_USER", "visioniq_api"),
        password=password,
        sslmode="disable",
    )
    store = PostgresScanEvidenceStore(conninfo)
    now = datetime.now(UTC)
    identity = ScanEvidenceIdentity(
        source_instance="https://github.com/approved/test",
        project_id="test-project",
        repository_id="repo-" + os.urandom(12).hex(),
    )

    def evidence(commit: str, blob: str) -> object:
        """Construct stable metadata for a committed repository revision."""
        scan = RepositoryScan(
            repo_root="/test/approved-checkout",
            commit_sha=commit,
            status="COMPLETE",
            files=(GitFile("app.py", blob, "python", False),),
            analyses=(FileAnalysis("app.py", blob, "UNAVAILABLE"),),
        )
        return build_scan_evidence(scan, identity, "https://github.com/approved/test", now)

    first = evidence("a" * 40, "b" * 40)
    second = evidence("c" * 40, "d" * 40)
    uri = "gs://fake-private-ci-archive/immutable"
    assert store.save(first, uri) == "CREATED"
    assert store.save(first, uri) == "UNCHANGED"
    assert store.save(second, uri) == "NEW_REVISION"
    assert store.save(evidence("a" * 40, "e" * 40), uri) == "CONFLICT"

    with psycopg.connect(conninfo) as connection, connection.cursor() as cursor:
        cursor.execute(
            """
            SELECT COUNT(*), bool_and(NOT acl_verified),
                bool_and(acl_principals = '[]'::jsonb)
            FROM evidence.source_revisions WHERE item_key = %s
            """,
            (first.item_key,),
        )
        count, denied, empty = cursor.fetchone()
        assert (count, denied, empty) == (2, True, True)
        cursor.execute(
            "SELECT latest_revision_id FROM evidence.source_items WHERE item_key = %s",
            (first.item_key,),
        )
        assert cursor.fetchone()[0] == second.revision
