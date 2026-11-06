"""N-2 tombstones and N-7 re-observed content on PostgreSQL, using the exact .NET SQL."""

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
STORE = (REPO / "src/VisionIQ.Api/Features/Evidence/PostgresEvidenceStore.cs").read_text()
READER = (REPO / "src/VisionIQ.Api/Features/Evidence/EvidenceQueryService.cs").read_text()


def _sql(text: str, pattern: str) -> str:
    sql = re.search(pattern, text, re.S).group(1)
    return re.sub(r"@([a-z_]+)\b", r"%(\1)s", sql)


TOMBSTONE = _sql(STORE, r'const string TombstoneSql = """(.*?)""";')
CLEAR = _sql(STORE, r'const string ClearTombstoneSql = """(.*?)""";')
PROMOTE = _sql(STORE, r'const string PromoteReobservedSql = """(.*?)""";')
READ = _sql(READER, r'const string sql = """(.*?)""";')


def _connect():
    return psycopg.connect(
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ["DB_API_USER"],
        password=os.environ["DB_API_PASSWORD"],
    )


def _item(cur) -> str:
    key = uuid.uuid4().hex + uuid.uuid4().hex
    cur.execute(
        """INSERT INTO evidence.source_items (item_key, provider, source_instance, project_id,
           kind, external_id, first_observed_at, last_observed_at)
           VALUES (%s, 'azure_devops', 'x', 'P', 'pull_request', %s, now(), now())""",
        (key, key[:10]),
    )
    return key


def _revision(cur, key, revision, source_at, observed_at, latest=True) -> None:
    cur.execute(
        """INSERT INTO evidence.source_revisions (item_key, provider_revision_id,
           source_updated_at, observed_at, content_sha256, source_url, retrieved_under_principal,
           redaction_version, normalizer_version, raw_snapshot_uri, acl_verified, acl_principals)
           VALUES (%s, %s, %s, %s, repeat('0', 64), 'https://x', 't', 'v', 'v', 'gs://t/x',
                   true, '["p"]')""",
        (key, revision, source_at, observed_at),
    )
    if latest:
        cur.execute(
            "UPDATE evidence.source_items SET latest_revision_id = %s, "
            "latest_source_updated_at = %s, latest_observed_at = %s WHERE item_key = %s",
            (revision, source_at, observed_at, key),
        )


def _served(cur, key) -> bool:
    cur.execute(READ, {"item": key})
    return cur.fetchone() is not None


def _latest(cur, key) -> str:
    cur.execute("SELECT latest_revision_id FROM evidence.source_items WHERE item_key = %s", (key,))
    return cur.fetchone()[0]


def test_a_tombstoned_item_is_never_served_and_a_restore_clears_it() -> None:
    with _connect() as connection, connection.cursor() as cur:
        key = _item(cur)
        _revision(cur, key, "1", T0, T0)
        assert _served(cur, key)

        cur.execute(
            TOMBSTONE, {"key": key, "at": T0 + timedelta(hours=1), "reason": "deleted_in_source"}
        )
        assert cur.rowcount == 1
        assert not _served(cur, key)

        restored_at = T0 + timedelta(
            hours=2
        )  # restored in ADO: a newer revision clears the tombstone
        cur.execute(CLEAR, {"key": key, "source_updated": restored_at})
        assert cur.rowcount == 1
        connection.rollback()


def test_a_deletion_observed_after_a_newer_revision_is_refused() -> None:
    with _connect() as connection, connection.cursor() as cur:
        key = _item(cur)
        _revision(cur, key, "2", T0 + timedelta(hours=3), T0 + timedelta(hours=3))
        cur.execute(
            TOMBSTONE, {"key": key, "at": T0 + timedelta(hours=1), "reason": "deleted_in_source"}
        )
        assert cur.rowcount == 0
        assert _served(cur, key)
        connection.rollback()


def test_an_older_revision_does_not_clear_a_tombstone() -> None:
    with _connect() as connection, connection.cursor() as cur:
        key = _item(cur)
        _revision(cur, key, "1", T0, T0)
        cur.execute(
            TOMBSTONE, {"key": key, "at": T0 + timedelta(hours=1), "reason": "missing_in_source"}
        )
        cur.execute(CLEAR, {"key": key, "source_updated": T0})  # a late replay of an old revision
        assert cur.rowcount == 0
        assert not _served(cur, key)
        connection.rollback()


def test_content_reverted_to_an_earlier_version_becomes_latest_again() -> None:
    with _connect() as connection, connection.cursor() as cur:
        key = _item(cur)
        _revision(cur, key, "active@t#aaaa", T0, T0 + timedelta(minutes=1))  # A
        _revision(cur, key, "active@t#bbbb", T0, T0 + timedelta(minutes=2))  # B (same source time)
        assert _latest(cur, key) == "active@t#bbbb"

        params = {"key": key, "revision": "active@t#aaaa", "observed": T0 + timedelta(minutes=3)}
        cur.execute(PROMOTE, params)  # A seen again now
        assert _latest(cur, key) == "active@t#aaaa"

        stale = {"key": key, "revision": "active@t#bbbb", "observed": T0 + timedelta(minutes=2)}
        cur.execute(PROMOTE, stale)  # an older observation of B does not win
        assert _latest(cur, key) == "active@t#aaaa"
        connection.rollback()


def test_numeric_revisions_and_older_source_times_are_never_promoted() -> None:
    with _connect() as connection, connection.cursor() as cur:
        key = _item(cur)
        _revision(cur, key, "7", T0, T0)
        _revision(cur, key, "8", T0, T0 + timedelta(minutes=1))
        cur.execute(PROMOTE, {"key": key, "revision": "7", "observed": T0 + timedelta(hours=1)})
        assert _latest(cur, key) == "8"

        other = _item(cur)
        _revision(cur, other, "x#old", T0, T0)
        _revision(cur, other, "x#new", T0 + timedelta(hours=1), T0 + timedelta(hours=1))
        cur.execute(
            PROMOTE, {"key": other, "revision": "x#old", "observed": T0 + timedelta(days=1)}
        )
        assert _latest(cur, other) == "x#new"
        connection.rollback()
