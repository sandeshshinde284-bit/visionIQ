"""Read-only LRN-09 retrieval probe for approved local Genoa Kiosk ADO history.

This CLI deliberately does not provide end-user authorization: it must run only
under an already-approved local operator identity and never as an external API.
It checks the current stored scope before retrieval; production user authorization
is a separately deferred integration.
"""

from __future__ import annotations

import argparse
import json
import os
import sys

import psycopg
from psycopg.conninfo import make_conninfo

from app.knowledge.embeddings import from_environment
from app.knowledge.retrieval import retrieve

SCOPES_SQL = """
SELECT DISTINCT jsonb_array_elements_text(c.acl_principals)
FROM knowledge.snapshot_pointer AS sp
JOIN knowledge.snapshot_entries AS se ON se.snapshot_id = sp.snapshot_id
JOIN knowledge.index_chunks AS c ON c.entry_key = se.entry_key
WHERE c.provider = 'azure_devops' AND c.project_id = %s
"""

ALLOWED_SQL = """
SELECT i.item_key
FROM evidence.source_items AS i
JOIN evidence.source_revisions AS r
  ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
WHERE i.deleted_at IS NULL AND i.provider = 'azure_devops'
  AND i.project_id = %(project)s AND r.acl_verified
  AND r.acl_principals ?| %(scopes)s
"""


def connection_string() -> str:
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
    parser.add_argument("--query", required=True, help="Non-sensitive defect description")
    parser.add_argument("--limit", type=int, default=5)
    args = parser.parse_args(argv)
    if not args.query.strip() or not 1 <= args.limit <= 20:
        parser.error("A nonempty query and limit between 1 and 20 are required")

    embedder = from_environment()
    if not embedder.model_id.startswith("vertex:"):
        parser.error("Real-history demo requires EMBEDDING_PROVIDER=vertex")

    conninfo = connection_string()
    with psycopg.connect(conninfo) as conn:
        scopes = [
            row[0]
            for row in conn.execute(SCOPES_SQL, (args.project,))
            if row[0].startswith("ado-project:")
        ]
        if len(scopes) != 1:
            print("FAIL: select one approved ADO project scope")
            return 1
        allowed = {
            row[0] for row in conn.execute(ALLOWED_SQL, {"project": args.project, "scopes": scopes})
        }
    if not allowed:
        print("FAIL: no current verified ADO evidence")
        return 1

    # Snapshot of stored scope metadata, not a live provider/user permission check.
    bundle = retrieve(
        lambda: psycopg.connect(conninfo),
        embedder,
        args.query,
        scopes,
        live_check=lambda key: key in allowed,
        limit=args.limit,
    )
    response = {
        "status": bundle.status,
        "authorization_mode": "approved-local-operator-stored-scope-only",
        "live_end_user_authorization": "NOT_TESTED",
        "snapshot": bundle.snapshot_id,
        "model": bundle.model_id,
        "elapsed_ms": bundle.elapsed_ms,
        "reasons": bundle.reasons,
        "suggestions": bundle.suggestions,
        "results": [
            {
                "item_key": item.item_key,
                "score": item.score,
                "lanes": item.lanes,
                "rank_reasons": item.rank_reasons,
                "counterevidence": item.counterevidence,
                "citation": item.citation,
            }
            for item in bundle.results
        ],
        "lesson_count": len(bundle.lessons),
    }
    print(json.dumps(response, indent=2))
    # Weak/insufficient is a valid retrieval outcome, not a process failure.
    return 0


if __name__ == "__main__":
    sys.exit(main())
