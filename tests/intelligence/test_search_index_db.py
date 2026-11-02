"""LRN-08 on PostgreSQL + pgvector: index guarantees and the card's completion evidence.

Completion evidence: one query answered by both keyword and vector search over the demo history.
"""

import hashlib
import json
import os
import uuid

import pytest

psycopg = pytest.importorskip("psycopg")
from psycopg.conninfo import make_conninfo  # noqa: E402

from app.knowledge import search, snapshots  # noqa: E402
from app.knowledge.embeddings import HashingEmbedder  # noqa: E402

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)

BUG = {
    "id": 4711,
    "workItemType": "Bug",
    "title": "Barcode print fails after payment retry",
    "state": "Active",
    "description": "",
    "reproSteps": "<div>1. Pay at the kiosk</div><div>2. Payment is captured</div>"
    "<div>3. Retry prints no barcode receipt</div>",
    "systemInfo": "<p>Kiosk OS 4.2, contact ana.lopez@contoso.com, Password=Hunter2Secret!</p>",
    "tags": ["kiosk", "payments"],
    "areaPath": "Shop\\Kiosk",
}
PR = {
    "id": 245,
    "title": "Retry printing after captured payment",
    "description": "Adds an idempotency key so retries do not double charge.",
    "reviewThreads": [
        {
            "filePath": "/src/Retry.cs",
            "comments": [{"content": "Retry-only patches were reverted."}],
        }
    ],
}


def _conninfo(prefix: str) -> str:
    return make_conninfo(
        "",
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ[f"DB_{prefix}_USER"],
        password=os.environ[f"DB_{prefix}_PASSWORD"],
    )


class Corpus:
    """Evidence written as ingestion would, plus an in-memory raw archive."""

    def __init__(self) -> None:
        self.scope = f"ado-project:https://dev.azure.com/t{uuid.uuid4().hex[:10]}/Payments"
        self.archive: dict[str, bytes] = {}

    def add(self, kind, record, *, verified=True, revision="1", deleted=False, raw=None):
        item = hashlib.sha256(f"{self.scope}|{kind}|{record.get('id')}".encode()).hexdigest()
        body = raw if raw is not None else json.dumps(record).encode()
        uri = f"mem://{item}/{revision}"
        self.archive[uri] = body
        with psycopg.connect(_conninfo("API")) as conn:
            conn.execute(
                "INSERT INTO evidence.source_items (item_key, provider, source_instance, "
                "project_id, kind, external_id, latest_revision_id, latest_source_updated_at, "
                "first_observed_at, last_observed_at, deleted_at, deletion_reason) VALUES "
                "(%s, 'azure_devops', 'https://dev.azure.com/x', 'Payments', %s, %s, %s, now(), "
                "now(), now(), %s, %s) ON CONFLICT (item_key) DO UPDATE SET "
                "latest_revision_id = EXCLUDED.latest_revision_id, "
                "deleted_at = EXCLUDED.deleted_at, deletion_reason = EXCLUDED.deletion_reason",
                (
                    item,
                    kind,
                    str(record.get("id")),
                    revision,
                    "2026-01-01T00:00:00Z" if deleted else None,
                    "deleted_in_source" if deleted else None,
                ),
            )
            conn.execute(
                "INSERT INTO evidence.source_revisions (item_key, provider_revision_id, "
                "source_updated_at, observed_at, content_sha256, source_url, "
                "retrieved_under_principal, redacted_payload, redaction_version, "
                "normalizer_version, raw_snapshot_uri, "
                "archive_status, acl_verified, acl_principals) VALUES (%s, %s, now(), now(), %s, "
                "'https://dev.azure.com/x/item', 't', '{}', 'v', 'v', %s, 'ARCHIVED', %s, %s)",
                (
                    item,
                    revision,
                    hashlib.sha256(body).hexdigest(),
                    uri,
                    verified,
                    json.dumps([self.scope]),
                ),
            )
        return item

    def read(self, uri: str) -> bytes:
        return self.archive[uri]  # unknown URIs (other tests' rows) fail in isolation

    def index(self, embedder=None, force=False):
        """Index and publish a snapshot (LRN-15); returns the indexing report."""
        report, self.published = snapshots.refresh(
            _conninfo("INTELLIGENCE"), self.read, embedder or HashingEmbedder(), force=force
        )
        return report

    def chunks(self, item):
        """What the published snapshot serves for this item."""
        with psycopg.connect(_conninfo("API")) as conn:
            return conn.execute(
                "SELECT c.chunk_key, c.provider_revision_id, c.acl_principals, c.content, "
                "e.model_id, e.dimension FROM knowledge.index_chunks c "
                "JOIN knowledge.chunk_embeddings e USING (chunk_key) "
                "JOIN knowledge.snapshot_entries se ON se.entry_key = c.entry_key "
                "JOIN knowledge.snapshot_pointer p ON p.snapshot_id = se.snapshot_id "
                "WHERE c.item_key = %s",
                (item,),
            ).fetchall()


def test_every_chunk_keeps_source_revision_access_and_pinned_model_and_no_secrets() -> None:
    corpus = Corpus()
    bug = corpus.add("work_item_current", BUG)
    corpus.index()

    rows = corpus.chunks(bug)
    assert rows
    assert {(r[1], tuple(r[2]), r[4], r[5]) for r in rows} == {
        ("1", (corpus.scope,), "test-hashing-768", 768)
    }
    text = "\n".join(r[3] for r in rows)
    assert "Retry prints no barcode receipt" in text  # Repro Steps are searchable
    assert "Hunter2Secret" not in text and "ana.lopez" not in text  # redacted before indexing


def test_one_query_is_answered_by_both_keyword_and_vector_search() -> None:
    corpus = Corpus()
    bug = corpus.add("work_item_current", BUG)
    pr = corpus.add("pull_request", PR)
    corpus.add(
        "work_item_current", {"id": 9, "workItemType": "Bug", "title": "Login page CSS broken"}
    )
    corpus.index()

    with psycopg.connect(_conninfo("INTELLIGENCE")) as conn:
        by_keyword = search.keyword(conn, "barcode retry", [corpus.scope])
        by_vector = search.vector(
            conn, HashingEmbedder(), "barcode receipt retry kiosk", [corpus.scope]
        )
        elsewhere = search.keyword(conn, "barcode retry", ["ado-project:https://other/Secret"])

    assert by_keyword[0].item_key == bug
    assert by_vector[0].item_key in {bug, pr}
    assert bug in {hit.item_key for hit in by_vector[:3]}
    assert elsewhere == []  # another scope sees nothing


def test_reruns_change_nothing_and_a_new_revision_replaces_old_chunks() -> None:
    corpus = Corpus()
    bug = corpus.add("work_item_current", BUG)
    corpus.index()
    before = {r[0] for r in corpus.chunks(bug)}
    corpus.index()
    assert {r[0] for r in corpus.chunks(bug)} == before  # idempotent

    corpus.add("work_item_current", {**BUG, "title": "Barcode print fails (updated)"}, revision="2")
    corpus.index()
    after = corpus.chunks(bug)
    assert after and {r[1] for r in after} == {"2"} and not before & {r[0] for r in after}


def test_deleted_and_unverified_items_are_never_searchable() -> None:
    corpus = Corpus()
    hidden = corpus.add("work_item_current", {**BUG, "id": 1}, verified=False)
    bug = corpus.add("work_item_current", BUG)
    corpus.index()
    assert corpus.chunks(hidden) == [] and corpus.chunks(bug)

    corpus.add("work_item_current", BUG, revision="1-del", deleted=True)  # tombstoned in source
    corpus.index()
    assert corpus.chunks(bug) == []  # deletion propagates into the index


def test_a_corrupt_item_fails_alone_and_a_model_change_re_embeds() -> None:
    corpus = Corpus()
    broken = corpus.add("work_item_current", {"id": 2}, raw=b"{not json")
    bug = corpus.add("work_item_current", BUG)
    report = corpus.index()
    assert broken in report.failed and corpus.chunks(bug)

    class NewModel(HashingEmbedder):
        model_id = "test-hashing-768-v2"

    corpus.index(NewModel(), force=True)  # deliberate model change in a shared test DB
    assert {r[4] for r in corpus.chunks(bug)} == {"test-hashing-768-v2"}  # never mixed


def test_the_database_rejects_a_wrong_dimension_vector() -> None:
    corpus = Corpus()
    bug = corpus.add("work_item_current", BUG)
    corpus.index()
    chunk_key = corpus.chunks(bug)[0][0]
    with (
        psycopg.connect(_conninfo("INTELLIGENCE")) as conn,
        pytest.raises(psycopg.errors.DataException),
    ):
        conn.execute(
            "INSERT INTO knowledge.chunk_embeddings VALUES (%s, 'other', 768, %s::vector, now())",
            (chunk_key, "[" + ",".join(["0.1"] * 3) + "]"),
        )


@pytest.mark.parametrize("role", ["API", "INTELLIGENCE"])
def test_services_can_use_pgvector_but_never_create_in_public(role) -> None:
    with psycopg.connect(_conninfo(role)) as conn:
        user = conn.execute("SELECT current_user").fetchone()[0]
        usage, create = conn.execute(
            "SELECT has_schema_privilege(%s, 'public', 'USAGE'), "
            "has_schema_privilege(%s, 'public', 'CREATE')",
            (user, user),
        ).fetchone()
        assert (usage, create) == (True, False)
        assert conn.execute("SELECT '[1,0]'::vector <=> '[0,1]'::vector").fetchone()[0] == 1.0
