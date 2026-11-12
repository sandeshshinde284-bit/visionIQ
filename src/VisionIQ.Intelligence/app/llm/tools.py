"""S08-07 / S08-08: the only way the brief pipeline (or any model-driven step) reads knowledge.

There is a fixed allowlist of read-only tools, each bound to the caller's access scopes when the
toolbox is created. Nothing here can write, comment, merge or reach a raw store, and a tool name
that is not on the list is refused and logged, whatever text asked for it.
"""

from __future__ import annotations

from collections.abc import Callable, Sequence
from dataclasses import dataclass
from typing import Any

import psycopg
import structlog

from app.knowledge import retrieval, snapshots
from app.knowledge.embeddings import Embedder

TOOL_SEARCH = "retrieval.search"
TOOL_FETCH = "evidence.fetch_authorized_source"
READ_ONLY_TOOLS: frozenset[str] = frozenset({TOOL_SEARCH, TOOL_FETCH})

FETCH_SQL = """
SELECT c.section, c.content, c.source_url, c.provider_revision_id, c.kind
FROM knowledge.index_chunks AS c
JOIN knowledge.snapshot_entries AS se
  ON se.entry_key = c.entry_key AND se.snapshot_id = %(snapshot)s
JOIN evidence.source_items AS i ON i.item_key = c.item_key AND i.deleted_at IS NULL
JOIN evidence.source_revisions AS r
  ON r.item_key = i.item_key AND r.provider_revision_id = i.latest_revision_id
 AND r.acl_verified AND r.acl_principals ?| %(scopes)s
WHERE c.item_key = %(key)s AND c.acl_principals ?| %(scopes)s
ORDER BY c.chunk_index
LIMIT %(limit)s
"""


class ToolDeniedError(PermissionError):
    """A tool outside the read-only allowlist was requested."""


@dataclass(frozen=True)
class SourceText:
    """The redacted, indexed text of one source the caller may read."""

    item_key: str
    kind: str
    title: str
    text: str
    url: str | None
    revision: str
    # True when the source has more indexed text than was returned.
    truncated: bool = False


class KnowledgeTools:
    """Read-only knowledge access for one caller (their scopes are fixed at construction)."""

    def __init__(
        self,
        connect: Callable[[], psycopg.Connection],
        embedder: Embedder,
        scopes: Sequence[str],
        live_check: Callable[[str], bool],
    ) -> None:
        self._connect = connect
        self._embedder = embedder
        self._scopes = [scope for scope in scopes if scope]
        self._live_check = live_check

    def search(
        self, query: str, limit: int = 10, exclude: Sequence[str] = ()
    ) -> retrieval.EvidenceBundle:
        """Hybrid retrieval over the published snapshot, filtered and re-checked for access."""
        return retrieval.retrieve(
            self._connect,
            self._embedder,
            query,
            self._scopes,
            self._live_check,
            limit=limit,
            exclude=exclude,
        )

    def fetch_authorized_source(self, item_key: str, max_chunks: int = 12) -> SourceText | None:
        """The indexed text of one source, or None if it is gone or not readable by the caller."""
        if not self._scopes or not self._live_check(item_key):
            return None
        with self._connect() as connection:
            served = snapshots.current(connection)
            if served is None:
                return None
            rows = connection.execute(
                FETCH_SQL,
                {
                    "snapshot": served[0],
                    "scopes": self._scopes,
                    "key": item_key,
                    "limit": max_chunks + 1,
                },
            ).fetchall()
        if not rows:
            return None
        truncated = len(rows) > max_chunks
        rows = rows[:max_chunks]
        first = rows[0][1]
        title = first.split("\n", 1)[0]
        text = "\n\n".join(_body(content) for _, content, _, _, _ in rows)
        return SourceText(item_key, rows[0][4], title, text, rows[0][2], rows[0][3], truncated)

    def dispatch(self, name: str, **arguments: Any) -> Any:
        """Run a tool by name. Anything not on the allowlist is refused (S08-07)."""
        if name not in READ_ONLY_TOOLS:
            structlog.get_logger().warning("tool_denied", tool=name[:80])
            raise ToolDeniedError(f"tool {name!r} is not allowed")
        if name == TOOL_SEARCH:
            return self.search(**arguments)
        return self.fetch_authorized_source(**arguments)


def _body(content: str) -> str:
    """A chunk starts with its document title and section header; keep only its text."""
    return content.split("\n", 2)[-1] if content.count("\n") >= 2 else content
