"""Draft human-reviewable LRN-09 benchmark cases from published, redacted ADO evidence.

Local operator tool only. No remote provider calls, synthetic labels or auth bypass.
Do not share output outside the authorised engineering environment.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

import psycopg

from app.knowledge.lrn09_demo import SCOPES_SQL, connection_string

CANDIDATES_SQL = """
WITH candidates AS (
    SELECT DISTINCT ON (i.item_key)
           i.item_key, i.external_id, r.source_url, c.content
    FROM knowledge.snapshot_pointer AS sp
    JOIN knowledge.snapshot_entries AS se ON se.snapshot_id = sp.snapshot_id
    JOIN knowledge.index_chunks AS c ON c.entry_key = se.entry_key
    JOIN evidence.source_items AS i ON i.item_key = se.item_key
    JOIN evidence.source_revisions AS r
      ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
    WHERE i.provider = 'azure_devops' AND i.project_id = %(project)s
      AND i.kind = 'work_item_current' AND i.deleted_at IS NULL
      AND r.acl_verified AND r.acl_principals ?| %(scopes)s
      AND c.acl_principals ?| %(scopes)s
      AND c.provider_revision_id = r.provider_revision_id
    ORDER BY i.item_key, c.chunk_index
)
SELECT item_key, external_id, source_url, content
FROM candidates
ORDER BY external_id DESC
LIMIT %(limit)s
"""


def draft_case(item_key: str, external_id: str, source_url: str, content: str) -> dict:
    """Create a candidate, never a verified relevance judgement."""
    excerpt = re.sub(r"\s+", " ", content).strip()[:180]
    return {
        "query": "",
        "expected_item_keys": [item_key],
        "confirmed_by_human": False,
        "ado_work_item_id": str(external_id),
        "source_url": source_url,
        "indexed_excerpt_for_review": excerpt,
        "review_instruction": (
            "Read the source ticket, write an independently worded defect query "
            "and confirm this is genuinely relevant before setting confirmed_by_human=true."
        ),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project", default="Genoa Kiosk")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--limit", type=int, default=10)
    args = parser.parse_args(argv)
    if not 3 <= args.limit <= 30:
        parser.error("--limit must be between 3 and 30")
    if args.output.exists():
        print("REFUSED: output file already exists; choose a different filename")
        return 2

    conninfo = connection_string()
    with psycopg.connect(conninfo) as conn:
        scopes = [
            row[0]
            for row in conn.execute(SCOPES_SQL, (args.project,))
            if row[0].startswith("ado-project:")
        ]
        if len(scopes) != 1:
            print("BLOCKED: expected exactly one approved indexed ADO project scope")
            return 2
        rows = conn.execute(
            CANDIDATES_SQL,
            {"project": args.project, "scopes": scopes, "limit": args.limit},
        ).fetchall()

    cases = [draft_case(*row) for row in rows]
    if len(cases) < 3:
        print("BLOCKED: fewer than three current published, scope-verified work items")
        return 2
    output = {
        "schema_version": 1,
        "project": args.project,
        "status": "DRAFT_NOT_BENCHMARKED",
        "reviewed_by_human": False,
        "cases": cases,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(output, indent=2) + "\n", encoding="utf-8")
    print(f"Drafted {len(cases)} UNCONFIRMED cases in {args.output}")
    print("Review each source URL, write a new query, then confirm at least three cases.")
    print("Do not publish the local file: it contains internal indexed evidence excerpts.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
