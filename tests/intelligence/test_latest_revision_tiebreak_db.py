"""B-8: the newest revision must become "latest" even when timestamps tie.

Runs the exact .NET statement (read from PostgresEvidenceStore.cs) and the
Python code-scan statement against a disposable PostgreSQL database.
"""

import os
import re
import uuid
from pathlib import Path

import pytest

psycopg = pytest.importorskip("psycopg")

REPO = Path(__file__).resolve().parents[2]
pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)


def _dotnet_sql() -> str:
    source = (REPO / "src/VisionIQ.Api/Features/Evidence/PostgresEvidenceStore.cs").read_text()
    sql = re.search(r'const string latestSql = """(.*?)""";', source, re.S).group(1)
    return re.sub(r"@(key|revision|source_updated|observed)\b", r"%(\1)s", sql)


def _python_sql() -> str:
    source = (REPO / "src/VisionIQ.Intelligence/app/code_intelligence/canonical.py").read_text()
    match = re.search(r'"""\s*(UPDATE evidence\.source_items.*?)"""', source, re.S)
    return match.group(1).replace("%(updated)s", "%(source_updated)s")


def _connect():
    return psycopg.connect(
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ["DB_API_USER"],
        password=os.environ["DB_API_PASSWORD"],
    )


def _apply(sql: str, steps: list[tuple]) -> str:
    key = uuid.uuid4().hex + uuid.uuid4().hex
    with _connect() as connection, connection.cursor() as cursor:
        cursor.execute(
            """INSERT INTO evidence.source_items (item_key, provider, source_instance, project_id,
               kind, external_id, first_observed_at, last_observed_at)
               VALUES (%s, 'test', 'test', 'p', 'tiebreak', %s, now(), now())""",
            (key, key),
        )
        for step in steps:
            revision, updated = step[0], step[1]
            observed = step[2] if len(step) > 2 else "2026-10-31T00:00:00Z"
            cursor.execute(
                """INSERT INTO evidence.source_revisions (item_key, provider_revision_id,
                   source_updated_at, observed_at, content_sha256, source_url,
                   retrieved_under_principal, redaction_version, normalizer_version,
                   raw_snapshot_uri) VALUES (%s, %s, %s, %s, repeat('0', 64), 'https://x',
                   't', 'v', 'v', 'gs://test/x')""",
                (key, revision, updated, observed),
            )
            params = {"key": key, "revision": revision, "source_updated": updated}
            if "%(observed)s" in sql:
                params["observed"] = observed
            cursor.execute(sql, params)
        cursor.execute(
            "SELECT latest_revision_id FROM evidence.source_items WHERE item_key = %s", (key,)
        )
        latest = cursor.fetchone()[0]
        connection.rollback()
    return latest


T1, T2 = "2026-10-30T10:00:00Z", "2026-10-30T11:00:00Z"
CASES = [
    ([("5", T1), ("6", T1)], "6"),
    ([("9", T1), ("10", T1)], "10"),
    ([("10", T1), ("9", T1)], "10"),
    ([("7", T2), ("8", T1)], "7"),
    ([("3", T1), ("4", T2)], "4"),
    (
        [("completed@2026-10-30T10:00:00Z#aa", T1), ("completed@2026-10-30T10:00:00Z#bb", T1)],
        "completed@2026-10-30T10:00:00Z#bb",
    ),
]


@pytest.mark.parametrize("statement", ["dotnet", "python"])
@pytest.mark.parametrize(("steps", "expected"), CASES)
def test_latest_revision_follows_source_order(statement, steps, expected) -> None:
    sql = _dotnet_sql() if statement == "dotnet" else _python_sql()
    assert _apply(sql, steps) == expected


O1, O2 = "2026-10-31T08:00:00Z", "2026-10-31T09:00:00Z"


def test_dotnet_content_revisions_with_tied_source_time_follow_observation_order() -> None:
    # B-9: "native#hash" content revisions share a source timestamp; the one observed
    # later must win even when its hash sorts lower.
    steps = [("active@2026-10-01T00:00:00Z#ff", T1, O1), ("active@2026-10-01T00:00:00Z#00", T1, O2)]
    assert _apply(_dotnet_sql(), steps) == "active@2026-10-01T00:00:00Z#00"
    reverse = [
        ("active@2026-10-01T00:00:00Z#00", T1, O2),
        ("active@2026-10-01T00:00:00Z#ff", T1, O1),
    ]
    assert _apply(_dotnet_sql(), reverse) == "active@2026-10-01T00:00:00Z#00"
