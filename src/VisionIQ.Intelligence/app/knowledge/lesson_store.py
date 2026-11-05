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

from app.knowledge.lessons import GENERATED_BY, Observation, Pattern, Plan, code_paths

DEFECT_TYPES = ("Bug",)

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
