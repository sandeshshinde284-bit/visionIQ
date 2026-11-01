"""Run one indexing pass: ``python -m app.knowledge.index_cli`` (scheduled job, S07-03)."""

from __future__ import annotations

import os
import sys
import uuid

import structlog
from psycopg.conninfo import make_conninfo

from app.knowledge import indexer
from app.knowledge.embeddings import from_environment
from app.logging_config import configure_from_environment


def read_gcs(uri: str) -> bytes:
    """Read an archived raw evidence object (gs://bucket/path) with the job's own credentials."""
    from google.cloud import storage

    if not uri.startswith("gs://"):
        raise ValueError("Raw evidence must be archived in Cloud Storage")
    bucket, _, name = uri[5:].partition("/")
    return storage.Client().bucket(bucket).blob(name).download_as_bytes()


def main() -> int:
    configure_from_environment()
    structlog.contextvars.bind_contextvars(run_id=uuid.uuid4().hex)
    log = structlog.get_logger("visioniq.indexer")
    conninfo = make_conninfo(
        "",
        host=os.environ["DB_HOST"],
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ["DB_NAME"],
        user=os.environ["DB_INTELLIGENCE_USER"],
        password=os.environ["DB_INTELLIGENCE_PASSWORD"],
        sslmode=os.environ.get("DB_SSL_MODE", "prefer"),
    )
    embedder = from_environment()
    log.info("index.started", model=embedder.model_id)
    try:
        report = indexer.run(
            conninfo, read_gcs, embedder, limit=int(os.environ.get("INDEX_BATCH_LIMIT", "500"))
        )
    except Exception as error:
        log.error("index.failed", error_type=type(error).__name__)
        raise
    log.info(
        "index.finished",
        indexed=report.indexed,
        chunks=report.chunks,
        removed=report.removed,
        failed=len(report.failed),
    )
    return 1 if report.failed else 0


if __name__ == "__main__":
    sys.exit(main())
