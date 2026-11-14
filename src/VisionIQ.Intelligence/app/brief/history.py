"""The database reads behind a brief: the ticket, its history and the counts used for risk.

Everything goes through the caller's access scopes, exactly as retrieval does: a fix is counted
only if the ticket it fixed is readable now. The pipeline talks to :class:`BriefSources`, so the
unit tests run it with in-memory fakes and only the opt-in database test needs PostgreSQL.
"""

from __future__ import annotations

from collections.abc import Callable, Sequence
from dataclasses import dataclass
from typing import Protocol

import psycopg

from app.brief.guidance import Dependents
from app.knowledge.embeddings import Embedder
from app.knowledge.retrieval import _READABLE, EvidenceBundle
from app.llm.tools import KnowledgeTools

MAX_TICKET_CHUNKS = 12


@dataclass(frozen=True)
class TicketText:
    item_key: str
    source_instance: str
    title: str
    text: str
    truncated: bool = False
    project_id: str = ""


@dataclass(frozen=True)
class FixRecord:
    """A past fix that changed at least one of the files in scope."""

    ticket_id: str
    ticket_item_key: str
    fix_kind: str
    fix_id: str
    reverted: bool
    paths: tuple[str, ...]

    @property
    def reference(self) -> str:
        fix = (
            f"PR {self.fix_id}" if self.fix_kind == "pull_request" else f"commit {self.fix_id[:12]}"
        )
        return f"ticket {self.ticket_id} -> {fix}" + (" (reverted)" if self.reverted else "")


@dataclass(frozen=True)
class HotspotHit:
    area: str
    band: str
    lift: float


@dataclass(frozen=True)
class HistoryFacts:
    """What the history says about the files in scope. ``None`` always means "not known"."""

    fixes: tuple[FixRecord, ...] = ()
    # Changes of any kind recorded for the files in the window; None when nothing is recorded.
    activity: int | None = None
    # Flagged areas of the latest hotspot sweep; None when no sweep has run yet.
    hotspots: tuple[HotspotHit, ...] | None = None
    # Fixes to the files in scope whose ticket this run may not read. They are counted, never
    # shown, so that hidden history is not mistaken for no history.
    unreadable_fixes: int = 0


class BriefSources(Protocol):
    def load_ticket(self, project_id: str, ticket_id: str) -> TicketText | None: ...

    def search(self, query: str, exclude: Sequence[str], limit: int) -> EvidenceBundle: ...

    def changed_files(self, tickets: Sequence[str]) -> dict[str, tuple[str, ...]]:
        """Files changed by the fixes of the given tickets (item keys), per ticket."""
        ...

    def dependents(self, repository_id: str, paths: Sequence[str]) -> list[Dependents]:
        """ENH-01: recorded references to the files in ``paths`` (or the projects they belong
        to), and other repositories that declare a dependency on ``repository_id``. Only read
        for an enhancement."""
        ...

    def history(
        self, ticket: TicketText, files: Sequence[str], window_days: int
    ) -> HistoryFacts: ...


# The run names its project by the pickup policy's project id, which is an alias when the ADO
# project name has spaces ("Genoa Kiosk" -> "genoa-kiosk"), while evidence stores the ADO name.
# So the ticket is found by its work-item number (unique in an ADO organization) among the items
# the run's scope may read; the project is only a tie-break, and the stored name is returned.
TICKET_SQL = """
SELECT i.item_key, i.source_instance, i.project_id
FROM evidence.source_items AS i
JOIN evidence.source_revisions AS r
  ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
 AND r.acl_verified AND r.acl_principals ?| %(scopes)s
WHERE i.provider = 'azure_devops' AND i.kind = 'work_item_current'
  AND i.external_id = %(ticket)s AND i.deleted_at IS NULL
ORDER BY (i.project_id = %(project)s) DESC, i.last_observed_at DESC
LIMIT 1
"""

_FIX_JOIN = f"""
FROM evidence.ticket_fix_links AS l
JOIN evidence.fix_change_sets AS cs
  ON cs.source_instance = l.source_instance AND cs.project_id = l.project_id
 AND cs.repository_id = l.repository_id
 AND ((l.pull_request_id IS NOT NULL AND cs.fix_kind = 'pull_request'
       AND cs.fix_id = l.pull_request_id)
   OR (l.pull_request_id IS NULL AND cs.fix_kind = 'commit' AND cs.fix_id = lower(l.commit_sha)))
{_READABLE}
WHERE l.source_classification IN ('explicit', 'inferred')
  AND (l.review_status IS NULL OR l.review_status <> 'disputed')
  AND l.retracted_at IS NULL
"""

_NAME = "lower(regexp_replace(p.path, '^.*/', ''))"

# Fixes that changed a file in scope, inside the window (by the fixed ticket's last update).
FIXES_SQL = f"""
SELECT l.ticket_id, t.item_key, cs.fix_kind, cs.fix_id,
       jsonb_array_length(l.revert_commit_shas) > 0 AS reverted,
       ARRAY(SELECT DISTINCT p.path FROM unnest(cs.paths) AS p(path)
             WHERE {_NAME} = ANY(%(files)s) ORDER BY 1 LIMIT 20) AS matched
{_FIX_JOIN}
  AND t.item_key <> %(self)s
  AND coalesce(t.latest_source_updated_at, l.last_seen_at)
      >= now() - make_interval(days => %(window)s)
  AND EXISTS (SELECT 1 FROM unnest(cs.paths) AS p(path) WHERE {_NAME} = ANY(%(files)s))
ORDER BY l.ticket_id, cs.fix_id
LIMIT 200
"""

# Every recorded change (fix or not) to a file in scope: the activity the fixes are judged against.
ACTIVITY_SQL = f"""
SELECT count(*)
FROM evidence.fix_change_sets AS cs
LEFT JOIN evidence.source_items AS si ON si.item_key = cs.provenance_item_key
WHERE cs.source_instance = %(instance)s
  AND coalesce(si.latest_source_updated_at, cs.observed_at)
      >= now() - make_interval(days => %(window)s)
  AND EXISTS (SELECT 1 FROM unnest(cs.paths) AS p(path) WHERE {_NAME} = ANY(%(files)s))
"""

# The same fixes without the access filter, as a bare count: how much history exists at all.
ALL_FIXES_SQL = f"""
SELECT count(DISTINCT (cs.fix_kind, cs.fix_id))
FROM evidence.ticket_fix_links AS l
JOIN evidence.fix_change_sets AS cs
  ON cs.source_instance = l.source_instance AND cs.project_id = l.project_id
 AND cs.repository_id = l.repository_id
 AND ((l.pull_request_id IS NOT NULL AND cs.fix_kind = 'pull_request'
       AND cs.fix_id = l.pull_request_id)
   OR (l.pull_request_id IS NULL AND cs.fix_kind = 'commit' AND cs.fix_id = lower(l.commit_sha)))
WHERE l.source_instance = %(instance)s
  AND l.source_classification IN ('explicit', 'inferred')
  AND (l.review_status IS NULL OR l.review_status <> 'disputed')
  AND l.retracted_at IS NULL
  AND l.last_seen_at >= now() - make_interval(days => %(window)s)
  AND EXISTS (SELECT 1 FROM unnest(cs.paths) AS p(path) WHERE {_NAME} = ANY(%(files)s))
"""

FIX_PATHS_SQL = f"""
SELECT t.item_key, cs.paths[1:40]
{_FIX_JOIN}
  AND t.item_key = ANY(%(keys)s)
"""

HOTSPOTS_SQL = """
SELECT a.area, a.band, a.lift
FROM knowledge.hotspot_areas AS a
WHERE a.sweep_id = %(sweep)s AND a.source_instance = %(instance)s
  AND a.project_id = %(project)s AND a.band IN ('HOT', 'WATCH')
ORDER BY a.rank
"""

LATEST_SWEEP_SQL = (
    "SELECT sweep_id FROM knowledge.hotspot_sweeps ORDER BY completed_at DESC LIMIT 1"
)


# ENH-01 / PROP-02: who refers to a file, from the dependency graph (LRN-07). A Python import is
# recorded between the two files; a .NET project reference between the two project files, so a
# source file is matched through the project file whose folder contains it. Only the latest scan
# of a repository counts, and only evidence the caller may read.
_SCAN_READABLE = """
JOIN evidence.source_items AS i
  ON i.item_key = a.provenance_item_key AND i.deleted_at IS NULL
 AND i.latest_revision_id = a.provenance_revision_id
JOIN evidence.source_revisions AS r
  ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
 AND r.acl_verified AND r.acl_principals ?| %(scopes)s
"""

FILE_DEPENDENTS_SQL = f"""
WITH wanted AS (
    SELECT DISTINCT ltrim(path, '/') AS path FROM unnest(%(paths)s::text[]) AS path
)
SELECT DISTINCT w.path, ltrim(target.native_id, '/') AS target,
       ltrim(source.native_id, '/') AS dependent, i.repository_id, i.item_key, r.source_url,
       r.provider_revision_id
FROM wanted AS w
JOIN evidence.graph_nodes AS target
  ON target.node_kind = 'file'
 AND (ltrim(target.native_id, '/') = w.path
      OR (target.native_id ~* '[.][a-z]{{2}}proj$'
          AND starts_with(w.path, regexp_replace(ltrim(target.native_id, '/'), '[^/]+$', ''))))
JOIN evidence.graph_assertions AS a
  ON a.to_node_key = target.node_key AND a.relation_type = 'file_dependency'
 AND a.assertion_state = 'active'
JOIN evidence.graph_nodes AS source
  ON source.node_key = a.from_node_key AND source.node_kind = 'file'
{_SCAN_READABLE}
ORDER BY 1, 2, 3
LIMIT 500
"""

REPOSITORY_DEPENDENTS_SQL = f"""
SELECT DISTINCT i.repository_id, ltrim(source.native_id, '/') AS declared_in, i.item_key,
       r.source_url, r.provider_revision_id
FROM evidence.graph_nodes AS target
JOIN evidence.graph_assertions AS a
  ON a.to_node_key = target.node_key AND a.relation_type = 'repository_dependency'
 AND a.assertion_state = 'active'
JOIN evidence.graph_nodes AS source ON source.node_key = a.from_node_key
{_SCAN_READABLE}
WHERE target.node_kind = 'repository' AND target.native_id = %(repository)s
  AND i.repository_id <> %(repository)s
ORDER BY 1, 2
LIMIT 100
"""


def _folder(path: str) -> str:
    return path.rsplit("/", 1)[0] + "/" if "/" in path else ""


def file_dependents(rows: Sequence[tuple], repository_id: str) -> list[Dependents]:
    """Group FILE_DEPENDENTS_SQL rows by the file that is referred to.

    Rows of the ticket's own repository are preferred when there are any (two repositories can
    hold the same path). A source file matched through several nested project files belongs to
    the innermost one.
    """
    own = [row for row in rows if row[3] == repository_id]
    rows = own or list(rows)
    innermost: dict[str, str] = {}
    for path, target, *_ in rows:
        best = innermost.get(path)
        if (
            target == path
            or best is None
            or (best != path and len(_folder(target)) > len(_folder(best)))
        ):
            innermost[path] = target
    grouped: dict[str, dict] = {}
    for path, target, dependent, _repository, item_key, url, revision in rows:
        if innermost[path] != target or dependent == target:
            continue
        entry = grouped.setdefault(
            target, {"dependents": [], "item_key": item_key, "url": url, "revision": revision}
        )
        if dependent not in entry["dependents"]:
            entry["dependents"].append(dependent)
    return [
        Dependents(
            target=target,
            dependents=tuple(entry["dependents"]),
            item_key=str(entry["item_key"]).strip(),
            url=entry["url"],
            revision=entry["revision"],
        )
        for target, entry in grouped.items()
    ]


def repository_dependents(rows: Sequence[tuple], repository_id: str) -> list[Dependents]:
    """REPOSITORY_DEPENDENTS_SQL rows as one record: files elsewhere that declare the dependency."""
    if not rows:
        return []
    names = []
    for repository, declared_in, *_ in rows:
        name = f"{repository}: {declared_in}"
        if name not in names:
            names.append(name)
    _, _, item_key, url, revision = rows[0]
    return [
        Dependents(
            target=repository_id,
            dependents=tuple(names),
            item_key=str(item_key).strip(),
            url=url,
            revision=revision,
            cross_repository=True,
        )
    ]


class PostgresBriefSources:
    """:class:`BriefSources` over PostgreSQL, bound to one run's access scopes."""

    def __init__(
        self,
        connect: Callable[[], psycopg.Connection],
        embedder: Embedder,
        scopes: Sequence[str],
        live_check: Callable[[str], bool],
    ) -> None:
        self._connect = connect
        self._scopes = [scope for scope in scopes if scope]
        self._tools = KnowledgeTools(connect, embedder, self._scopes, live_check)

    def load_ticket(self, project_id: str, ticket_id: str) -> TicketText | None:
        if not self._scopes:
            return None
        with self._connect() as connection:
            row = connection.execute(
                TICKET_SQL, {"scopes": self._scopes, "project": project_id, "ticket": ticket_id}
            ).fetchone()
        if row is None:
            return None
        source = self._tools.fetch_authorized_source(row[0], max_chunks=MAX_TICKET_CHUNKS)
        if source is None:
            return None  # readable evidence, but not in the published knowledge snapshot yet
        return TicketText(row[0], row[1], source.title, source.text, source.truncated, row[2])

    def search(self, query: str, exclude: Sequence[str], limit: int) -> EvidenceBundle:
        return self._tools.search(query, limit=limit, exclude=exclude)

    def changed_files(self, tickets: Sequence[str]) -> dict[str, tuple[str, ...]]:
        fix_paths: dict[str, tuple[str, ...]] = {}
        if not tickets or not self._scopes:
            return fix_paths
        with self._connect() as connection:
            for key, paths in connection.execute(
                FIX_PATHS_SQL, {"scopes": self._scopes, "keys": list(tickets)}
            ):
                fix_paths[key] = tuple(sorted({*fix_paths.get(key, ()), *paths}))
        return fix_paths

    def dependents(self, repository_id: str, paths: Sequence[str]) -> list[Dependents]:
        if not self._scopes:
            return []
        found: list[Dependents] = []
        with self._connect() as connection:
            if paths:
                rows = connection.execute(
                    FILE_DEPENDENTS_SQL, {"scopes": self._scopes, "paths": list(paths)}
                ).fetchall()
                found.extend(file_dependents(rows, repository_id))
            if repository_id:
                rows = connection.execute(
                    REPOSITORY_DEPENDENTS_SQL,
                    {"scopes": self._scopes, "repository": repository_id},
                ).fetchall()
                found.extend(repository_dependents(rows, repository_id))
        return found

    def history(self, ticket: TicketText, files: Sequence[str], window_days: int) -> HistoryFacts:
        names = sorted({name.lower() for name in files})
        with self._connect() as connection:
            sweep = connection.execute(LATEST_SWEEP_SQL).fetchone()
            hotspots = (
                None
                if sweep is None
                else tuple(
                    HotspotHit(area, band, float(lift))
                    for area, band, lift in connection.execute(
                        HOTSPOTS_SQL,
                        {
                            "sweep": sweep[0],
                            "instance": ticket.source_instance,
                            "project": ticket.project_id,
                        },
                    )
                )
            )
            if not names:
                return HistoryFacts(hotspots=hotspots)
            parameters = {
                "scopes": self._scopes,
                "files": names,
                "window": window_days,
                "self": ticket.item_key,
                "instance": ticket.source_instance,
            }
            fixes = tuple(
                FixRecord(ticket_id, item_key, kind, fix_id, bool(reverted), tuple(matched))
                for ticket_id, item_key, kind, fix_id, reverted, matched in connection.execute(
                    FIXES_SQL, parameters
                )
            )
            activity = connection.execute(ACTIVITY_SQL, parameters).fetchone()[0]
            all_fixes = connection.execute(ALL_FIXES_SQL, parameters).fetchone()[0]
        readable = len({(fix.fix_kind, fix.fix_id) for fix in fixes})
        return HistoryFacts(
            fixes, int(activity) or None, hotspots, max(0, int(all_fixes) - readable)
        )
