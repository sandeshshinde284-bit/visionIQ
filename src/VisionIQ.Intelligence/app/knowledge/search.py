"""LRN-08: keyword and vector search over the index, pre-filtered by the caller's access scopes.

These are candidate retrievers: LRN-09 merges them with graph results and re-checks access live
(master design 7.1: scope applies at retrieval and again at hydration/citation time). Query text is
redacted before it is embedded, because people paste logs and the query goes to the model provider.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass

import psycopg

from app.knowledge.embeddings import Embedder
from app.knowledge.indexer import _vector
from app.knowledge.redaction import redact


@dataclass(frozen=True)
class Hit:
    chunk_key: str
    item_key: str
    kind: str
    section: str
    content: str
    provider_revision_id: str
    source_url: str | None
    score: float


_COLUMNS = (
    "c.chunk_key, c.item_key, c.kind, c.section, c.content, c.provider_revision_id, c.source_url"
)


def keyword(
    conn: psycopg.Connection, query: str, scopes: Sequence[str], limit: int = 20
) -> list[Hit]:
    """PostgreSQL full-text search (web-search syntax), best matches first."""
    if not scopes or not query.strip():
        return []
    rows = conn.execute(
        f"SELECT {_COLUMNS}, ts_rank_cd(c.tsv, q) AS score "
        "FROM knowledge.index_chunks AS c, websearch_to_tsquery('english', %(q)s) AS q "
        "WHERE c.tsv @@ q AND c.acl_principals ?| %(scopes)s "
        "ORDER BY score DESC, c.chunk_key LIMIT %(limit)s",
        {"q": query, "scopes": list(scopes), "limit": limit},
    ).fetchall()
    return [Hit(*row[:7], float(row[7])) for row in rows]


def vector(
    conn: psycopg.Connection, embedder: Embedder, query: str, scopes: Sequence[str], limit: int = 20
) -> list[Hit]:
    """Nearest chunks by cosine similarity, only from the embedder's own model (never mixed)."""
    if not scopes or not query.strip():
        return []
    embedding = _vector(embedder.embed_query(redact(query).text))
    with conn.transaction():
        # Filtering by access scopes happens after the approximate index scan, so widen the scan;
        # on pgvector >= 0.8 also keep scanning until enough rows pass the filter.
        conn.execute(f"SET LOCAL hnsw.ef_search = {max(40, min(1000, limit * 10))}")
        try:
            with conn.transaction():
                conn.execute("SET LOCAL hnsw.iterative_scan = 'relaxed_order'")
        except psycopg.errors.Error:
            pass  # older pgvector: the wider ef_search above is the fallback
        rows = conn.execute(
            f"SELECT {_COLUMNS}, 1 - (e.embedding <=> %(v)s::vector) AS score "
            "FROM knowledge.chunk_embeddings AS e "
            "JOIN knowledge.index_chunks AS c USING (chunk_key) "
            "WHERE e.model_id = %(model)s AND c.acl_principals ?| %(scopes)s "
            "ORDER BY e.embedding <=> %(v)s::vector, c.chunk_key LIMIT %(limit)s",
            {"v": embedding, "model": embedder.model_id, "scopes": list(scopes), "limit": limit},
        ).fetchall()
    return [Hit(*row[:7], float(row[7])) for row in rows]
