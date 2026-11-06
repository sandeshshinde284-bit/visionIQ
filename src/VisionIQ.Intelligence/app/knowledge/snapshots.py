"""LRN-15: publish consistent knowledge snapshots (master design 6.6 stage 9, 6.11, 6.15, KB-5).

A snapshot lists which index entry of each item is served. For every item that is readable now it
takes the entry for the current revision, else the last good one (served but labelled outdated), and
only ever an entry with the item's CURRENT access list and the current redaction policy, chunker and
embedding model. A candidate is published only after consistency, access and coverage checks pass,
by moving one pointer; otherwise it is rejected with its reasons and the previous snapshot keeps
serving. Rollback moves the pointer back to the last good snapshot.
"""

from __future__ import annotations

import json
import uuid
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import UTC, datetime

import psycopg
import structlog

from app.knowledge import indexer
from app.knowledge.chunking import CHUNKER_VERSION
from app.knowledge.embeddings import Embedder
from app.knowledge.extract import INDEXED_KINDS
from app.knowledge.redaction import REDACTION_VERSION

# Coverage: a candidate may lose at most 20% of the previous snapshot's items that are STILL
# readable (indexing gaps). Items deleted or re-scoped at the source always leave immediately and
# never count, so a safety check can never delay a revocation.
MAX_DROP = 0.2
KEEP = 5  # published/superseded snapshots kept for rollback
LOCK = 0x56495153  # "VIQS": one publisher at a time
log = structlog.get_logger("visioniq.snapshots")

MEMBERSHIP_SQL = """
INSERT INTO knowledge.snapshot_entries (snapshot_id, item_key, entry_key)
SELECT %(snap)s, i.item_key, e.entry_key
FROM evidence.source_items AS i
JOIN evidence.source_revisions AS r
  ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
JOIN LATERAL (
    SELECT x.entry_key FROM knowledge.index_entries AS x
    WHERE x.item_key = i.item_key AND x.acl_principals = r.acl_principals
      AND x.model_id = %(model)s AND x.chunker_version = %(chunker)s
      AND x.redaction_version = %(redaction)s
      -- never an incomplete entry: fall back to the last complete one (served, labelled outdated)
      AND x.chunk_count = (SELECT count(*) FROM knowledge.index_chunks AS c
                         WHERE c.entry_key = x.entry_key)
        AND NOT EXISTS (SELECT 1 FROM knowledge.index_chunks AS c
                        WHERE c.entry_key = x.entry_key AND NOT EXISTS (
                            SELECT 1 FROM knowledge.chunk_embeddings AS ce
                            WHERE ce.chunk_key = c.chunk_key AND ce.model_id = x.model_id))
    -- Prefer an entry matching the item's CURRENT revision/content; otherwise take the most
    -- recently indexed stale entry (still served, so the item isn't dropped while it reindexes).
    ORDER BY (x.provider_revision_id = r.provider_revision_id
              AND x.source_content_sha256 = r.content_sha256) DESC, x.indexed_at DESC
    LIMIT 1
) AS e ON true
WHERE i.deleted_at IS NULL AND r.acl_verified AND i.kind = ANY(%(kinds)s)
"""

COUNTS_SQL = """
SELECT count(*),
       count(*) FILTER (WHERE e.provider_revision_id = r.provider_revision_id
                          AND e.source_content_sha256 = r.content_sha256),
       (SELECT count(*) FROM evidence.source_items AS i2
        JOIN evidence.source_revisions AS r2
          ON r2.item_key = i2.item_key AND r2.provider_revision_id = i2.latest_revision_id
        WHERE i2.deleted_at IS NULL AND r2.acl_verified AND i2.kind = ANY(%(kinds)s))
FROM knowledge.snapshot_entries AS se
JOIN knowledge.index_entries AS e USING (entry_key)
JOIN evidence.source_items AS i ON i.item_key = se.item_key
JOIN evidence.source_revisions AS r
  ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
WHERE se.snapshot_id = %(snap)s
"""

INCONSISTENT_SQL = """
SELECT count(*) FROM knowledge.snapshot_entries AS se
JOIN knowledge.index_entries AS e USING (entry_key)
WHERE se.snapshot_id = %(snap)s AND (
    e.chunk_count <> (SELECT count(*) FROM knowledge.index_chunks c WHERE c.entry_key = e.entry_key)
    OR EXISTS (SELECT 1 FROM knowledge.index_chunks c WHERE c.entry_key = e.entry_key
               AND NOT EXISTS (SELECT 1 FROM knowledge.chunk_embeddings ce
                               WHERE ce.chunk_key = c.chunk_key AND ce.model_id = %(model)s)))
"""

LOST_SQL = """
SELECT count(*) FROM knowledge.snapshot_entries AS p
JOIN evidence.source_items AS i ON i.item_key = p.item_key
JOIN evidence.source_revisions AS r
  ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
WHERE p.snapshot_id = %(prev)s AND i.deleted_at IS NULL AND r.acl_verified
  AND i.kind = ANY(%(kinds)s)
  AND NOT EXISTS (SELECT 1 FROM knowledge.snapshot_entries AS c
                  WHERE c.snapshot_id = %(snap)s AND c.item_key = p.item_key)
"""

UNREADABLE_SQL = """
SELECT count(*) FROM knowledge.snapshot_entries AS se
JOIN knowledge.index_entries AS e USING (entry_key)
LEFT JOIN evidence.source_items AS i ON i.item_key = se.item_key
LEFT JOIN evidence.source_revisions AS r
  ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
WHERE se.snapshot_id = %(snap)s AND (
    i.item_key IS NULL OR i.deleted_at IS NOT NULL OR r.item_key IS NULL OR NOT r.acl_verified
    OR e.acl_principals <> r.acl_principals)
"""


@dataclass
class SnapshotResult:
    snapshot_id: str
    status: str  # published | rejected
    previous: str | None
    counts: dict = field(default_factory=dict)
    checks: dict = field(default_factory=dict)
    reason: str | None = None


def current(conn: psycopg.Connection) -> tuple[str, datetime] | None:
    """The published snapshot and when it was published (None before the first publish)."""
    row = conn.execute(
        "SELECT p.snapshot_id, s.published_at FROM knowledge.snapshot_pointer p "
        "JOIN knowledge.snapshots s USING (snapshot_id)"
    ).fetchone()
    return (row[0], row[1]) if row else None


def _event(conn, event_type: str, subject: str, payload: dict) -> None:
    conn.execute(
        "INSERT INTO knowledge.change_events (event_type, subject, payload) VALUES (%s, %s, %s)",
        (event_type, subject, json.dumps(payload, default=str)),
    )


def build_and_publish(
    conninfo: str,
    model_id: str,
    kinds: tuple[str, ...] = INDEXED_KINDS,
    max_drop: float = MAX_DROP,
    force: bool = False,
) -> SnapshotResult:
    """Build a candidate from current evidence and index; publish only if every check passes."""
    now = datetime.now(UTC)
    snap = f"snap-{now:%Y%m%d-%H%M%S}-{uuid.uuid4().hex[:6]}"
    params = {
        "snap": snap,
        "model": model_id,
        "chunker": CHUNKER_VERSION,
        "redaction": REDACTION_VERSION,
        "kinds": list(kinds),
    }
    with psycopg.connect(conninfo) as conn, conn.transaction():
        conn.execute("SELECT pg_advisory_xact_lock(%s)", (LOCK,))
        previous = current(conn)
        conn.execute(
            "INSERT INTO knowledge.snapshots (snapshot_id, status, parent_snapshot_id, model_id, "
            "chunker_version, redaction_version, created_at) "
            "VALUES (%s, 'candidate', %s, %s, %s, %s, %s)",
            (
                snap,
                previous[0] if previous else None,
                model_id,
                CHUNKER_VERSION,
                REDACTION_VERSION,
                now,
            ),
        )
        conn.execute(MEMBERSHIP_SQL, params)
        served, current_rev, eligible = conn.execute(COUNTS_SQL, params).fetchone()
        prev_count = (
            conn.execute(
                "SELECT count(*) FROM knowledge.snapshot_entries WHERE snapshot_id = %s",
                (previous[0],),
            ).fetchone()[0]
            if previous
            else 0
        )
        counts = {
            "items": served,
            "current": current_rev,
            "outdated": served - current_rev,
            "eligible": eligible,
            "not_indexed": eligible - served,
            "previous_items": prev_count,
        }
        inconsistent = conn.execute(INCONSISTENT_SQL, params).fetchone()[0]
        unreadable = conn.execute(UNREADABLE_SQL, params).fetchone()[0]
        lost = (
            conn.execute(LOST_SQL, {**params, "prev": previous[0]}).fetchone()[0] if previous else 0
        )
        counts["lost"] = lost  # still readable, but missing from the candidate
        dropped_too_much = not force and prev_count > 0 and lost > max_drop * prev_count
        nothing_indexed = eligible > 0 and served == 0
        checks = {
            "consistency": inconsistent == 0,
            "access": unreadable == 0,
            "coverage": not (dropped_too_much or nothing_indexed),
        }
        reasons = []
        if inconsistent:
            reasons.append(f"{inconsistent} item(s) with missing chunks or vectors")
        if unreadable:
            reasons.append(f"{unreadable} item(s) not readable with their stored access list")
        if dropped_too_much:
            reasons.append(
                f"{lost} of {prev_count} served items are still readable but no longer indexed "
                f"(more than {max_drop:.0%})"
            )
        if nothing_indexed:
            reasons.append(f"none of {eligible} readable items is indexed for {model_id}")
        status = "published" if all(checks.values()) else "rejected"
        conn.execute(
            "UPDATE knowledge.snapshots SET status = %s, counts = %s, checks = %s, reason = %s, "
            "published_at = CASE WHEN %s = 'published' THEN now() END WHERE snapshot_id = %s",
            (
                status,
                json.dumps(counts),
                json.dumps(checks),
                "; ".join(reasons) or None,
                status,
                snap,
            ),
        )
        if status == "published":
            if previous:
                conn.execute(
                    "UPDATE knowledge.snapshots SET status = 'superseded' WHERE snapshot_id = %s",
                    (previous[0],),
                )
                for (item,) in conn.execute(  # items no longer served (deleted, re-scoped)
                    "SELECT item_key FROM knowledge.snapshot_entries WHERE snapshot_id = %s "
                    "EXCEPT SELECT item_key FROM knowledge.snapshot_entries WHERE snapshot_id = %s",
                    (previous[0], snap),
                ).fetchall():
                    _event(
                        conn, "index.item_removed", item, {"item_key": item, "snapshot_id": snap}
                    )
            conn.execute(
                "INSERT INTO knowledge.snapshot_pointer (snapshot_id, moved_at) VALUES (%s, now()) "
                "ON CONFLICT (singleton) DO UPDATE SET snapshot_id = EXCLUDED.snapshot_id, "
                "moved_at = now()",
                (snap,),
            )
            _event(conn, "snapshot.published", snap, {"snapshot_id": snap, **counts})
        else:
            _event(conn, "snapshot.rejected", snap, {"snapshot_id": snap, "reasons": reasons})
    result = SnapshotResult(
        snap, status, previous[0] if previous else None, counts, checks, "; ".join(reasons) or None
    )
    log.info(
        "snapshot." + status,
        snapshot_id=snap,
        previous=result.previous,
        **counts,
        reason=result.reason,
    )
    return result


def rollback(conninfo: str, to: str | None = None) -> str:
    """Serve the last good snapshot again (or a named one that still has its membership)."""
    with psycopg.connect(conninfo) as conn, conn.transaction():
        conn.execute("SELECT pg_advisory_xact_lock(%s)", (LOCK,))
        served = current(conn)
        target = (
            to
            or conn.execute(
                "SELECT snapshot_id FROM knowledge.snapshots WHERE status = 'superseded' "
                "AND published_at IS NOT NULL ORDER BY published_at DESC LIMIT 1"
            ).fetchone()
        )
        target = target[0] if isinstance(target, tuple) else target
        if (
            not target
            or not conn.execute(
                "SELECT 1 FROM knowledge.snapshots WHERE snapshot_id = %s AND status <> 'expired' "
                "AND published_at IS NOT NULL",
                (target,),
            ).fetchone()
        ):
            raise ValueError("No earlier published snapshot is available to roll back to")
        if served:
            conn.execute(
                "UPDATE knowledge.snapshots SET status = 'rolled_back' WHERE snapshot_id = %s",
                (served[0],),
            )
        conn.execute(
            "UPDATE knowledge.snapshots SET status = 'published' WHERE snapshot_id = %s", (target,)
        )
        conn.execute(
            "UPDATE knowledge.snapshot_pointer SET snapshot_id = %s, moved_at = now()", (target,)
        )
        _event(
            conn,
            "snapshot.rolled_back",
            target,
            {"snapshot_id": target, "from": served[0] if served else None},
        )
    log.warning("snapshot.rolled_back", to=target, from_snapshot=served[0] if served else None)
    return target


def cleanup(conninfo: str, keep: int = KEEP) -> int:
    """Expire snapshots beyond the newest ``keep`` and delete entries no snapshot uses any more.

    Entries created after the newest snapshot are waiting to be published and are never deleted.
    """
    with psycopg.connect(conninfo) as conn, conn.transaction():
        conn.execute("SELECT pg_advisory_xact_lock(%s)", (LOCK,))
        conn.execute(
            "WITH kept AS (SELECT snapshot_id FROM knowledge.snapshots "
            "  WHERE status IN ('published', 'superseded') ORDER BY published_at DESC LIMIT %s) "
            "UPDATE knowledge.snapshots SET status = 'expired' "
            "WHERE status IN ('superseded', 'rejected', 'rolled_back') "
            "AND snapshot_id NOT IN (SELECT snapshot_id FROM kept) "
            "AND snapshot_id NOT IN (SELECT snapshot_id FROM knowledge.snapshot_pointer)",
            (keep,),
        )
        conn.execute(
            "DELETE FROM knowledge.snapshot_entries se USING knowledge.snapshots s "
            "WHERE s.snapshot_id = se.snapshot_id AND s.status = 'expired'"
        )
        return conn.execute(
            "DELETE FROM knowledge.index_entries e WHERE NOT EXISTS ("
            "  SELECT 1 FROM knowledge.snapshot_entries se WHERE se.entry_key = e.entry_key) "
            "AND e.indexed_at < (SELECT coalesce(max(created_at), '-infinity') "
            "                    FROM knowledge.snapshots)"
        ).rowcount


def refresh(
    conninfo: str,
    read_raw: Callable[[str], bytes],
    embedder: Embedder,
    only: list[str] | None = None,
    force: bool = False,
) -> tuple[indexer.IndexReport, SnapshotResult]:
    """Index what changed, publish a checked snapshot, then clean up (the scheduled job)."""
    report = indexer.run(conninfo, read_raw, embedder, only=only)
    result = build_and_publish(conninfo, embedder.model_id, force=force)
    if result.status == "published":
        cleanup(conninfo)
    return report, result
