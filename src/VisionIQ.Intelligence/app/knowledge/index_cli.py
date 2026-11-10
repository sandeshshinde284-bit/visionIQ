"""Keep the search index current and published (scheduled job, S07-03, LRN-15).

python -m app.knowledge.index_cli                 # index changes, publish a checked snapshot
python -m app.knowledge.index_cli status          # the served snapshot and recent attempts
python -m app.knowledge.index_cli rollback [id]   # serve the last good (or a named) snapshot
"""

from __future__ import annotations

import argparse
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
    parser = argparse.ArgumentParser(
        description="Refresh the VisionIQ search index from canonical evidence"
    )
    parser.add_argument(
        "--only",
        action="append",
        metavar="ITEM_KEY",
        help="Reindex a specific canonical item key; repeat for multiple items",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=int(os.environ.get("INDEX_BATCH_LIMIT", "500")),
        help="Maximum changed items in this pass (default: INDEX_BATCH_LIMIT or 500)",
    )
    parser.add_argument(
        "--min-full-coverage",
        type=float,
        default=float(os.environ.get("INDEX_MIN_FULL_COVERAGE", str(snapshots.MIN_FULL_COVERAGE))),
        help="Minimum fraction of eligible items a FULL (no --only) pass must cover to publish "
        "(default: INDEX_MIN_FULL_COVERAGE or 0.95); ignored for a targeted --only pass",
    )
    parser.add_argument(
        "--allow-partial",
        action="store_true",
        default=os.environ.get("INDEX_ALLOW_PARTIAL", "").strip().lower() in ("1", "true", "yes"),
        help="Explicitly allow a full (no --only) pass below --min-full-coverage, for initial "
        "bulk backfill (default: INDEX_ALLOW_PARTIAL or off). The snapshot is published as an "
        "'incremental' pass, marked partial, and can never serve fewer items than are already "
        "published -- coverage only grows while backfilling, it never regresses.",
    )
    options = parser.parse_args(args) if not args or args[0] not in ("status", "rollback") else None
    if options is not None and options.limit < 1:
        parser.error("--limit must be greater than zero")
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
    if options.only and options.allow_partial:
        parser.error("--allow-partial only applies to a full pass; it is not used with --only")
    embedder = from_environment()
    scope = "targeted" if options.only else ("incremental" if options.allow_partial else "full")
    log.info("index.started", model=embedder.model_id, scope=scope)
    try:
        report, published = snapshots.refresh(
            conninfo,
            read_gcs,
            embedder,
            only=options.only,
            limit=options.limit,
            min_full_coverage=options.min_full_coverage,
            allow_partial=options.allow_partial,
        )
    except Exception as error:
        log.error("index.failed", error_type=type(error).__name__)
        raise
    log.info(
        "index.finished",
        scope=scope,
        indexed=report.indexed,
        chunks=report.chunks,
        embedded=report.embedded,
        reused=report.reused,
        failed=len(report.failed),
        failed_item_keys=report.failed[:20],
        eligible=published.counts.get("eligible"),
        covered=published.counts.get("items"),
        coverage_status=published.counts.get("coverage_status"),
        snapshot=published.snapshot_id,
        snapshot_status=published.status,
        reason=published.reason,
    )
    if scope == "targeted" and published.status == "published":
        log.info(
            "index.targeted_publish",
            note="This pass only claims coverage for the --only item(s); it is not a full-corpus "
            "readiness signal. Run without --only for a full pass before reporting the index as "
            "complete.",
            items=options.only,
        )
    if scope == "incremental" and published.status == "published":
        log.info(
            "index.incremental_publish",
            note="This is a partial backfill pass, not a full-corpus readiness signal. Keep "
            "running with --allow-partial until coverage reaches --min-full-coverage, then run "
            "a normal full pass (no flag) to confirm and mark the index complete.",
            eligible=published.counts.get("eligible"),
            covered=published.counts.get("items"),
        )
    return 1 if report.failed or published.status != "published" else 0


if __name__ == "__main__":
    sys.exit(main())
