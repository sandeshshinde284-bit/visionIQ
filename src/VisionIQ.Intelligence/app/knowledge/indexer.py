"""LRN-08 (S07-03..05): build and maintain the keyword + vector search index.

Only the latest revision of each current (not deleted) item with a VERIFIED access list is indexed.
An item is re-indexed when its content, access list, redaction policy, chunker or embedding model
changes; deleted or no-longer-verified items are removed (deletion propagates to the index, 6.10).
Each item is replaced atomically; one failing item never stops the others.
"""

from __future__ import annotations

import json
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import UTC, datetime

import psycopg
import structlog

from app.knowledge.chunking import CHUNKER_VERSION, chunk
from app.knowledge.embeddings import Embedder
from app.knowledge.extract import INDEXED_KINDS, extract
from app.knowledge.redaction import REDACTION_VERSION, redact

LOCK = 0x56495149  # "VIQI": one indexer at a time
log = structlog.get_logger("visioniq.indexer")

PENDING_SQL = """
SELECT i.item_key, i.provider, i.kind, i.project_id, i.repository_id, r.provider_revision_id,
       r.content_sha256, r.raw_snapshot_uri, r.acl_principals, r.source_updated_at, r.source_url
FROM evidence.source_items AS i
JOIN evidence.source_revisions AS r
  ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
LEFT JOIN knowledge.index_items AS x ON x.item_key = i.item_key
WHERE i.deleted_at IS NULL AND r.acl_verified AND jsonb_array_length(r.acl_principals) > 0
  AND i.kind = ANY(%(kinds)s)
  AND (x.item_key IS NULL OR x.source_content_sha256 <> r.content_sha256
       OR x.provider_revision_id <> r.provider_revision_id OR x.acl_principals <> r.acl_principals
       OR x.chunker_version <> %(chunker)s OR x.redaction_version <> %(redaction)s
       OR x.model_id <> %(model)s)
ORDER BY i.item_key
LIMIT %(limit)s
"""

STALE_SQL = """
DELETE FROM knowledge.index_items AS x
WHERE NOT EXISTS (
    SELECT 1 FROM evidence.source_items AS i
    JOIN evidence.source_revisions AS r
      ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
    WHERE i.item_key = x.item_key AND i.deleted_at IS NULL AND r.acl_verified
)
"""


@dataclass
class IndexReport:
    indexed: int = 0
    chunks: int = 0
    removed: int = 0
    failed: list[str] = field(default_factory=list)


def _vector(values: list[float]) -> str:
    return "[" + ",".join(f"{v:.9g}" for v in values) + "]"


def run(
    conninfo: str,
    read_raw: Callable[[str], bytes],
    embedder: Embedder,
    limit: int = 500,
    kinds: tuple[str, ...] = INDEXED_KINDS,
) -> IndexReport:
    """One bounded indexing pass. ``read_raw`` returns an archived raw evidence object by URI."""
    report = IndexReport()
    with psycopg.connect(conninfo, autocommit=True) as conn:
        conn.execute("SELECT pg_advisory_lock(%s)", (LOCK,))
        try:
            report.removed = conn.execute(STALE_SQL).rowcount
            pending = conn.execute(
                PENDING_SQL,
                {
                    "kinds": list(kinds),
                    "chunker": CHUNKER_VERSION,
                    "redaction": REDACTION_VERSION,
                    "model": embedder.model_id,
                    "limit": limit,
                },
            ).fetchall()
            for row in pending:
                try:
                    report.chunks += _index_item(conn, row, read_raw, embedder)
                    report.indexed += 1
                except Exception as error:  # isolate: one bad item never stops the run
                    report.failed.append(row[0])
                    log.warning(
                        "index.item_failed",
                        item_key=row[0],
                        kind=row[2],
                        error_type=type(error).__name__,
                    )
        finally:
            conn.execute("SELECT pg_advisory_unlock(%s)", (LOCK,))
    return report


def _index_item(conn: psycopg.Connection, row, read_raw, embedder: Embedder) -> int:
    (
        item_key,
        provider,
        kind,
        project,
        repository,
        revision,
        content_sha,
        uri,
        acl,
        source_updated,
        source_url,
    ) = row
    record = json.loads(read_raw(uri))
    document = extract(kind, record)
    title = redact(document.title).text
    sections = [type(s)(s.name, redact(s.text).text) for s in document.sections]
    chunks = chunk(item_key, revision, document.label, title, sections)
    vectors = embedder.embed_documents([c.text for c in chunks]) if chunks else []
    now = datetime.now(UTC)
    acl_json = json.dumps(acl)
    with conn.transaction():  # replace the item atomically
        conn.execute("DELETE FROM knowledge.index_items WHERE item_key = %s", (item_key,))
        conn.execute(
            "INSERT INTO knowledge.index_items (item_key, provider_revision_id, "
            "source_content_sha256, acl_principals, chunker_version, redaction_version, model_id, "
            "chunk_count, indexed_at) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s)",
            (
                item_key,
                revision,
                content_sha,
                acl_json,
                CHUNKER_VERSION,
                REDACTION_VERSION,
                embedder.model_id,
                len(chunks),
                now,
            ),
        )
        for piece, vector in zip(chunks, vectors, strict=True):
            conn.execute(
                "INSERT INTO knowledge.index_chunks (chunk_key, item_key, provider_revision_id, "
                "chunk_index, section, content, content_sha256, provider, kind, project_id, "
                "repository_id, source_url, source_updated_at, acl_principals, redaction_version, "
                "chunker_version) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, "
                "%s, %s)",
                (
                    piece.chunk_key,
                    item_key,
                    revision,
                    piece.chunk_index,
                    piece.section,
                    piece.text,
                    piece.content_sha256,
                    provider,
                    kind,
                    project,
                    repository,
                    source_url,
                    source_updated,
                    acl_json,
                    REDACTION_VERSION,
                    CHUNKER_VERSION,
                ),
            )
            conn.execute(
                "INSERT INTO knowledge.chunk_embeddings (chunk_key, model_id, dimension, "
                "embedding, embedded_at) VALUES (%s, %s, %s, %s::vector, %s)",
                (piece.chunk_key, embedder.model_id, embedder.dimension, _vector(vector), now),
            )
    log.debug("index.item_indexed", item_key=item_key, kind=kind, chunks=len(chunks))
    return len(chunks)
