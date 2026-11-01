"""LRN-10 schema (migration 0013): change sets, append-only lessons and role access."""

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
STORE = (REPO / "src/VisionIQ.Api/Features/Lessons/PostgresFixChangeSetStore.cs").read_text()
UPSERT = re.sub(
    r"@([a-z_]+)\b",
    r"%(\1)s",
    re.search(r'const string UpsertSql = """(.*?)""";', STORE, re.S).group(1),
)


def _connect(prefix: str):
    return psycopg.connect(
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ[f"DB_{prefix}_USER"],
        password=os.environ[f"DB_{prefix}_PASSWORD"],
    )


def test_a_change_set_keeps_the_newest_observation() -> None:
    fix = uuid.uuid4().hex[:12]
    base = {
        "instance": "https://dev.azure.com/contoso",
        "project": "P",
        "repository": "repo-1",
        "kind": "pull_request",
        "fix": fix,
        "truncated": False,
        "provenance": "a" * 64,
    }
    with _connect("API") as connection, connection.cursor() as cur:
        cur.execute(UPSERT, {**base, "paths": ["/a.cs"], "observed": T0 + timedelta(hours=2)})
        cur.execute(UPSERT, {**base, "paths": ["/old.cs"], "observed": T0})  # a late, older event
        cur.execute("SELECT paths FROM evidence.fix_change_sets WHERE fix_id = %s", (fix,))
        assert cur.fetchone()[0] == ["/a.cs"]
        connection.rollback()


def _lesson(cur, status: str = "candidate", cause: str = "unverified", verifier=None) -> str:
    cur.execute(
        "INSERT INTO knowledge.lesson_patterns (source_instance, project_id, repository_id, "
        "latest_revision, created_at) VALUES ('https://x', 'P', 'r', 1, now()) RETURNING pattern_id"
    )
    pattern = cur.fetchone()[0]
    cur.execute(
        "INSERT INTO knowledge.lesson_revisions (pattern_id, revision, status, cause_status, "
        "title, signature_paths, observation_keys, decision, rationale, generated_by, verified_by, "
        "created_at) VALUES (%s, 1, %s, %s, 't', '{/a.cs}', %s, 'CREATE_NEW', 'r', "
        "'process:visioniq-lessons', %s, now())",
        (pattern, status, cause, ["k1", "k2"], verifier),
    )
    return pattern


def test_lesson_revisions_are_append_only() -> None:
    with _connect("INTELLIGENCE") as connection, connection.cursor() as cur:
        pattern = _lesson(cur)
        with pytest.raises(psycopg.errors.RaiseException, match="append-only"):
            cur.execute(
                "UPDATE knowledge.lesson_revisions SET status = 'stale' WHERE pattern_id = %s",
                (pattern,),
            )
        connection.rollback()
    with _connect("INTELLIGENCE") as connection, connection.cursor() as cur:
        pattern = _lesson(cur)
        with pytest.raises(psycopg.errors.RaiseException, match="append-only"):
            cur.execute("DELETE FROM knowledge.lesson_revisions WHERE pattern_id = %s", (pattern,))
        connection.rollback()


@pytest.mark.parametrize("status,cause", [("current", "unverified"), ("candidate", "verified")])
def test_current_or_verified_needs_a_named_human(status: str, cause: str) -> None:
    with _connect("INTELLIGENCE") as connection, connection.cursor() as cur:
        with pytest.raises(psycopg.errors.CheckViolation):
            _lesson(cur, status, cause)
        connection.rollback()
    with _connect("INTELLIGENCE") as connection, connection.cursor() as cur:
        _lesson(cur, status, cause, verifier="human:payments-owner")  # allowed when named
        connection.rollback()


def test_intelligence_writes_lessons_and_the_api_can_only_read_them() -> None:
    with _connect("INTELLIGENCE") as connection, connection.cursor() as cur:
        _lesson(cur)
        cur.execute("SELECT count(*) FROM evidence.fix_change_sets")  # reads what fixes changed
        connection.rollback()
    with _connect("API") as connection, connection.cursor() as cur:
        cur.execute("SELECT count(*) FROM knowledge.lesson_revisions")
        with pytest.raises(psycopg.errors.InsufficientPrivilege):
            _lesson(cur)
        connection.rollback()
