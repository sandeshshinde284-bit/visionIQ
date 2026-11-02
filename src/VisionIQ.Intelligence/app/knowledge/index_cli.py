"""Keep the search index current and published (scheduled job, S07-03, LRN-15).

python -m app.knowledge.index_cli                 # index changes, publish a checked snapshot
python -m app.knowledge.index_cli status          # the served snapshot and recent attempts
python -m app.knowledge.index_cli rollback [id]   # serve the last good (or a named) snapshot
"""

from __future__ import annotations

import os
import sys
import uuid

import structlog
from psycopg.conninfo import make_conninfo

from app.knowledge import snapshots
from app.knowledge.embeddings import from_environment
from app.logging_config import configure_from_environment


def read_gcs(uri: str) -> bytes:
    """Read an archived raw evidence object (gs://bucket/path) with the job's own credentials."""
    from google.cloud import storage

    if not uri.startswith("gs://"):
        raise ValueError("Raw evidence must be archived in Cloud Storage")
    bucket, _, name = uri[5:].partition("/")
    return storage.Client().bucket(bucket).blob(name).download_as_bytes()


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


def main(argv: list[str] | None = None) -> int:
    args = sys.argv[1:] if argv is None else argv
    configure_from_environment()
    structlog.contextvars.bind_contextvars(run_id=uuid.uuid4().hex)
    log = structlog.get_logger("visioniq.indexer")
    conninfo = _conninfo()
    if args[:1] == ["status"]:
        import psycopg

        with psycopg.connect(conninfo) as conn:
            served = snapshots.current(conn)
            print(
                f"served: {served[0]} (published {served[1]:%Y-%m-%d %H:%M})"
                if served
                else "served: none"
            )
            for row in conn.execute(
                "SELECT snapshot_id, status, coalesce(reason, '') FROM knowledge.snapshots "
                "ORDER BY created_at DESC LIMIT 10"
            ):
                print(f"  {row[0]}  {row[1]:<11} {row[2]}")
        return 0
    if args[:1] == ["rollback"]:
        target = snapshots.rollback(conninfo, args[1] if len(args) > 1 else None)
        print(f"now serving {target}")
        return 0
    embedder = from_environment()
    log.info("index.started", model=embedder.model_id)
    try:
        report, published = snapshots.refresh(conninfo, read_gcs, embedder)
    except Exception as error:
        log.error("index.failed", error_type=type(error).__name__)
        raise
    log.info(
        "index.finished",
        indexed=report.indexed,
        chunks=report.chunks,
        embedded=report.embedded,
        reused=report.reused,
        failed=len(report.failed),
        snapshot=published.snapshot_id,
        snapshot_status=published.status,
        reason=published.reason,
    )
    return 1 if report.failed or published.status != "published" else 0


if __name__ == "__main__":
    sys.exit(main())
