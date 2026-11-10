"""Read-only LRN-08 acceptance against a published ADO index.

Checks source/revision/ACL/model/vector provenance and exercises both retrieval lanes.
Requires the intelligence DB login, Vertex credentials, and an approved ADO project scope.
No writes, source body prints, or Confluence permission bypass.
"""

from __future__ import annotations

import argparse
import json
import os
import sys

import psycopg
from psycopg.conninfo import make_conninfo

from app.knowledge import search
from app.knowledge.embeddings import from_environment

VALIDATE_SQL = """
SELECT count(*) AS chunks,
       count(DISTINCT c.item_key) AS items,
       count(*) FILTER (
          WHERE i.item_key IS NULL OR i.deleted_at IS NOT NULL OR
                r.item_key IS NULL OR NOT r.acl_verified OR
                c.provider_revision_id <> i.latest_revision_id OR
                c.acl_principals <> r.acl_principals OR
                c.project_id <> i.project_id OR
                c.provider <> i.provider
       ) AS invalid_provenance,
       count(*) FILTER (WHERE ce.chunk_key IS NULL OR
            ce.model_id <> e.model_id OR ce.dimension <> %(dimension)s) AS invalid_vectors,
       count(DISTINCT e.model_id) AS model_count
FROM knowledge.snapshot_pointer AS sp
JOIN knowledge.snapshot_entries AS se ON se.snapshot_id = sp.snapshot_id
JOIN knowledge.index_entries AS e ON e.entry_key = se.entry_key
JOIN knowledge.index_chunks AS c ON c.entry_key = e.entry_key
LEFT JOIN knowledge.chunk_embeddings AS ce
  ON ce.chunk_key = c.chunk_key AND ce.model_id = e.model_id
LEFT JOIN evidence.source_items AS i ON i.item_key = c.item_key
LEFT JOIN evidence.source_revisions AS r
  ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
WHERE c.provider = 'azure_devops' AND c.project_id = %(project)s
  AND e.model_id = %(model)s
"""

SCOPES_SQL = """
SELECT DISTINCT jsonb_array_elements_text(c.acl_principals)
FROM knowledge.snapshot_pointer AS sp
JOIN knowledge.snapshot_entries AS se ON se.snapshot_id = sp.snapshot_id
JOIN knowledge.index_chunks AS c ON c.entry_key = se.entry_key
WHERE c.provider = 'azure_devops' AND c.project_id = %s
"""

ITEMS_SQL = """
SELECT DISTINCT c.item_key FROM knowledge.snapshot_pointer AS sp
JOIN knowledge.snapshot_entries AS se ON se.snapshot_id = sp.snapshot_id
JOIN knowledge.index_chunks AS c ON c.entry_key = se.entry_key
WHERE c.provider = 'azure_devops' AND c.project_id = %s
"""


def conninfo() -> str:
    return make_conninfo(
        "",
        host=os.environ["DB_HOST"],
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ["DB_NAME"],
        user=os.environ["DB_INTELLIGENCE_USER"],
        password=os.environ["DB_INTELLIGENCE_PASSWORD"],
        sslmode=os.environ.get("DB_SSL_MODE", "prefer"),
    )


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", default="Genoa Kiosk")
    parser.add_argument("--query", required=True, help="A real, non-sensitive demo-history query")
    args = parser.parse_args(argv)

    embedder = from_environment()
    if not embedder.model_id.startswith("vertex:"):
        parser.error("Real LRN-08 acceptance requires EMBEDDING_PROVIDER=vertex")
    if not args.query.strip():
        parser.error("--query must not be blank")

    with psycopg.connect(conninfo()) as conn:
        snapshot = conn.execute("SELECT snapshot_id FROM knowledge.snapshot_pointer").fetchone()
        if not snapshot:
            print("FAIL: no published snapshot")
            return 1
        scopes = [
            row[0]
            for row in conn.execute(SCOPES_SQL, (args.project,))
            if row[0].startswith("ado-project:")
        ]
        if len(scopes) != 1:
            print("FAIL: expected exactly one ADO project scope for the demo project")
            return 1
        stats = conn.execute(
            VALIDATE_SQL,
            {"project": args.project, "model": embedder.model_id, "dimension": embedder.dimension},
        ).fetchone()
        chunks, items, invalid_provenance, invalid_vectors, models = stats
        if not chunks or not items or invalid_provenance or invalid_vectors or models != 1:
            print(
                json.dumps(
                    {
                        "status": "FAIL",
                        "items": items,
                        "chunks": chunks,
                        "invalid_provenance": invalid_provenance,
                        "invalid_vectors": invalid_vectors,
                        "models": models,
                    }
                )
            )
            return 1

        valid_keys = {row[0] for row in conn.execute(ITEMS_SQL, (args.project,))}
        by_keyword = search.keyword(conn, args.query, scopes)
        by_vector = search.vector(conn, embedder, args.query, scopes)
        keyword_keys = {h.item_key for h in by_keyword}
        vector_keys = {h.item_key for h in by_vector}
        safe = keyword_keys <= valid_keys and vector_keys <= valid_keys
        result = {
            "status": "PASS" if by_keyword and by_vector and safe else "FAIL",
            "snapshot": snapshot[0],
            "project": args.project,
            "model": embedder.model_id,
            "dimension": embedder.dimension,
            "indexed_items": items,
            "indexed_chunks": chunks,
            "invalid_provenance": invalid_provenance,
            "invalid_vectors": invalid_vectors,
            "keyword_hits": len(by_keyword),
            "vector_hits": len(by_vector),
            "overlap_items": len(keyword_keys & vector_keys),
            "out_of_scope_hits": 0 if safe else len((keyword_keys | vector_keys) - valid_keys),
        }
        print(json.dumps(result, indent=2))
        return 0 if result["status"] == "PASS" else 1


if __name__ == "__main__":
    sys.exit(main())
