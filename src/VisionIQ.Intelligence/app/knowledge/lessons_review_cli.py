"""LRN-10 Human Gate actions: a named engineer marks a lesson reviewed/disputed/retired, or
splits a pattern that conflates two distinct root causes (master design 7.4.1, 8.8-8.10).

These are never produced by the automated consolidation job (lessons_cli.py / lessons.py's
consolidate()) -- that job is deliberately pure and evidence-only. Every action here requires a
named --verified-by; there is no anonymous or automatic path to "reviewed" anywhere in the system.

Usage:
    python -m app.knowledge.lessons_review_cli review <pattern_id> --status reviewed \
        --verified-by "jane.doe" [--cause-status verified] [--rationale "..."]

    python -m app.knowledge.lessons_review_cli fork <pattern_id> --verified-by "jane.doe" \
        --observation-key <key> [--observation-key <key> ...] [--rationale "..."]
"""

from __future__ import annotations

import argparse
import os
import sys
from datetime import UTC, datetime

import psycopg
import structlog
from psycopg.conninfo import make_conninfo

from app.knowledge import lesson_store
from app.knowledge.lessons_cli import LOCK as CONSOLIDATION_LOCK
from app.logging_config import configure_from_environment


def _env(name: str) -> str:
    value = os.environ.get(name)
    if not value:
        raise SystemExit(f"{name} is required")
    return value


def _conninfo() -> str:
    # Reviews are a privileged human action, not the unattended intelligence job -- use the API
    # role (matches how other human-review writes, e.g. ticket-fix link review, are authenticated).
    return make_conninfo(
        "",
        host=_env("DB_HOST"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=_env("DB_NAME"),
        user=_env("DB_API_USER"),
        password=_env("DB_API_PASSWORD"),
        sslmode=os.environ.get("DB_SSL_MODE", "prefer"),
    )


def run_review(args: argparse.Namespace) -> dict[str, object]:
    now = datetime.now(UTC)
    with psycopg.connect(_conninfo()) as conn, conn.transaction():
        conn.execute("SELECT pg_advisory_xact_lock(%s)", (CONSOLIDATION_LOCK,))
        revision = lesson_store.review(
            conn,
            pattern_id=args.pattern_id,
            status=args.status,
            verified_by=args.verified_by,
            now=now,
            cause_status=args.cause_status,
            rationale=args.rationale,
        )
    return {"pattern_id": args.pattern_id, "revision": revision, "status": args.status}


def run_fork(args: argparse.Namespace) -> dict[str, object]:
    now = datetime.now(UTC)
    with psycopg.connect(_conninfo()) as conn, conn.transaction():
        conn.execute("SELECT pg_advisory_xact_lock(%s)", (CONSOLIDATION_LOCK,))
        new_pattern_id = lesson_store.fork(
            conn,
            pattern_id=args.pattern_id,
            observation_keys_to_split_out=args.observation_key,
            verified_by=args.verified_by,
            now=now,
            rationale=args.rationale,
        )
    return {"original_pattern_id": args.pattern_id, "new_pattern_id": new_pattern_id}


def main() -> int:
    configure_from_environment()
    log = structlog.get_logger("visioniq.lessons_review")

    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="action", required=True)

    review_parser = sub.add_parser("review", help="Mark a lesson pattern reviewed, disputed or retired.")
    review_parser.add_argument("pattern_id")
    review_parser.add_argument("--status", required=True, choices=lesson_store.REVIEW_STATUSES)
    review_parser.add_argument("--verified-by", required=True, help="Named human, never anonymous.")
    review_parser.add_argument("--cause-status", default="unverified", choices=lesson_store.CAUSE_STATUSES)
    review_parser.add_argument("--rationale", default=None)

    fork_parser = sub.add_parser("fork", help="Split a pattern's members into a new, separate pattern.")
    fork_parser.add_argument("pattern_id")
    fork_parser.add_argument("--verified-by", required=True, help="Named human, never anonymous.")
    fork_parser.add_argument(
        "--observation-key", action="append", required=True, dest="observation_key",
        help="Repeat for each observation to move into the new pattern.",
    )
    fork_parser.add_argument("--rationale", default=None)

    args = parser.parse_args()
    try:
        result = run_review(args) if args.action == "review" else run_fork(args)
    except (ValueError, psycopg.Error) as error:
        log.error("lessons_review.failed", action=args.action, error=str(error))
        print(f"FAILED: {error}", file=sys.stderr)
        return 1
    log.info("lessons_review.finished", action=args.action, **result)
    print(result)
    return 0


if __name__ == "__main__":
    sys.exit(main())
