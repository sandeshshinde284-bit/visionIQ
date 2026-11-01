"""Run lesson consolidation once: ``python -m app.knowledge.lessons_cli`` (scheduled job)."""

from __future__ import annotations

import os
import sys
import uuid
from datetime import UTC, datetime

import psycopg
import structlog
from psycopg.conninfo import make_conninfo

from app.knowledge import lesson_store
from app.knowledge.lessons import consolidate
from app.logging_config import configure_from_environment

LOCK = 0x5649514C  # "VIQL": one consolidation at a time


def _env(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise SystemExit(f"{name} is required")
    return value


def run(conninfo: str, defect_types: tuple[str, ...] = lesson_store.DEFECT_TYPES) -> dict[str, int]:
    """One atomic consolidation over all qualifying evidence."""
    now = datetime.now(UTC)
    with psycopg.connect(conninfo) as conn, conn.transaction():
        conn.execute("SELECT pg_advisory_xact_lock(%s)", (LOCK,))
        observations = lesson_store.load_observations(conn, defect_types)
        plan = consolidate(observations, lesson_store.load_patterns(conn), now)
        return {"observations": len(observations)} | lesson_store.apply(
            conn, plan, observations, now
        )


def main() -> int:
    configure_from_environment()
    structlog.contextvars.bind_contextvars(run_id=uuid.uuid4().hex)
    log = structlog.get_logger("visioniq.lessons")
    conninfo = make_conninfo(
        "",
        host=_env("DB_HOST"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=_env("DB_NAME"),
        user=_env("DB_INTELLIGENCE_USER"),
        password=_env("DB_INTELLIGENCE_PASSWORD"),
        sslmode=os.environ.get("DB_SSL_MODE", "prefer"),
    )
    types = tuple(
        t.strip() for t in os.environ.get("LESSON_DEFECT_TYPES", "Bug").split(",") if t.strip()
    )
    log.info("lessons.started", defect_types=list(types))
    try:
        result = run(conninfo, types)
    except Exception as error:
        log.error("lessons.failed", error_type=type(error).__name__)
        raise
    log.info("lessons.finished", **result)
    return 0


if __name__ == "__main__":
    sys.exit(main())
