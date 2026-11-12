"""S10-12: the hotspot sweep. Reads recorded changes and defect fixes, scores every area and
stores the result with the evidence behind it.

A sweep is one transaction and takes an advisory lock, so a scheduled sweep and an on-demand one
(or two replicas) never run at the same time. Each sweep keeps its own rows: a report can be
reproduced later and compared with the next one. Briefs read the latest sweep (BRF-03).
"""

from __future__ import annotations

import uuid
from collections.abc import Sequence
from dataclasses import dataclass
from datetime import UTC, datetime
from typing import Literal

import psycopg
import structlog
from psycopg.types.json import Jsonb

from app.hotspots.scoring import ROOT_AREA, SCORING_VERSION, AreaActivity, ScoredArea, score

Trigger = Literal["schedule", "on_demand"]
# Work-item types that count as incidents. A ticket whose type is not recorded also counts:
# leaving it out would hide real defects whenever metadata is missing.
DEFAULT_DEFECT_TYPES = ("Bug", "Defect", "Incident", "Issue")
MAX_EVIDENCE_PER_AREA = 50
_LOCK_KEY = 0x56495148  # "VIQH": one hotspot sweep at a time

# Directory of a path, cut to the first %(depth)s segments; files at the top level are "(root)".
_AREA = f"""
CASE WHEN position('/' in trim(both '/' from p.path)) = 0 THEN '{ROOT_AREA}'
     ELSE array_to_string(
         (string_to_array(
             regexp_replace(trim(both '/' from p.path), '/[^/]*$', ''), '/'))[1:%(depth)s], '/')
END
"""

ACTIVITY_SQL = f"""
WITH changes AS (
    SELECT cs.source_instance, cs.project_id, cs.repository_id, cs.fix_kind, cs.fix_id, a.area
    FROM evidence.fix_change_sets AS cs
    LEFT JOIN evidence.source_items AS si ON si.item_key = cs.provenance_item_key
    CROSS JOIN LATERAL (
        SELECT DISTINCT {_AREA} AS area FROM unnest(cs.paths) AS p(path)
    ) AS a
    WHERE coalesce(si.latest_source_updated_at, cs.observed_at)
          >= now() - make_interval(days => %(window)s)
),
fixes AS (
    SELECT l.source_instance, l.project_id, l.repository_id, l.ticket_id,
           CASE WHEN l.pull_request_id IS NOT NULL THEN 'pull_request' ELSE 'commit' END
               AS fix_kind,
           coalesce(l.pull_request_id, lower(l.commit_sha)) AS fix_id,
           jsonb_array_length(l.revert_commit_shas) > 0 AS reverted
    FROM evidence.ticket_fix_links AS l
    LEFT JOIN evidence.source_items AS t
      ON t.provider = 'azure_devops' AND t.source_instance = l.source_instance
     AND t.project_id = l.project_id AND t.kind = 'work_item_current'
     AND t.external_id = l.ticket_id
    LEFT JOIN evidence.source_revisions AS r
      ON r.item_key = t.item_key AND r.provider_revision_id = t.latest_revision_id
    WHERE l.source_classification IN ('explicit', 'inferred')
      AND (l.review_status IS NULL OR l.review_status <> 'disputed')
      AND l.retracted_at IS NULL
      AND (r.redacted_payload ->> 'workItemType' IS NULL
           OR lower(r.redacted_payload ->> 'workItemType') = ANY(%(types)s))
)
SELECT c.source_instance, c.project_id, c.repository_id, c.area,
       count(DISTINCT (c.fix_kind, c.fix_id)) AS changes,
       count(DISTINCT (c.fix_kind, c.fix_id)) FILTER (WHERE f.ticket_id IS NOT NULL)
           AS defect_fixes,
       count(DISTINCT (c.fix_kind, c.fix_id)) FILTER (WHERE f.reverted) AS reverted_fixes,
       coalesce(
           jsonb_agg(DISTINCT jsonb_build_object(
               'ticket_id', f.ticket_id, 'fix_kind', c.fix_kind, 'fix_id', c.fix_id,
               'reverted', f.reverted)) FILTER (WHERE f.ticket_id IS NOT NULL),
           '[]'::jsonb) AS evidence
FROM changes AS c
LEFT JOIN fixes AS f
  ON f.source_instance = c.source_instance AND f.project_id = c.project_id
 AND f.repository_id = c.repository_id AND f.fix_kind = c.fix_kind AND f.fix_id = c.fix_id
GROUP BY c.source_instance, c.project_id, c.repository_id, c.area
"""


@dataclass(frozen=True)
class SweepResult:
    sweep_id: uuid.UUID
    trigger: Trigger
    window_days: int
    area_depth: int
    started_at: datetime
    completed_at: datetime
    areas: tuple[ScoredArea, ...]

    @property
    def flagged(self) -> tuple[ScoredArea, ...]:
        return tuple(area for area in self.areas if area.band in ("HOT", "WATCH"))


def read_activity(
    connection: psycopg.Connection,
    window_days: int,
    area_depth: int,
    defect_types: Sequence[str] = DEFAULT_DEFECT_TYPES,
) -> list[AreaActivity]:
    rows = connection.execute(
        ACTIVITY_SQL,
        {
            "window": window_days,
            "depth": area_depth,
            "types": [name.lower() for name in defect_types],
        },
    ).fetchall()
    return [
        AreaActivity(
            instance,
            project,
            repository,
            area,
            int(changes),
            int(fixes),
            int(reverted),
            tuple(evidence[:MAX_EVIDENCE_PER_AREA]),
        )
        for instance, project, repository, area, changes, fixes, reverted, evidence in rows
    ]


def run_sweep(
    connection: psycopg.Connection,
    trigger: Trigger,
    window_days: int,
    area_depth: int,
    defect_types: Sequence[str] = DEFAULT_DEFECT_TYPES,
) -> SweepResult | None:
    """Score all areas and store the sweep. None if another sweep is running right now."""
    log = structlog.get_logger()
    started = datetime.now(UTC)
    with connection.transaction():
        locked = connection.execute("SELECT pg_try_advisory_xact_lock(%s)", (_LOCK_KEY,)).fetchone()
        if not locked or not locked[0]:
            log.info("hotspot_sweep_skipped", reason="already_running")
            return None
        areas = score(read_activity(connection, window_days, area_depth, defect_types))
        result = SweepResult(
            uuid.uuid4(), trigger, window_days, area_depth, started, datetime.now(UTC), tuple(areas)
        )
        connection.execute(
            "INSERT INTO knowledge.hotspot_sweeps (sweep_id, trigger, scoring_version, "
            "window_days, area_depth, areas_scored, areas_flagged, started_at, completed_at) "
            "VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s)",
            (
                result.sweep_id,
                trigger,
                SCORING_VERSION,
                window_days,
                area_depth,
                len(areas),
                len(result.flagged),
                result.started_at,
                result.completed_at,
            ),
        )
        for scored in areas:
            activity = scored.activity
            connection.execute(
                "INSERT INTO knowledge.hotspot_areas (sweep_id, source_instance, project_id, "
                "repository_id, area, changes, defect_fixes, reverted_fixes, lift, band, rank, "
                "evidence) VALUES (%s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s, %s)",
                (
                    result.sweep_id,
                    activity.source_instance,
                    activity.project_id,
                    activity.repository_id,
                    activity.area,
                    activity.changes,
                    activity.defect_fixes,
                    activity.reverted_fixes,
                    scored.lift,
                    scored.band,
                    scored.rank,
                    Jsonb(list(activity.evidence)),
                ),
            )
    log.info(
        "hotspot_sweep_completed",
        sweep_id=str(result.sweep_id),
        trigger=trigger,
        window_days=window_days,
        areas_scored=len(areas),
        areas_flagged=len(result.flagged),
    )
    return result


def report(result: SweepResult, limit: int = 20) -> dict:
    """The hotspot report: flagged areas first, each with the counts and fixes behind it."""
    return {
        "sweep_id": str(result.sweep_id),
        "trigger": result.trigger,
        "scoring_version": SCORING_VERSION,
        "window_days": result.window_days,
        "area_depth": result.area_depth,
        "completed_at": result.completed_at.isoformat(),
        "areas_scored": len(result.areas),
        "areas_flagged": len(result.flagged),
        "areas": [
            {
                "rank": scored.rank,
                "band": scored.band,
                "lift": scored.lift,
                "project": scored.activity.project_id,
                "repository": scored.activity.repository_id,
                "area": scored.activity.area,
                "changes": scored.activity.changes,
                "defect_fixes": scored.activity.defect_fixes,
                "reverted_fixes": scored.activity.reverted_fixes,
                "evidence": list(scored.activity.evidence),
            }
            for scored in result.areas[:limit]
        ],
    }
