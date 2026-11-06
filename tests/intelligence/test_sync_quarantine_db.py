"""G-1 (LRN-05): the sync quarantine list on PostgreSQL, using the exact .NET SQL."""

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
SOURCE = (REPO / "src/VisionIQ.Ingestor.Cli/PostgresSyncQuarantine.cs").read_text()
T0 = datetime(2026, 1, 1, tzinfo=UTC)


def _sql(name: str) -> str:
    sql = re.search(rf'const string {name} = """(.*?)""";', SOURCE, re.S).group(1)
    return re.sub(r"@([a-z_]+)\b", r"%(\1)s", sql)


ADD, RESOLVE, OPEN = _sql("AddSql"), _sql("ResolveSql"), _sql("OpenSql")


def _connect():
    return psycopg.connect(
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ["DB_API_USER"],
        password=os.environ["DB_API_PASSWORD"],
    )


def test_attempts_resolution_and_reopening() -> None:
    key = {"source": "s-" + uuid.uuid4().hex[:8], "scope": "P/work-items", "kind": "work_item"}
    item = {**key, "native": "2", "code": "not_readable", "reason": "missing"}

    def row(cur):
        cur.execute(
            "SELECT attempts, first_failed_at, resolved_at IS NOT NULL "
            "FROM evidence.sync_quarantine WHERE source_id = %(source)s",
            key,
        )
        return cur.fetchone()

    def open_ids(cur):
        cur.execute(OPEN, key)
        return [r[0] for r in cur.fetchall()]

    with _connect() as connection, connection.cursor() as cur:
        cur.execute(ADD, {**item, "at": T0})
        cur.execute(ADD, {**item, "at": T0 + timedelta(hours=1)})  # still failing
        assert row(cur) == (2, T0, False)
        assert open_ids(cur) == ["2"]

        cur.execute(RESOLVE, {**key, "native": "2", "at": T0 + timedelta(hours=2)})
        assert row(cur)[2] is True
        assert open_ids(cur) == []

        reopened = T0 + timedelta(days=1)
        cur.execute(ADD, {**item, "at": reopened})  # fails again later: a fresh entry
        assert row(cur) == (1, reopened, False)
        connection.rollback()


def test_invalid_codes_are_rejected_by_the_database() -> None:
    bad = {
        "source": "s",
        "scope": "P",
        "kind": "Work Item",
        "native": "2",
        "code": "x",
        "reason": "r",
        "at": T0,
    }
    with _connect() as connection, connection.cursor() as cur:
        with pytest.raises(psycopg.errors.CheckViolation):
            cur.execute(ADD, bad)
        connection.rollback()
