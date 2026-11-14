"""The database reads behind a fix run: the ticket, the brief's scope and the area's past fixes.

Everything goes through the run's access scopes, exactly as the brief does. The pipeline talks to
:class:`FixSources`, so the unit tests run it with in-memory fakes.
"""

from __future__ import annotations

from collections.abc import Callable, Sequence
from dataclasses import dataclass
from typing import Literal, Protocol

import psycopg

from app.brief.history import _FIX_JOIN, BriefSources, TicketText

MAX_HISTORICAL_FIXES = 50


@dataclass(frozen=True)
class HistoricalFix:
    """A past fix in the same files, with every path it changed (its tests among them)."""

    ticket_id: str
    fix_reference: str
    paths: tuple[str, ...]
    # How the ticket-to-fix link itself was established (LRN-04).
    link: Literal["explicit", "inferred"] = "explicit"


class FixSources(Protocol):
    def load_ticket(self, project_id: str, ticket_id: str) -> TicketText | None: ...

    def brief_scope(self, project_id: str, ticket_id: str) -> tuple[str, ...]:
        """The files the ticket's latest brief predicted (BRF-03); empty when there is none."""
        ...

    def brief_risk_band(self, project_id: str, ticket_id: str) -> str | None:
        """The risk band of the ticket's latest brief (LOW, MODERATE, HIGH, INSUFFICIENT_DATA);
        None when there is none. Only read when models are selected by complexity."""
        ...

    def historical_fixes(self, ticket: TicketText, paths: Sequence[str]) -> list[HistoricalFix]:
        """Past fixes that changed one of ``paths`` (full repository paths, not file names)."""
        ...


BRIEF_RISK_BAND_SQL = """
SELECT risk_band
FROM knowledge.briefs
WHERE project_id = %(project)s AND ticket_id = %(ticket)s AND state <> 'BLOCKED'
ORDER BY created_at DESC
LIMIT 1
"""

BRIEF_SCOPE_SQL = """
SELECT document -> 'predicted_scope'
FROM knowledge.briefs
WHERE project_id = %(project)s AND ticket_id = %(ticket)s AND state <> 'BLOCKED'
ORDER BY created_at DESC
LIMIT 1
"""

# Fixes of readable tickets that changed a file in scope and were not reverted: the tests such a
# fix added or changed are the area's historical regression tests (design 4.5.1, tier 2).
# The match is on the full path (case and a leading slash aside), not on the file name: a fix in
# another repository that touched a file called utils.py is not history of this area.
_PATH = "lower(ltrim(replace(p.path, chr(92), '/'), '/'))"
HISTORICAL_FIXES_SQL = f"""
SELECT l.ticket_id, cs.fix_kind, cs.fix_id, l.source_classification, cs.paths[1:200]
{_FIX_JOIN}
  AND t.item_key <> %(self)s
  AND jsonb_array_length(l.revert_commit_shas) = 0
  AND EXISTS (SELECT 1 FROM unnest(cs.paths) AS p(path) WHERE {_PATH} = ANY(%(files)s))
ORDER BY coalesce(t.latest_source_updated_at, l.last_seen_at) DESC, cs.fix_id
LIMIT {MAX_HISTORICAL_FIXES}
"""


class PostgresFixSources:
    """:class:`FixSources` over PostgreSQL, bound to one run's access scopes."""

    def __init__(
        self,
        connect: Callable[[], psycopg.Connection],
        brief_sources: BriefSources,
        scopes: Sequence[str],
    ) -> None:
        self._connect = connect
        self._brief_sources = brief_sources
        self._scopes = [scope for scope in scopes if scope]

    def load_ticket(self, project_id: str, ticket_id: str) -> TicketText | None:
        return self._brief_sources.load_ticket(project_id, ticket_id)

    def brief_scope(self, project_id: str, ticket_id: str) -> tuple[str, ...]:
        with self._connect() as connection:
            row = connection.execute(
                BRIEF_SCOPE_SQL, {"project": project_id, "ticket": ticket_id}
            ).fetchone()
        if row is None or not isinstance(row[0], list):
            return ()
        return tuple(
            str(item["path"]) for item in row[0] if isinstance(item, dict) and "path" in item
        )

    def brief_risk_band(self, project_id: str, ticket_id: str) -> str | None:
        with self._connect() as connection:
            row = connection.execute(
                BRIEF_RISK_BAND_SQL, {"project": project_id, "ticket": ticket_id}
            ).fetchone()
        return str(row[0]) if row is not None and row[0] else None

    def historical_fixes(self, ticket: TicketText, paths: Sequence[str]) -> list[HistoricalFix]:
        names = sorted({path.replace("\\", "/").lstrip("/").lower() for path in paths} - {""})
        if not names or not self._scopes:
            return []
        with self._connect() as connection:
            rows = connection.execute(
                HISTORICAL_FIXES_SQL,
                {"scopes": self._scopes, "files": names, "self": ticket.item_key},
            ).fetchall()
        return [
            HistoricalFix(
                ticket_id,
                f"PR {fix_id}" if kind == "pull_request" else f"commit {fix_id[:12]}",
                tuple(paths),
                "inferred" if classification == "inferred" else "explicit",
            )
            for ticket_id, kind, fix_id, classification, paths in rows
        ]
