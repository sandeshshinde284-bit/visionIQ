"""LRN-08 (S07-03..05), LRN-15: build the keyword + vector search index as immutable entries.

Only the latest revision of each current (not deleted) item with a VERIFIED access list is indexed.
An entry is one item revision under one access list, redaction policy, chunker and embedding model;
a change to any of them creates a new entry (old entries are never edited). What is SERVED is
decided by publishing a snapshot (app.knowledge.snapshots), which also drops deleted or re-scoped
items.
Unchanged chunk text reuses its vector; one failing item never stops the others.
"""

from __future__ import annotations

import hashlib
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
WHERE i.deleted_at IS NULL AND r.acl_verified AND jsonb_array_length(r.acl_principals) > 0
  AND i.kind = ANY(%(kinds)s)
  AND (%(only)s::text[] IS NULL OR i.item_key = ANY(%(only)s::text[]))
  AND NOT EXISTS (
      SELECT 1 FROM knowledge.index_entries AS x
      WHERE x.item_key = i.item_key AND x.provider_revision_id = r.provider_revision_id
        AND x.source_content_sha256 = r.content_sha256 AND x.acl_principals = r.acl_principals
        AND x.chunker_version = %(chunker)s AND x.redaction_version = %(redaction)s
        AND x.model_id = %(model)s
        -- LRN-15: an incomplete entry (lost chunk or vector) counts as missing and is repaired
        AND x.chunk_count = (SELECT count(*) FROM knowledge.index_chunks AS c
                         WHERE c.entry_key = x.entry_key)
        AND NOT EXISTS (SELECT 1 FROM knowledge.index_chunks AS c
                        WHERE c.entry_key = x.entry_key AND NOT EXISTS (
                            SELECT 1 FROM knowledge.chunk_embeddings AS ce
                            WHERE ce.chunk_key = c.chunk_key AND ce.model_id = x.model_id)))
ORDER BY i.item_key
LIMIT %(limit)s
"""


@dataclass
class IndexReport:
    indexed: int = 0
    chunks: int = 0
    removed: int = 0  # kept for compatibility; removal now happens when a snapshot is published
    embedded: int = 0  # chunks sent to the embedding model
    reused: int = 0  # unchanged chunks whose existing vector was kept (S12-11)
    failed: list[str] = field(default_factory=list)


def _vector(values: list[float]) -> str:
    return "[" + ",".join(f"{v:.9g}" for v in values) + "]"


def run(
    conninfo: str,
    read_raw: Callable[[str], bytes],
    embedder: Embedder,
    limit: int = 500,
    kinds: tuple[str, ...] = INDEXED_KINDS,
    only: list[str] | None = None,
) -> IndexReport:
    """One bounded indexing pass. ``read_raw`` returns an archived raw evidence object by URI.

    ``only`` restricts the pass to given item keys (targeted re-index after a PR event, S12-11).
    """
    report = IndexReport()
    with psycopg.connect(conninfo, autocommit=True) as conn:
        conn.execute("SELECT pg_advisory_lock(%s)", (LOCK,))
        try:
            pending = conn.execute(
                PENDING_SQL,
                {
                    "kinds": list(kinds),
                    "chunker": CHUNKER_VERSION,
                    "redaction": REDACTION_VERSION,
                    "model": embedder.model_id,
                    "only": only,
                    "limit": limit,
                },
            ).fetchall()
            for row in pending:
                try:
                    total, embedded, reused = _index_item(conn, row, read_raw, embedder)
                    report.chunks += total
                    report.embedded += embedded
                    report.reused += reused
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


def _chunk_key(entry_key: str, chunk_key: str) -> str:
    """Chunk keys are scoped to their entry, so entries of the same revision never collide."""
    return hashlib.sha256(f"{entry_key}|{chunk_key}".encode()).hexdigest()


def _parse_vector(text: str) -> list[float]:
    return [float(v) for v in text.strip("[]").split(",")]


def _index_item(
    conn: psycopg.Connection, row, read_raw, embedder: Embedder
) -> tuple[int, int, int]:
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
    # S12-11: only chunks whose text changed are re-embedded; unchanged ones keep their vector.
    # Keyed by content_sha256, not chunk_key/index, so a chunk that merely shifted position
    # (e.g. an earlier section grew) still matches its old vector instead of being re-embedded.
    known = {
        sha: _parse_vector(vector)
        for sha, vector in conn.execute(
            "SELECT c.content_sha256, e.embedding::text FROM knowledge.index_chunks AS c "
            "JOIN knowledge.chunk_embeddings AS e USING (chunk_key) "
            "WHERE c.item_key = %s AND e.model_id = %s",
            (item_key, embedder.model_id),
        )
    }
    fresh = [c for c in chunks if c.content_sha256 not in known]
    new_vectors = dict(
        zip(
            (c.content_sha256 for c in fresh),
            embedder.embed_documents([c.text for c in fresh]) if fresh else [],
            strict=True,
        )
    )
    vectors = [known.get(c.content_sha256) or new_vectors[c.content_sha256] for c in chunks]
    now = datetime.now(UTC)
    acl_json = json.dumps(acl)
    entry_key = hashlib.sha256(
        "|".join(
            [
                item_key,
                revision,
                content_sha,
                acl_json,
                CHUNKER_VERSION,
                REDACTION_VERSION,
                embedder.model_id,
            ]
        ).encode()
    ).hexdigest()
    with conn.transaction():  # one immutable entry, written atomically
        conn.execute(
            "INSERT INTO knowledge.index_entries (entry_key, item_key, provider_revision_id, "
            "source_content_sha256, acl_principals, chunker_version, redaction_version, model_id, "
            "chunk_count, indexed_at) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s) "
            "ON CONFLICT (entry_key) DO NOTHING",
            (
                entry_key,
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
        # Repairing an incomplete entry: its derived chunks are rebuilt (entry metadata unchanged).
        conn.execute("DELETE FROM knowledge.index_chunks WHERE entry_key = %s", (entry_key,))
        for piece, vector in zip(chunks, vectors, strict=True):
            conn.execute(
                "INSERT INTO knowledge.index_chunks (chunk_key, entry_key, item_key, "
                "provider_revision_id, chunk_index, section, content, content_sha256, provider, "
                "kind, project_id, repository_id, source_url, source_updated_at, acl_principals, "
                "redaction_version, chunker_version) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, "
                "%s, %s, %s, %s, %s, %s, %s, %s)",
                (
                    _chunk_key(entry_key, piece.chunk_key),
                    entry_key,
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
                (
                    _chunk_key(entry_key, piece.chunk_key),
                    embedder.model_id,
                    embedder.dimension,
                    _vector(vector),
                    now,
                ),
            )
        conn.execute(  # knowledge.changed (S12-15), committed with the change itself
            "INSERT INTO knowledge.change_events (event_type, subject, payload) "
            "VALUES ('index.item_updated', %s, %s)",
            (
                item_key,
                json.dumps(
                    {
                        "item_key": item_key,
                        "entry_key": entry_key,
                        "kind": kind,
                        "revision": revision,
                        "chunks": len(chunks),
                        "re_embedded": len(fresh),
                    }
                ),
            ),
        )
    log.debug(
        "index.item_indexed",
        item_key=item_key,
        kind=kind,
        chunks=len(chunks),
        embedded=len(fresh),
        reused=len(chunks) - len(fresh),
    )
    return len(chunks), len(fresh), len(chunks) - len(fresh)
