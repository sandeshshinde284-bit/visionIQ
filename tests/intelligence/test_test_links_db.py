"""LRN-07 test links on PostgreSQL, using the exact SQL from the .NET code.

- the store keeps each work item's CURRENT "Tested By" links; older replays never win;
- tested_by is visible only while the defect's latest revision states it;
- automated_by is visible only while its test method exists in the latest code scan;
- the automated-test mapping is exact and reports ambiguity.
"""

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


def _raw_strings(relative: str) -> list[str]:
    text = (REPO / relative).read_text()
    blocks = re.findall(r'"""(.*?)"""', text, re.S)
    return [re.sub(r"@([a-z_]+)\b", r"%(\1)s", block) for block in blocks]


STORE = _raw_strings("src/VisionIQ.Api/Features/TestLinks/PostgresTestLinkStore.cs")
GUARD = next(s for s in STORE if "INSERT INTO evidence.test_link_sources" in s)
DELETE_LINKS = next(s for s in STORE if "DELETE FROM evidence.tested_by_links" in s)
UPSERT_LINK = next(s for s in STORE if "INSERT INTO evidence.tested_by_links" in s)
DELETE_AUTOMATION = next(s for s in STORE if "DELETE FROM evidence.test_case_automation" in s)
UPSERT_AUTOMATION = next(s for s in STORE if "INSERT INTO evidence.test_case_automation" in s)
OUTGOING = next(
    s
    for s in _raw_strings("src/VisionIQ.Api/Features/Graph/PostgresRelationshipGraphStore.cs")
    if "SELECT latest.to_node_key" in s
)
MAPPING = next(
    s
    for s in _raw_strings("src/VisionIQ.Api/Features/Graph/TestLinkGraphMaterializer.cs")
    if "substring(n.native_id" in s
)


def _connect():
    return psycopg.connect(
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ["DB_API_USER"],
        password=os.environ["DB_API_PASSWORD"],
    )


def _key() -> str:
    return uuid.uuid4().hex + uuid.uuid4().hex


def _ref(key: str = "a" * 64, revision: str = "1#x") -> str:
    return f"evidence:{key}:{revision}"


def _replace(cur, source, project, item, test_cases, at, name=None, ref=None) -> None:
    """The same statements, in the same order, as PostgresTestLinkStore.ReplaceAsync."""
    key = {"source": source, "project": project, "item": item, "at": at}
    cur.execute(GUARD, key)
    if cur.fetchone() is None:
        return  # older than what was already applied: ignored entirely
    cur.execute(DELETE_LINKS, {**key, "ids": list(test_cases)})
    for test_case in sorted(set(test_cases)):
        cur.execute(UPSERT_LINK, {**key, "test": test_case, "ref": ref or _ref()})
    if name is None:
        cur.execute(DELETE_AUTOMATION, key)
    else:
        cur.execute(UPSERT_AUTOMATION, {**key, "name": name, "storage": None, "ref": ref or _ref()})


def _links(cur, source) -> list[tuple[str, str]]:
    cur.execute(
        "SELECT work_item_id, test_case_id FROM evidence.tested_by_links "
        "WHERE source_instance = %s ORDER BY 1, 2",
        (source,),
    )
    return cur.fetchall()


def test_the_store_keeps_current_links_and_older_replays_never_win() -> None:
    source = "https://dev.azure.com/" + uuid.uuid4().hex[:8]
    with _connect() as connection, connection.cursor() as cur:
        _replace(cur, source, "P", "1", ["10", "11"], T0)
        assert _links(cur, source) == [("1", "10"), ("1", "11")]

        _replace(cur, source, "P", "1", ["10"], T0 + timedelta(hours=1))  # link 11 removed in ADO
        assert _links(cur, source) == [("1", "10")]

        _replace(cur, source, "P", "1", ["10", "11", "12"], T0)  # an older revision replayed
        assert _links(cur, source) == [("1", "10")]  # ignored entirely: 11 is not resurrected

        _replace(cur, source, "P", "1", [], T0 + timedelta(hours=2))  # all links removed
        assert _links(cur, source) == []
        connection.rollback()


def test_automation_is_replaced_and_removed_by_newer_revisions_only() -> None:
    source = "https://dev.azure.com/" + uuid.uuid4().hex[:8]
    with _connect() as connection, connection.cursor() as cur:

        def name() -> str | None:
            cur.execute(
                "SELECT automated_test_name FROM evidence.test_case_automation "
                "WHERE source_instance = %s",
                (source,),
            )
            row = cur.fetchone()
            return row[0] if row else None

        _replace(cur, source, "P", "77", [], T0 + timedelta(hours=1), name="Shop.T.New")
        _replace(cur, source, "P", "77", [], T0, name="Shop.T.Old")  # older replay
        assert name() == "Shop.T.New"
        _replace(cur, source, "P", "77", [], T0, name=None)  # older replay cannot delete
        assert name() == "Shop.T.New"
        _replace(cur, source, "P", "77", [], T0 + timedelta(hours=2), name=None)
        assert name() is None
        connection.rollback()


def _item(cur, provider="azure_devops", kind="work_item_current") -> str:
    key = _key()
    cur.execute(
        """INSERT INTO evidence.source_items (item_key, provider, source_instance, project_id,
           kind, external_id, first_observed_at, last_observed_at)
           VALUES (%s, %s, 'x', 'P', %s, %s, now(), now())""",
        (key, provider, kind, key[:12]),
    )
    return key


def _revision(cur, item: str, revision: str, at: datetime, latest: bool = True) -> None:
    cur.execute(
        """INSERT INTO evidence.source_revisions (item_key, provider_revision_id, source_updated_at,
           observed_at, content_sha256, source_url, retrieved_under_principal, redaction_version,
           normalizer_version, raw_snapshot_uri)
           VALUES (%s, %s, %s, now(), repeat('0', 64), 'https://x', 't', 'v', 'v', 'gs://t/x')""",
        (item, revision, at),
    )
    if latest:
        cur.execute(
            "UPDATE evidence.source_items SET latest_revision_id = %s, "
            "latest_source_updated_at = %s WHERE item_key = %s",
            (revision, at, item),
        )


def _node(cur, anchor: str, kind: str, native: str) -> str:
    key = _key()
    cur.execute(
        "INSERT INTO evidence.graph_nodes (node_key, anchor_item_key, node_kind, native_id) "
        "VALUES (%s, %s, %s, %s)",
        (key, anchor, kind, native),
    )
    return key


def _edge(cur, a: str, b: str, relation: str, item: str, revision: str, at: datetime) -> None:
    cur.execute(
        """INSERT INTO evidence.graph_assertions (assertion_key, relationship_key, from_node_key,
           to_node_key, relation_type, classification, assertion_state, provenance_item_key,
           provenance_revision_id, source_asserted_at, content_sha256)
           VALUES (%s, %s, %s, %s, %s, 'explicit', 'active', %s, %s, %s, repeat('0', 64))""",
        (_key(), _key(), a, b, relation, item, revision, at),
    )


def _outgoing(cur, node: str) -> list[str]:
    cur.execute(OUTGOING, {"node": node, "limit": 100})
    return [row[4] for row in cur.fetchall()]


def test_tested_by_is_visible_only_while_the_latest_defect_revision_states_it() -> None:
    with _connect() as connection, connection.cursor() as cur:
        bug, test_case = _item(cur), _item(cur)
        _revision(cur, bug, "9#a", T0)
        _revision(cur, test_case, "2#a", T0)
        bug_node = _node(cur, bug, "work_item", "1234")
        _edge(cur, bug_node, _node(cur, test_case, "work_item", "77"), "tested_by", bug, "9#a", T0)
        assert _outgoing(cur, bug_node) == ["tested_by"]

        _revision(
            cur, bug, "10#b", T0 + timedelta(hours=1)
        )  # the link was removed in this revision
        assert _outgoing(cur, bug_node) == []
        connection.rollback()


def test_automated_by_is_visible_only_while_the_test_exists_in_the_latest_scan() -> None:
    with _connect() as connection, connection.cursor() as cur:
        test_case, scan = _item(cur), _item(cur, provider="git", kind="code_scan")
        _revision(cur, test_case, "2#a", T0)
        _revision(cur, scan, "c1", T0)
        file_node = _node(cur, scan, "file", "tests/CartTests.cs")
        method = _node(cur, scan, "test_case", "tests/CartTests.cs::Shop.Tests.CartTests.Retry:10")
        _edge(cur, file_node, method, "file_test", scan, "c1", T0)
        case_node = _node(cur, test_case, "work_item", "77")
        _edge(cur, case_node, method, "automated_by", test_case, "2#a", T0)
        assert _outgoing(cur, case_node) == ["automated_by"]

        _revision(cur, scan, "c2", T0 + timedelta(hours=1))  # test deleted: not in the new scan
        assert _outgoing(cur, case_node) == []
        connection.rollback()


def test_the_mapping_is_exact_ignores_old_scans_and_reports_ambiguity() -> None:
    name = "Shop.Tests.CartTests.Retry_" + uuid.uuid4().hex[:6]
    with _connect() as connection, connection.cursor() as cur:

        def matches() -> int:
            cur.execute(MAPPING, {"name": name})
            return len(cur.fetchall())

        repo_a = _item(cur, provider="git", kind="code_scan")
        _revision(cur, repo_a, "c1", T0)
        old = _node(cur, repo_a, "test_case", f"tests/Old.cs::{name}:3")
        _edge(cur, _node(cur, repo_a, "file", "tests/Old.cs"), old, "file_test", repo_a, "c1", T0)
        _revision(cur, repo_a, "c2", T0 + timedelta(hours=1))  # old scan no longer latest
        assert matches() == 0

        current = _node(cur, repo_a, "test_case", f"tests/Cart.cs::{name}:10")
        _edge(
            cur, _node(cur, repo_a, "file", "tests/Cart.cs"), current, "file_test", repo_a, "c2", T0
        )
        similar = _node(cur, repo_a, "test_case", f"tests/Cart.cs::{name}_slow:20")
        _edge(
            cur,
            _node(cur, repo_a, "file", "tests/Cart2.cs"),
            similar,
            "file_test",
            repo_a,
            "c2",
            T0,
        )
        assert matches() == 1  # exact name only; "_slow" is a different test

        repo_b = _item(cur, provider="git", kind="code_scan")
        _revision(cur, repo_b, "d1", T0)
        twin = _node(cur, repo_b, "test_case", f"tests/Cart.cs::{name}:10")
        _edge(cur, _node(cur, repo_b, "file", "tests/Cart.cs"), twin, "file_test", repo_b, "d1", T0)
        assert matches() == 2  # same name in two repositories: ambiguous, not guessed
        connection.rollback()
