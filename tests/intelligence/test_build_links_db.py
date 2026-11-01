"""LRN-07 build links on PostgreSQL, using the exact SQL from the .NET code."""

import os
import re
import uuid
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest

psycopg = pytest.importorskip("psycopg")

REPO = Path(__file__).resolve().parents[2]
pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
T0 = datetime(2026, 1, 1, tzinfo=UTC)


def _const(relative: str, name: str) -> str:
    text = (REPO / relative).read_text()
    sql = re.search(rf'const string {name} = """(.*?)""";', text, re.S).group(1)
    return re.sub(r"@([a-z_]+)\b", r"%(\1)s", sql)


UPSERT = _const("src/VisionIQ.Api/Features/BuildLinks/PostgresBuildLinkStore.cs", "UpsertSql")
FIND = _const("src/VisionIQ.Api/Features/Graph/GraphEvidenceLookup.cs", "FindSql")


def _connect():
    return psycopg.connect(
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ["DB_API_USER"],
        password=os.environ["DB_API_PASSWORD"],
    )


def _link(source, sha, at, pr=None):
    return {
        "source": source,
        "project": "P",
        "build": "91",
        "repo": "repo-1",
        "sha": sha,
        "pr": pr,
        "ref": f"evidence:{'a' * 64}:completed@x#1",
        "at": at,
    }


def test_an_older_build_revision_never_overwrites_a_newer_one() -> None:
    source = "https://dev.azure.com/" + uuid.uuid4().hex[:8]
    with _connect() as connection, connection.cursor() as cur:
        cur.execute(UPSERT, _link(source, "b" * 40, T0 + timedelta(hours=1)))
        cur.execute(UPSERT, _link(source, "a" * 40, T0))  # older replay
        cur.execute(
            "SELECT commit_sha FROM evidence.build_links WHERE source_instance = %s", (source,)
        )
        assert cur.fetchone()[0] == "b" * 40
        connection.rollback()


def test_a_build_must_state_a_commit_or_a_pull_request() -> None:
    source = "https://dev.azure.com/" + uuid.uuid4().hex[:8]
    with _connect() as connection, connection.cursor() as cur:
        with pytest.raises(psycopg.errors.CheckViolation):
            cur.execute(UPSERT, _link(source, None, T0))
        connection.rollback()


def test_commit_lookup_is_scoped_to_the_builds_repository() -> None:
    source = "https://dev.azure.com/" + uuid.uuid4().hex[:8]
    sha = uuid.uuid4().hex + uuid.uuid4().hex[:8]
    with _connect() as connection, connection.cursor() as cur:
        key = uuid.uuid4().hex + uuid.uuid4().hex
        cur.execute(
            """INSERT INTO evidence.source_items (item_key, provider, source_instance, project_id,
               repository_id, kind, external_id, latest_revision_id, latest_source_updated_at,
               first_observed_at, last_observed_at)
               VALUES (%s, 'azure_devops', %s, 'P', 'repo-B', 'commit', %s, NULL, NULL,
                       now(), now())""",
            (key, source, sha),
        )
        cur.execute(
            """INSERT INTO evidence.source_revisions (item_key, provider_revision_id,
               source_updated_at, observed_at, content_sha256, source_url,
               retrieved_under_principal, redaction_version, normalizer_version, raw_snapshot_uri)
               VALUES (%s, 'r1', %s, now(), repeat('0', 64), 'https://x', 't', 'v', 'v', 'gs://t/x')""",
            (key, T0),
        )
        cur.execute(
            "UPDATE evidence.source_items SET latest_revision_id = 'r1', "
            "latest_source_updated_at = %s WHERE item_key = %s",
            (T0, key),
        )

        def find(repo: str):
            cur.execute(
                FIND,
                {"source": source, "project": "P", "repo": repo, "kind": "commit", "id": sha},
            )
            row = cur.fetchone()
            return row[0] if row else None

        assert find("repo-B") == key
        assert find("repo-A") is None  # same SHA, other repository: never matched
        connection.rollback()
