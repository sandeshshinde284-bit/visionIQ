"""LRN-13 manual, audit-friendly, fail-closed LEARN refresh.

This is the *operator* path for a hackathon, NOT a PR webhook processor,
Pub/Sub publisher, automated scheduler or a root-cause verifier.

Run after a trusted ingestion of an actual PR/commit/revert has completed.
Reindex explicitly supplied canonical item keys; publish a checked snapshot
before reconciling evidence-backed lessons. An indexing/publish failure must
not advance lesson state. No partial-success report is labelled complete.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from datetime import UTC, datetime
from pathlib import Path

import psycopg
from psycopg.conninfo import make_conninfo

from app.knowledge import lesson_history, lessons_cli, snapshots
from app.knowledge.embeddings import Embedder, from_environment
from app.knowledge.index_cli import read_gcs

ITEM_KEY = re.compile(r"[a-f0-9]{64}\Z")
MAX_ITEMS = 20

LESSON_STATUS_SQL = """
SELECT p.pattern_id::text, p.source_instance, p.project_id, p.repository_id,
       p.latest_revision, r.status, r.cause_status
FROM knowledge.lesson_patterns AS p
JOIN knowledge.lesson_revisions AS r
  ON r.pattern_id = p.pattern_id AND r.revision = p.latest_revision
"""


def _conninfo() -> str:
    return make_conninfo(
        "",
        host=os.environ["DB_HOST"],
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ["DB_NAME"],
        user=os.environ["DB_INTELLIGENCE_USER"],
        password=os.environ["DB_INTELLIGENCE_PASSWORD"],
        sslmode=os.environ.get("DB_SSL_MODE", "prefer"),
    )


def _validate_keys(keys: list[str]) -> list[str]:
    if not keys or len(keys) > MAX_ITEMS or len(set(keys)) != len(keys):
        raise ValueError("Provide 1 to 20 unique canonical item keys")
    if not all(ITEM_KEY.fullmatch(key) for key in keys):
        raise ValueError("Canonical item keys must be lowercase 64-character SHA-256 hex")
    return keys


def _source_scopes(conn: psycopg.Connection, keys: list[str]) -> set[tuple[str, str]]:
    rows = conn.execute(
        "SELECT item_key, source_instance, project_id, kind FROM evidence.source_items "
        "WHERE item_key = ANY(%s)",
        (keys,),
    ).fetchall()
    if {row[0] for row in rows} != set(keys):
        raise ValueError("At least one canonical item does not exist")
    if any(row[3] not in ("pull_request", "work_item_current") for row in rows):
        raise ValueError("Manual LRN-13 refresh accepts only PR or work-item evidence")
    return {(row[1], row[2]) for row in rows}


def _lesson_status(
    conn: psycopg.Connection,
    scopes: set[tuple[str, str]],
) -> dict[str, dict]:
    return {
        row[0]: {
            "source_instance": row[1],
            "project_id": row[2],
            "repository_id": row[3],
            "revision": row[4],
            "status": row[5],
            "cause": row[6],
        }
        for row in conn.execute(LESSON_STATUS_SQL)
        if (row[1], row[2]) in scopes
    }


def run(
    conninfo: str,
    keys: list[str],
    read_raw,
    embedder: Embedder,
    *,
    index_limit: int = MAX_ITEMS,
) -> dict:
    """Refresh selected evidence and consolidate lessons, refusing unsafe partials.

    This is not one transaction across GCS/embedding and PostgreSQL. The
    published-snapshot gate ensures failed indexing cannot trigger consolidation.
    A subsequent invocation is idempotent at the lesson revision level.
    """
    keys = _validate_keys(keys)
    if index_limit < len(keys):
        raise ValueError("index_limit must cover every requested item")
    with psycopg.connect(conninfo) as conn:
        scopes = _source_scopes(conn, keys)
        before = _lesson_status(conn, scopes)
        current = snapshots.current(conn)
        previous_id = str(current[0]) if current else None
        highest_event = conn.execute(
            "SELECT COALESCE(MAX(event_id), 0) FROM knowledge.change_events"
        ).fetchone()[0]

    indexed, published = snapshots.refresh(
        conninfo,
        read_raw,
        embedder,
        only=keys,
        limit=index_limit,
    )
    if indexed.failed or published.status != "published":
        raise RuntimeError(
            "Targeted indexing failed or snapshot was not published; "
            "lesson consolidation was not run"
        )

    # The same refresh may affect revocations, but no claim of verified cause
    # is made automatically by LRN-10.
    lesson_result = lessons_cli.run(conninfo)
    with psycopg.connect(conninfo) as conn:
        after = _lesson_status(conn, scopes)
        events = [
            {"type": row[0], "subject": row[1]}
            for row in conn.execute(
                "SELECT event_type, subject FROM knowledge.change_events "
                "WHERE event_id > %s ORDER BY event_id",
                (highest_event,),
            )
            if row[1] in set(keys) | set(after) | set(before)
        ]
        lesson_changes = [
            {
                "pattern_id": pattern,
                "before": before.get(pattern),
                "after": after.get(pattern),
                "history": lesson_history.timeline(conn, pattern)["revisions"],
            }
            for pattern in sorted(set(before) | set(after))
            if before.get(pattern) != after.get(pattern)
        ]
    return {
        "story": "LRN-13",
        "mode": "manual",
        "completed_at": datetime.now(UTC).isoformat(),
        "target_item_keys": keys,
        "snapshot_before": previous_id,
        "snapshot_after": str(published.snapshot_id),
        "snapshot_status": published.status,
        "index": {
            "items_indexed": indexed.indexed,
            "chunks": indexed.chunks,
            "embedded": indexed.embedded,
            "reused": indexed.reused,
        },
        "lesson_run": lesson_result,
        "changed_lessons": lesson_changes,
        "change_events": events,
        "limitations": [
            "Source ingestion must have completed before this command.",
            "No automatic PR-event trigger or Pub/Sub delivery is claimed.",
            "Candidate lessons do not assert a verified shared root cause.",
        ],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Manually publish targeted PR evidence and update lesson history"
    )
    parser.add_argument(
        "--item-key",
        action="append",
        required=True,
        help="Canonical PR or work-item SHA-256 key (repeatable; max 20)",
    )
    parser.add_argument(
        "--output",
        required=True,
        type=Path,
        help="Path to local audit JSON (written on success only)",
    )
    args = parser.parse_args(argv)
    try:
        result = run(
            _conninfo(),
            _validate_keys(args.item_key),
            read_gcs,
            from_environment(),
        )
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(result, indent=2, default=str), encoding="utf-8")
        print(
            json.dumps(
                {
                    "status": "PASS",
                    "snapshot": result["snapshot_after"],
                    "changed_lessons": len(result["changed_lessons"]),
                    "report": str(args.output),
                }
            )
        )
        return 0
    except (ValueError, RuntimeError, KeyError, psycopg.Error, OSError) as error:
        # Never echo credential-bearing connection strings or raw source text.
        print(f"LRN-13 manual refresh FAILED: {type(error).__name__}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
