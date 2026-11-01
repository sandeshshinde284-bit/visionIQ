"""Show how a lesson changed over time (LRN-13 demo, S12-16).

    python -m app.knowledge.lesson_history              # list lessons with their current status
    python -m app.knowledge.lesson_history <pattern-id> # full timeline of one lesson

Read-only; uses the API database login (DB_API_*). Revisions are append-only, so the timeline shows
exactly how evidence moved the lesson, e.g. supported (candidate) -> disputed after a revert.
"""

from __future__ import annotations

import json
import os
import sys

import psycopg
from psycopg.conninfo import make_conninfo

LIST_SQL = """
SELECT p.pattern_id::text, p.latest_revision, r.status, r.title, r.created_at
FROM knowledge.lesson_patterns p
JOIN knowledge.lesson_revisions r ON r.pattern_id = p.pattern_id AND r.revision = p.latest_revision
ORDER BY r.created_at DESC
"""

REVISIONS_SQL = """
SELECT revision, status, cause_status, decision, title, signature_paths, rationale,
       cardinality(observation_keys), verified_by, created_at
FROM knowledge.lesson_revisions WHERE pattern_id = %s ORDER BY revision
"""

DECISIONS_SQL = """
SELECT d.decided_at, d.decision, o.ticket_id, o.fix_kind, o.fix_id, d.rationale
FROM knowledge.lesson_decisions d
JOIN knowledge.lesson_observations o USING (observation_key)
WHERE %s = ANY(d.pattern_ids)
ORDER BY d.decided_at, d.decision_id
"""


def timeline(conn: psycopg.Connection, pattern_id: str) -> dict:
    revisions = [
        {
            "revision": r[0],
            "status": r[1],
            "cause": r[2],
            "decision": r[3],
            "title": r[4],
            "signature": r[5],
            "rationale": r[6],
            "supporting_tickets": r[7],
            "verified_by": r[8],
            "at": r[9].isoformat(),
        }
        for r in conn.execute(REVISIONS_SQL, (pattern_id,))
    ]
    decisions = [
        {
            "at": d[0].isoformat(),
            "decision": d[1],
            "ticket": d[2],
            "fix": f"{d[3]} {d[4]}",
            "rationale": d[5],
        }
        for d in conn.execute(DECISIONS_SQL, (pattern_id,))
    ]
    return {"pattern_id": pattern_id, "revisions": revisions, "decisions": decisions}


def _conninfo() -> str:
    return make_conninfo(
        "",
        host=os.environ["DB_HOST"],
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ["DB_NAME"],
        user=os.environ["DB_API_USER"],
        password=os.environ["DB_API_PASSWORD"],
        sslmode=os.environ.get("DB_SSL_MODE", "prefer"),
    )


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    with psycopg.connect(_conninfo()) as conn:
        if not args:
            for pattern_id, revision, status, title, at in conn.execute(LIST_SQL):
                print(f"{pattern_id}  rev {revision}  {status:<9}  {title}  ({at:%Y-%m-%d})")
            return 0
        result = timeline(conn, args[0])
    if not result["revisions"]:
        print(f"No lesson {args[0]}", file=sys.stderr)
        return 1
    if "--json" in args:
        print(json.dumps(result, indent=2))
        return 0
    for r in result["revisions"]:
        print(
            f"rev {r['revision']}  {r['status']:<9} cause {r['cause']:<10} {r['decision']:<16} "
            f"{r['supporting_tickets']} tickets  {r['at']}"
        )
        print(f"        {r['rationale']}")
    for d in result["decisions"]:
        print(f"  {d['at']}  {d['decision']:<16} ticket {d['ticket']} ({d['fix']})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
