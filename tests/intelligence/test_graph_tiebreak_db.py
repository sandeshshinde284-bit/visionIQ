"""m-1: on equal times, the assertion backed by the current evidence revision wins.

Runs the exact .NET traversal statement against PostgreSQL.
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
T = "2026-10-30T10:00:00Z"


def _sql() -> str:
    source = (
        REPO / "src/VisionIQ.Api/Features/Graph/PostgresRelationshipGraphStore.cs"
    ).read_text()
    pattern = r'const string sql = """\s*(SELECT latest\.to_node_key.*?)\s*""";'
    sql = re.search(pattern, source, re.S).group(1)
    return sql.replace("@node", "%(node)s").replace("@limit", "%(limit)s")


def _key() -> str:
    return uuid.uuid4().hex + uuid.uuid4().hex


@pytest.mark.parametrize("current_sorts_lower", [True, False])
def test_equal_time_tie_prefers_the_current_evidence_revision(current_sorts_lower: bool) -> None:
    item, a, b, relationship = _key(), _key(), _key(), _key()
    old_rev, current_rev = "rev-old", "rev-current"
    # Make the current revision's assertion key sort LOWER in one case and HIGHER in the other,
    # so only the new rule (not the hash) can pick it both times.
    keys = sorted([_key(), _key()])
    current_key, old_key = (keys[0], keys[1]) if current_sorts_lower else (keys[1], keys[0])
    connection = psycopg.connect(
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ["DB_API_USER"],
        password=os.environ["DB_API_PASSWORD"],
    )
    with connection, connection.cursor() as cursor:
        cursor.execute(
            """INSERT INTO evidence.source_items (item_key, provider, source_instance, project_id,
               kind, external_id, first_observed_at, last_observed_at)
               VALUES (%s, 'git', 'x', 'p', 'code_scan', %s, now(), now())""",
            (item, item),
        )
        for revision in (old_rev, current_rev):
            cursor.execute(
                """INSERT INTO evidence.source_revisions (item_key, provider_revision_id,
                   source_updated_at, observed_at, content_sha256, source_url,
                   retrieved_under_principal, redaction_version, normalizer_version,
                   raw_snapshot_uri) VALUES (%s, %s, %s, now(), repeat('0', 64), 'https://x',
                   't', 'v', 'v', 'gs://t/x')""",
                (item, revision, T),
            )
        cursor.execute(
            "UPDATE evidence.source_items SET latest_revision_id = %s, "
            "latest_source_updated_at = %s WHERE item_key = %s",
            (current_rev, T, item),
        )
        for node in (a, b):
            cursor.execute(
                "INSERT INTO evidence.graph_nodes "
                "(node_key, anchor_item_key, node_kind, native_id) VALUES (%s, %s, 'file', %s)",
                (node, item, node[:8]),
            )
        for assertion, revision in ((current_key, current_rev), (old_key, old_rev)):
            cursor.execute(
                """INSERT INTO evidence.graph_assertions (assertion_key, relationship_key,
                   from_node_key, to_node_key, relation_type, classification, assertion_state,
                   provenance_item_key, provenance_revision_id, source_asserted_at, observed_at,
                   content_sha256) VALUES (%s, %s, %s, %s, 'file_renamed', 'explicit', 'active',
                   %s, %s, %s, %s, repeat('0', 64))""",
                (assertion, relationship, a, b, item, revision, T, T),
            )
        rows = cursor.execute(_sql(), {"node": a, "limit": 10}).fetchall()
        connection.rollback()
    assert [row[3] for row in rows] == [current_rev]
