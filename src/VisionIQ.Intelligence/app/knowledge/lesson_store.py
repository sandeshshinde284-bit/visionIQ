"""LRN-10: reads qualifying evidence and applies a consolidation plan atomically.

Runs as the intelligence database login: it may read all evidence (row-level security
allows reads) and writes only the knowledge schema. One run holds a transaction-scoped
advisory lock, so concurrent runs cannot create duplicate lessons.
"""

from __future__ import annotations

import json
from collections.abc import Sequence
from datetime import datetime

import psycopg

from app.knowledge.lessons import (
    GENERATED_BY,
    Observation,
    Pattern,
    Plan,
    code_paths,
    signature_for,
    title_for,
)

DEFECT_TYPES = ("Bug",)

# Reviewed/disputed/retired and fork-by-scope are both human-initiated (master design 7.4.1, 8.8-8.10:
# the Human Gate, not an algorithm, decides these). They share the automated consolidation job's
# advisory lock so a manual review action and a scheduled consolidation run can never interleave
# writes to the same pattern.
REVIEW_STATUSES = ("reviewed", "disputed", "retired")
CAUSE_STATUSES = ("unverified", "verified")
MIN_PATTERN_MEMBERS = 2  # mirrors lesson_revisions' own CHECK (cardinality(observation_keys) >= 2)

# Explicit, not-disputed fix links of current (not deleted) defect tickets, with the
# files each fix changed.
QUALIFYING_SQL = """
SELECT l.association_key, l.source_instance, l.project_id, l.repository_id, l.ticket_id,
       cs.fix_kind, cs.fix_id, cs.paths, COALESCE(l.revert_commit_shas, '[]'::jsonb)
FROM evidence.ticket_fix_links AS l
JOIN evidence.fix_change_sets AS cs
  ON cs.source_instance = l.source_instance AND cs.project_id = l.project_id
 AND cs.repository_id = l.repository_id
 AND ((l.pull_request_id IS NOT NULL AND cs.fix_kind = 'pull_request'
       AND cs.fix_id = l.pull_request_id)
   OR (l.pull_request_id IS NULL AND cs.fix_kind = 'commit' AND cs.fix_id = lower(l.commit_sha)))
JOIN evidence.source_items AS t
  ON t.provider = 'azure_devops' AND t.source_instance = l.source_instance
 AND t.project_id = l.project_id AND t.kind = 'work_item_current'
 AND t.external_id = l.ticket_id AND t.deleted_at IS NULL
JOIN evidence.source_revisions AS r
  ON r.item_key = t.item_key AND r.provider_revision_id = t.latest_revision_id
WHERE l.source_classification = 'explicit'
  AND (l.review_status IS NULL OR l.review_status <> 'disputed')
  -- Review R4-02: a complete, authoritative later source revision no longer asserting this link
  -- removes it from qualifying evidence, even if it was previously verified (kept in
  -- evidence.ticket_fix_links with its review history for investigation, never erased here).
  AND l.retracted_at IS NULL
  AND l.repository_id IS NOT NULL
  AND r.redacted_payload->>'workItemType' = ANY(%(defect_types)s)
"""

STATE_SQL = """
SELECT p.pattern_id::text, p.source_instance, p.project_id, p.repository_id, p.latest_revision,
       r.status, r.signature_paths,
       COALESCE((SELECT array_agg(m.observation_key::text) FROM knowledge.lesson_memberships AS m
                 WHERE m.pattern_id = p.pattern_id), '{}')
FROM knowledge.lesson_patterns AS p
JOIN knowledge.lesson_revisions AS r
  ON r.pattern_id = p.pattern_id AND r.revision = p.latest_revision
"""

LAST_DECISIONS_SQL = """
SELECT DISTINCT ON (observation_key) observation_key::text, decision, pattern_ids::text[]
FROM knowledge.lesson_decisions ORDER BY observation_key, decision_id DESC
"""

# Recorded observation facts for a pattern's current members, as they were when attributed --
# not a re-query of live ticket_fix_links, so a review/fork action never silently changes what
# membership or signature meant at the time, even if the source ticket has since changed.
PATTERN_MEMBER_OBSERVATIONS_SQL = """
SELECT o.observation_key::text, o.association_key, o.source_instance, o.project_id,
       o.repository_id, o.ticket_id, o.fix_kind, o.fix_id, o.paths
FROM knowledge.lesson_observations AS o
JOIN knowledge.lesson_memberships AS m ON m.observation_key = o.observation_key
WHERE m.pattern_id = %(pattern_id)s
"""

PATTERN_SQL = """
SELECT p.pattern_id::text, p.source_instance, p.project_id, p.repository_id, p.latest_revision,
       r.status, r.signature_paths
FROM knowledge.lesson_patterns AS p
JOIN knowledge.lesson_revisions AS r
  ON r.pattern_id = p.pattern_id AND r.revision = p.latest_revision
WHERE p.pattern_id = %(pattern_id)s
"""


def load_observations(conn: psycopg.Connection, defect_types: Sequence[str] = DEFECT_TYPES):
    rows = conn.execute(QUALIFYING_SQL, {"defect_types": list(defect_types)}).fetchall()
    return [
        Observation(
            association_key=row[0],
            source_instance=row[1],
            project_id=row[2],
            repository_id=row[3],
            ticket_id=row[4],
            fix_kind=row[5],
            fix_id=row[6],
            paths=code_paths(row[7]),
            reverted_by=tuple(sorted(row[8] if isinstance(row[8], list) else json.loads(row[8]))),
        )
        for row in rows
    ]


def load_patterns(conn: psycopg.Connection) -> list[Pattern]:
    return [
        Pattern(
            pattern_id=row[0],
            scope=(row[1], row[2], row[3]),
            latest_revision=row[4],
            status=row[5],
            signature=frozenset(row[6]),
            members=set(row[7]),
        )
        for row in conn.execute(STATE_SQL).fetchall()
    ]


def apply(
    conn: psycopg.Connection, plan: Plan, observations: Sequence[Observation], now: datetime
) -> dict[str, int]:
    """Write the plan (caller owns the transaction); repeated decisions are not re-logged."""
    by_key = {o.key: o for o in observations}
    involved = {d.observation_key for d in plan.decisions} | {k for k, _ in plan.memberships}
    for key in sorted(involved):
        o = by_key[key]
        conn.execute(
            "INSERT INTO knowledge.lesson_observations (observation_key, association_key, "
            "source_instance, project_id, repository_id, ticket_id, fix_kind, fix_id, paths, "
            "recorded_at) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s) "
            "ON CONFLICT (observation_key) DO NOTHING",
            (
                key,
                o.association_key,
                o.source_instance,
                o.project_id,
                o.repository_id,
                o.ticket_id,
                o.fix_kind,
                o.fix_id,
                sorted(o.paths),
                now,
            ),
        )
    for pattern in plan.new_patterns:
        instance, project, repository = pattern.scope
        conn.execute(
            "INSERT INTO knowledge.lesson_patterns (pattern_id, source_instance, project_id, "
            "repository_id, latest_revision, created_at) VALUES (%s, %s, %s, %s, 1, %s)",
            (pattern.pattern_id, instance, project, repository, now),
        )
    for revision in plan.revisions:
        conn.execute(
            "INSERT INTO knowledge.lesson_revisions (pattern_id, revision, status, cause_status, "
            "title, signature_paths, observation_keys, decision, rationale, generated_by, "
            "created_at) VALUES (%s, %s, %s, 'unverified', %s, %s, %s, %s, %s, %s, %s)",
            (
                revision.pattern_id,
                revision.revision,
                revision.status,
                revision.title,
                list(revision.signature),
                list(revision.observation_keys),
                revision.decision,
                revision.rationale,
                GENERATED_BY,
                now,
            ),
        )
        conn.execute(
            "UPDATE knowledge.lesson_patterns SET latest_revision = %s "
            "WHERE pattern_id = %s AND latest_revision < %s",
            (revision.revision, revision.pattern_id, revision.revision),
        )
    created = {p.pattern_id for p in plan.new_patterns}
    for revision in plan.revisions:  # knowledge.changed (S12-15), same transaction
        is_new = revision.pattern_id in created and revision.revision == 1
        event = "lesson.created" if is_new else "lesson.revised"
        conn.execute(
            "INSERT INTO knowledge.change_events (event_type, subject, payload) "
            "VALUES (%s, %s, %s)",
            (
                event,
                revision.pattern_id,
                json.dumps(
                    {
                        "pattern_id": revision.pattern_id,
                        "revision": revision.revision,
                        "status": revision.status,
                        "decision": revision.decision,
                    }
                ),
            ),
        )
    for key, pattern_id in plan.memberships:
        conn.execute(
            "INSERT INTO knowledge.lesson_memberships (observation_key, pattern_id, joined_at) "
            "VALUES (%s, %s, %s) ON CONFLICT (observation_key) DO NOTHING",
            (key, pattern_id, now),
        )
    last = {row[0]: (row[1], tuple(row[2])) for row in conn.execute(LAST_DECISIONS_SQL)}
    logged = 0
    for decision in plan.decisions:
        if last.get(decision.observation_key) == (decision.decision, decision.pattern_ids):
            continue  # same conclusion as last run: nothing new to record
        conn.execute(
            "INSERT INTO knowledge.lesson_decisions (observation_key, decision, pattern_ids, "
            "rationale, decided_at) VALUES (%s, %s, %s, %s, %s)",
            (
                decision.observation_key,
                decision.decision,
                list(decision.pattern_ids),
                decision.rationale,
                now,
            ),
        )
        logged += 1
    return {
        "new_lessons": len(plan.new_patterns),
        "new_revisions": len(plan.revisions),
        "joined": sum(d.decision == "LINK_EXISTING" for d in plan.decisions),
        "human_review": sum(d.decision == "HUMAN_REVIEW" for d in plan.decisions),
        "decisions_logged": logged,
    }


def _load_member_observations(conn: psycopg.Connection, pattern_id: str) -> dict[str, Observation]:
    rows = conn.execute(PATTERN_MEMBER_OBSERVATIONS_SQL, {"pattern_id": pattern_id}).fetchall()
    if not rows:
        raise ValueError(f"Pattern {pattern_id} has no recorded members (unknown pattern_id?).")
    return {
        row[0]: Observation(
            association_key=row[1],
            source_instance=row[2],
            project_id=row[3],
            repository_id=row[4],
            ticket_id=row[5],
            fix_kind=row[6],
            fix_id=row[7],
            paths=code_paths(row[8]),
        )
        for row in rows
    }


def _change_event(conn: psycopg.Connection, event_type: str, pattern_id: str, payload: dict) -> None:
    conn.execute(
        "INSERT INTO knowledge.change_events (event_type, subject, payload) VALUES (%s, %s, %s)",
        (event_type, pattern_id, json.dumps(payload)),
    )


def review(
    conn: psycopg.Connection,
    pattern_id: str,
    status: str,
    verified_by: str,
    now: datetime,
    cause_status: str = "unverified",
    rationale: str | None = None,
) -> int:
    """Record a named human's verdict on a lesson pattern (master design 7.4.1, the Human Gate --
    this is never produced by the automated consolidation job in lessons.py). Caller owns the
    transaction and must hold the same advisory lock lessons_cli.py uses, so this can't interleave
    with a scheduled consolidation run writing the same pattern concurrently.

    Mirrors the database's own CHECK constraints client-side, so a bad call fails with a clear
    Python exception here rather than an opaque constraint-violation from Postgres:
      - status must be reviewed/disputed/retired (candidate is the initial state only, never set here)
      - "reviewed" status or "verified" cause_status both require a named verified_by (never anonymous)
    """
    if status not in REVIEW_STATUSES:
        raise ValueError(f"status must be one of {REVIEW_STATUSES}, got {status!r}")
    if cause_status not in CAUSE_STATUSES:
        raise ValueError(f"cause_status must be one of {CAUSE_STATUSES}, got {cause_status!r}")
    if not verified_by or not verified_by.strip():
        raise ValueError("verified_by is required: a review is always attributed to a named human.")
    if (status == "reviewed" or cause_status == "verified") and not verified_by.strip():
        raise ValueError("Marking a lesson reviewed or its cause verified requires verified_by.")

    row = conn.execute(PATTERN_SQL, {"pattern_id": pattern_id}).fetchone()
    if row is None:
        raise ValueError(f"No pattern found with pattern_id={pattern_id!r}.")
    _, _, _, _, latest_revision, _, signature_paths = row
    next_revision = latest_revision + 1
    members = _load_member_observations(conn, pattern_id)
    title = title_for(frozenset(signature_paths))
    final_rationale = rationale or f"Marked {status} by {verified_by}."

    conn.execute(
        "INSERT INTO knowledge.lesson_revisions (pattern_id, revision, status, cause_status, "
        "title, signature_paths, observation_keys, decision, rationale, generated_by, "
        "verified_by, created_at) VALUES (%s, %s, %s, %s, %s, %s, %s, 'REVISE_EXISTING', %s, "
        "%s, %s, %s)",
        (
            pattern_id,
            next_revision,
            status,
            cause_status,
            title,
            list(signature_paths),
            sorted(members),
            final_rationale[:2000],
            GENERATED_BY,
            verified_by,
            now,
        ),
    )
    conn.execute(
        "UPDATE knowledge.lesson_patterns SET latest_revision = %s WHERE pattern_id = %s",
        (next_revision, pattern_id),
    )
    _change_event(
        conn,
        "lesson.reviewed",
        pattern_id,
        {"pattern_id": pattern_id, "revision": next_revision, "status": status, "verified_by": verified_by},
    )
    return next_revision


def fork(
    conn: psycopg.Connection,
    pattern_id: str,
    observation_keys_to_split_out: Sequence[str],
    verified_by: str,
    now: datetime,
    rationale: str | None = None,
) -> str:
    """Split a subset of a pattern's members into a new, separate pattern (master design's
    "fork by scope" outcome of Human Gate selective learning -- a named engineer's judgment call
    that this pattern actually conflates two distinct root causes, never an algorithmic decision;
    consolidate() in lessons.py has no trigger for this and must not be given one speculatively).

    Both the remaining original pattern and the new pattern must still satisfy the schema's own
    invariant that a lesson needs at least MIN_PATTERN_MEMBERS observations -- a fork that would
    leave either side short of that is rejected before any write happens.
    """
    if not verified_by or not verified_by.strip():
        raise ValueError("verified_by is required: a fork is always attributed to a named human.")
    split_keys = set(observation_keys_to_split_out)
    if not split_keys:
        raise ValueError("observation_keys_to_split_out must be non-empty.")

    row = conn.execute(PATTERN_SQL, {"pattern_id": pattern_id}).fetchone()
    if row is None:
        raise ValueError(f"No pattern found with pattern_id={pattern_id!r}.")
    _, instance, project, repository, latest_revision, _, _ = row
    all_members = _load_member_observations(conn, pattern_id)
    unknown = split_keys - all_members.keys()
    if unknown:
        raise ValueError(f"Not members of {pattern_id}: {sorted(unknown)}")
    remaining_keys = all_members.keys() - split_keys
    if len(remaining_keys) < MIN_PATTERN_MEMBERS:
        raise ValueError(
            f"Fork would leave the original pattern with only {len(remaining_keys)} member(s); "
            f"a lesson needs at least {MIN_PATTERN_MEMBERS}. Retire the whole pattern instead."
        )
    if len(split_keys) < MIN_PATTERN_MEMBERS:
        raise ValueError(
            f"Fork would create a new pattern with only {len(split_keys)} member(s); "
            f"a lesson needs at least {MIN_PATTERN_MEMBERS}."
        )

    new_pattern_id = str(uuid.uuid4())
    new_signature = signature_for([all_members[k] for k in split_keys])
    remaining_signature = signature_for([all_members[k] for k in remaining_keys])
    # lesson_revisions.signature_paths has its own CHECK (cardinality >= 1): a human-chosen split
    # whose members don't actually share a single file would otherwise fail as an opaque database
    # error instead of this clear one.
    if not new_signature:
        raise ValueError(
            "The observations chosen to split out don't share a common changed file with each "
            "other -- a lesson's signature can't be empty. Pick a subset with real file overlap."
        )
    if not remaining_signature:
        raise ValueError(
            "The observations left behind after this split don't share a common changed file "
            "with each other -- a lesson's signature can't be empty. This fork would leave the "
            "original pattern without a valid signature."
        )
    fork_rationale = (rationale or f"Split out by {verified_by}: evidence showed distinct root causes.")[:2000]

    # New pattern: same (source_instance, project_id, repository_id) scope as the original -- the
    # pattern model has no finer scope dimension to split by, so "fork by scope" here means "split
    # this scope's conflated evidence into two lessons", not a move to a different scope.
    conn.execute(
        "INSERT INTO knowledge.lesson_patterns (pattern_id, source_instance, project_id, "
        "repository_id, latest_revision, created_at) VALUES (%s, %s, %s, %s, 1, %s)",
        (new_pattern_id, instance, project, repository, now),
    )
    conn.execute(
        "INSERT INTO knowledge.lesson_revisions (pattern_id, revision, status, cause_status, "
        "title, signature_paths, observation_keys, decision, rationale, generated_by, "
        "verified_by, created_at) VALUES (%s, 1, 'candidate', 'unverified', %s, %s, %s, "
        "'FORK_BY_SCOPE', %s, %s, %s, %s)",
        (
            new_pattern_id,
            title_for(new_signature),
            list(new_signature),
            sorted(split_keys),
            fork_rationale,
            GENERATED_BY,
            verified_by,
            now,
        ),
    )
    conn.execute(
        "UPDATE knowledge.lesson_memberships SET pattern_id = %s WHERE pattern_id = %s AND observation_key = ANY(%s)",
        (new_pattern_id, pattern_id, list(split_keys)),
    )

    # Original pattern keeps its remaining members, resignatured and revised -- its own history
    # (every prior revision) stays exactly as it was; only a new revision is appended.
    next_revision = latest_revision + 1
    conn.execute(
        "INSERT INTO knowledge.lesson_revisions (pattern_id, revision, status, cause_status, "
        "title, signature_paths, observation_keys, decision, rationale, generated_by, "
        "verified_by, created_at) VALUES (%s, %s, 'candidate', 'unverified', %s, %s, %s, "
        "'REVISE_EXISTING', %s, %s, %s, %s)",
        (
            pattern_id,
            next_revision,
            title_for(remaining_signature),
            list(remaining_signature),
            sorted(remaining_keys),
            f"Forked out {len(split_keys)} observation(s) into {new_pattern_id} by {verified_by}."[:2000],
            GENERATED_BY,
            verified_by,
            now,
        ),
    )
    conn.execute(
        "UPDATE knowledge.lesson_patterns SET latest_revision = %s WHERE pattern_id = %s",
        (next_revision, pattern_id),
    )

    _change_event(
        conn, "lesson.forked", pattern_id,
        {"pattern_id": pattern_id, "new_pattern_id": new_pattern_id, "verified_by": verified_by},
    )
    _change_event(
        conn, "lesson.created", new_pattern_id,
        {"pattern_id": new_pattern_id, "forked_from": pattern_id, "verified_by": verified_by},
    )
    return new_pattern_id
