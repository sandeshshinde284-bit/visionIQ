"""Inspect the approved, canonical LRN-07 graph from LOCAL PostgreSQL only.

The inspector is an operator-only CLI, never an HTTP endpoint or an authorization
mechanism. It reads the same latest-active assertions as the .NET graph store,
preserves provenance, and never retrieves raw source snapshots or credentials.

Example (use approved source IDs from your own ADO configuration):
    python scripts/inspect_graph.py --source-instance "$ADO_ORG_URL" \
        --project "$ADO_PROJECT" --work-item-id "$TASK_ID"

Cross-project traversal requires an additional explicit --allow-project value.
Environment: DB_TARGET=local, DB_HOST=localhost/127.0.0.1, DB_NAME,
DB_API_USER, DB_API_PASSWORD. No default provider/org/project/repo identifiers.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from collections import deque

import psycopg
from psycopg.rows import dict_row

# Match the .NET PostgresRelationshipGraphStore.OutgoingAsync contract.
# A newer retraction/dispute wins even when older active evidence remains.
_ACTIVE_EDGES = """
WITH latest AS (
    SELECT DISTINCT ON (g.relationship_key)
        g.from_node_key, g.to_node_key, g.relation_type, g.classification,
        g.assertion_state, g.provenance_item_key, g.provenance_revision_id,
        g.relationship_key, g.ticket_fix_association_key
    FROM evidence.graph_assertions g
    LEFT JOIN evidence.source_items current_source
      ON current_source.item_key = g.provenance_item_key
    WHERE g.from_node_key = %s
    ORDER BY g.relationship_key, g.source_asserted_at DESC,
             g.observed_at DESC,
             (g.provenance_revision_id = current_source.latest_revision_id)
                DESC NULLS LAST,
             g.assertion_key DESC
)
SELECT latest.to_node_key, latest.relation_type, latest.classification,
       latest.provenance_item_key, latest.provenance_revision_id,
       provenance.project_id AS proof_project
FROM latest
JOIN evidence.graph_nodes destination_node
  ON destination_node.node_key = latest.to_node_key
JOIN evidence.source_items destination
  ON destination.item_key = destination_node.anchor_item_key
 AND destination.deleted_at IS NULL
JOIN evidence.source_items provenance
  ON provenance.item_key = latest.provenance_item_key
 AND provenance.deleted_at IS NULL
LEFT JOIN evidence.ticket_fix_links fix
  ON fix.association_key = latest.ticket_fix_association_key
WHERE latest.assertion_state = 'active'
  AND latest.classification IN ('explicit','verified')
  AND (latest.relation_type NOT IN (
      'commit_file','file_symbol','file_dependency','repository_dependency',
      'file_test','tested_by','automated_by'
  ) OR provenance.latest_revision_id = latest.provenance_revision_id)
  AND (latest.relation_type <> 'automated_by' OR EXISTS (
      SELECT 1 FROM evidence.graph_assertions declared
      JOIN evidence.source_items scan
        ON scan.item_key = declared.provenance_item_key
      WHERE declared.to_node_key = latest.to_node_key
        AND declared.relation_type = 'file_test'
        AND declared.provenance_revision_id = scan.latest_revision_id
        AND declared.assertion_state = 'active'
        AND declared.classification IN ('explicit','verified')
        AND scan.deleted_at IS NULL))
  AND (latest.ticket_fix_association_key IS NULL
       OR (fix.association_key IS NOT NULL
           AND fix.review_status IS DISTINCT FROM 'disputed'
           AND jsonb_array_length(fix.revert_commit_shas) = 0
           AND fix.retracted_at IS NULL
           AND (fix.pull_request_id IS NULL OR NOT EXISTS (
               SELECT 1 FROM evidence.ticket_fix_links sibling
               WHERE sibling.source_instance = fix.source_instance
                 AND sibling.project_id = fix.project_id
                 AND sibling.ticket_id = fix.ticket_id
                 AND sibling.repository_id IS NOT DISTINCT FROM fix.repository_id
                 AND sibling.pull_request_id = fix.pull_request_id
                 AND (sibling.review_status = 'disputed'
                      OR jsonb_array_length(sibling.revert_commit_shas) > 0)))))
ORDER BY latest.relationship_key
LIMIT %s
"""

_NODE = """
SELECT n.node_key, n.node_kind, n.native_id, i.item_key,
       i.provider, i.source_instance, i.project_id, i.repository_id
FROM evidence.graph_nodes n
JOIN evidence.source_items i ON i.item_key = n.anchor_item_key
WHERE n.node_key = %s
  AND i.deleted_at IS NULL
  AND i.latest_revision_id IS NOT NULL
"""

_ROOT = """
SELECT n.node_key FROM evidence.source_items i
JOIN evidence.graph_nodes n ON n.anchor_item_key = i.item_key
WHERE i.provider = 'azure_devops'
  AND rtrim(i.source_instance, '/') = rtrim(%s, '/')
  AND i.project_id = %s
  AND i.kind = 'work_item_current'
  AND i.external_id = %s
  AND i.deleted_at IS NULL AND i.latest_revision_id IS NOT NULL
  AND n.node_kind = 'work_item' AND n.native_id = %s
"""


def database_settings(env: dict[str, str]) -> dict:
    """Allow only a local DB; never read or log credentials from argv."""
    host = env.get("DB_HOST", "").strip().lower()
    if env.get("DB_TARGET", "").lower() != "local" or host not in {"localhost", "127.0.0.1", "::1"}:
        raise ValueError("This inspector requires DB_TARGET=local and loopback DB_HOST")
    required = ("DB_NAME", "DB_API_USER", "DB_API_PASSWORD")
    missing = [name for name in required if not env.get(name)]
    if missing:
        raise ValueError("Missing local database configuration: " + ", ".join(missing))
    return {
        "host": host,
        "port": int(env.get("DB_PORT", "5432")),
        "dbname": env["DB_NAME"],
        "user": env["DB_API_USER"],
        "password": env["DB_API_PASSWORD"],
        "connect_timeout": 10,
    }


def inspect(
    connection: psycopg.Connection,
    source_instance: str,
    project: str,
    work_item_id: str,
    projects: set[str],
    depth: int,
    max_nodes: int,
) -> dict:
    """Read only the latest navigable source-backed edges; no inferred links."""
    with connection.cursor(row_factory=dict_row) as cursor:
        cursor.execute(_ROOT, (source_instance, project, work_item_id, work_item_id))
        roots = cursor.fetchall()
        if len(roots) != 1:
            raise ValueError("No unique canonical work-item graph root; run ingestion first")
        root = roots[0]["node_key"]
        seen: dict[str, dict] = {}
        edges: list[dict] = []
        queue = deque([(root, 0)])
        visited: set[str] = set()

        def node_for(key: str) -> dict | None:
            cursor.execute(_NODE, (key,))
            row = cursor.fetchone()
            if row is None or row["project_id"] not in projects:
                return None
            return {
                "key": row["node_key"],
                "kind": row["node_kind"],
                "native_id": row["native_id"],
                "provider": row["provider"],
                "project": row["project_id"],
                "repository": row["repository_id"],
                "source": row["source_instance"],
            }

        start = node_for(root)
        if start is None:
            raise ValueError("Graph root is missing or outside approved project scope")
        seen[root] = start
        while queue:
            key, hops = queue.popleft()
            if key in visited:
                continue
            visited.add(key)
            if hops >= depth:
                continue
            cursor.execute(_ACTIVE_EDGES, (key, 200))
            rows = cursor.fetchall()
            for row in rows:
                if row["proof_project"] not in projects:
                    continue
                dest = row["to_node_key"]
                item = seen.get(dest) or node_for(dest)
                if item is None:
                    continue
                if dest not in seen:
                    if len(seen) >= max_nodes:
                        continue
                    seen[dest] = item
                edges.append(
                    {
                        "from": key,
                        "to": dest,
                        "relation": row["relation_type"],
                        "classification": row["classification"],
                        "provenance_item_key": row["provenance_item_key"],
                        "provenance_revision": row["provenance_revision_id"],
                    }
                )
                if dest not in visited:
                    queue.append((dest, hops + 1))
        return {
            "root": root,
            "nodes": list(seen.values()),
            "edges": edges,
            "note": (
                "Source-backed structural links only. Declared tests are not proof "
                "of execution or coverage. Internal local operator use; no user ACL validation."
            ),
        }


def main() -> int:
    parser = argparse.ArgumentParser(description="Inspect the local canonical VisionIQ graph")
    parser.add_argument("--source-instance", required=True, help="Approved ADO organization URL")
    parser.add_argument("--project", required=True, help="Approved provider-native project")
    parser.add_argument("--work-item-id", required=True, help="ADO Task or Bug ID")
    parser.add_argument(
        "--allow-project",
        action="append",
        default=[],
        help="Additional approved project scope for explicit cross-project links",
    )
    parser.add_argument("--depth", type=int, default=4, choices=range(1, 6))
    parser.add_argument("--max-nodes", type=int, default=100)
    args = parser.parse_args()
    if not args.work_item_id.isascii() or not args.work_item_id.isdecimal():
        parser.error("--work-item-id must be a numeric provider-native ID")
    if not 1 <= args.max_nodes <= 100:
        parser.error("--max-nodes must be between 1 and 100")
    try:
        settings = database_settings(os.environ)
        with psycopg.connect(**settings) as db, db.transaction():
            db.execute("SET TRANSACTION READ ONLY")
            result = inspect(
                db,
                args.source_instance,
                args.project,
                args.work_item_id,
                {args.project, *args.allow_project},
                args.depth,
                args.max_nodes,
            )
        print(json.dumps(result, indent=2))
        return 0
    except (ValueError, psycopg.Error) as error:
        # Never show SQL connection strings or raw provider errors (may include secrets).
        print(f"Graph inspection failed: {type(error).__name__}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
