"""Explicit, authorized entry point to persist a commit-pinned LRN-06 code scan.

Runs only when invoked by a trusted job/operator. Does not create cloud resources,
read .env secrets into logs, or expose an unauthenticated HTTP endpoint.
"""

import argparse
import os
import subprocess
import time
import uuid
from datetime import datetime
from pathlib import Path
from urllib.parse import urlsplit

import structlog
from psycopg.conninfo import make_conninfo

from app.code_intelligence.canonical import (
    GcsScanArchive,
    PostgresScanEvidenceStore,
    ScanEvidenceIdentity,
    ingest_complete_scan,
)
from app.code_intelligence.parsers import default_parser_registry
from app.code_intelligence.relationships import ApprovedRepository, resolve_dependencies
from app.code_intelligence.snapshots import scan_with_recovery
from app.logging_config import configure_from_environment


def _required_env(key: str) -> str:
    """Read a required private runtime setting without disclosing its value."""
    value = os.environ.get(key)
    if not value:
        raise ValueError(f"Required runtime configuration is missing: {key}")
    return value


def _committed_at(repo_root: str, commit: str) -> datetime:
    """Get the commit's real author-independent committer timestamp from Git."""
    result = subprocess.run(
        ["git", "-C", repo_root, "show", "-s", "--format=%cI", commit],
        capture_output=True,
        check=True,
        timeout=60,
        text=True,
    )
    value = datetime.fromisoformat(result.stdout.strip())
    if value.tzinfo is None:
        raise ValueError("Git commit time does not include a timezone")
    return value


def _source_url(raw: str) -> str:
    """Accept only explicitly approved, credential-free HTTPS source URLs."""
    parsed = urlsplit(raw)
    if (
        parsed.scheme != "https"
        or not parsed.hostname
        or parsed.username
        or parsed.password
        or parsed.query
        or parsed.fragment
    ):
        raise ValueError("Approved source URL must be HTTPS without embedded credentials")
    return raw


def main(args: list[str] | None = None) -> int:
    """Scan one approved local Git checkout and archive only complete evidence."""
    parser = argparse.ArgumentParser(description="Import approved Git scan to canonical evidence")
    parser.add_argument("--repo", required=True, help="Approved local Git checkout")
    parser.add_argument("--cache", required=True, help="Private local last-good metadata cache")
    parser.add_argument("--project-id", required=True, help="Provider-native approved project ID")
    parser.add_argument(
        "--repository-id", required=True, help="Provider-native approved repository ID"
    )
    parser.add_argument("--source-url", required=True, help="Trusted HTTPS repository source URL")
    parser.add_argument(
        "--ado-source-instance",
        help=(
            "Approved ADO organization URL: links commits to files, and makes this "
            "repository's code readable to members of --project-id (the ADO project name)"
        ),
    )
    arguments = parser.parse_args(args)

    configure_from_environment()
    # Every line of this scan carries scan_id, project and repository. Never logged:
    # connection strings, passwords, environment values, bucket names or source content.
    structlog.contextvars.clear_contextvars()
    structlog.contextvars.bind_contextvars(
        scan_id=uuid.uuid4().hex,
        project_id=arguments.project_id,
        repository_id=arguments.repository_id,
    )
    log = structlog.get_logger("visioniq.scanner")
    try:
        return _run(arguments, log)
    except Exception as error:
        log.error("scan.failed", error_type=type(error).__name__)  # never the message
        raise
    finally:
        structlog.contextvars.clear_contextvars()


def _run(arguments: argparse.Namespace, log) -> int:
    started = time.monotonic()
    url = _source_url(arguments.source_url)
    identity = ScanEvidenceIdentity(
        source_instance=url,
        project_id=arguments.project_id,
        repository_id=arguments.repository_id,
    )
    identity.item_key()
    repository = Path(arguments.repo).resolve()
    log.info("scan.started")
    scan = scan_with_recovery(repository, arguments.cache, default_parser_registry())
    gaps = [a for a in scan.analyses if any("could not fully parse" in d for d in a.diagnostics)]
    for analysis in gaps:
        # N-16: visible, never silent. The diagnostic names the line, never the code.
        log.warning("scan.grammar_gap", path=analysis.path, diagnostic=analysis.diagnostics[0])
    log.info(
        "scan.completed",
        status=scan.status,
        commit=scan.commit_sha,
        files=len(scan.files),
        parsed=sum(a.symbol_analysis == "AVAILABLE" for a in scan.analyses),
        file_level_only=sum(a.symbol_analysis == "UNAVAILABLE" for a in scan.analyses),
        failed=sum(a.symbol_analysis == "FAILED" for a in scan.analyses),
        symbols=sum(len(a.symbols) for a in scan.analyses),
        grammar_gaps=len(gaps),
        duration_ms=round((time.monotonic() - started) * 1000),
    )
    if scan.status != "COMPLETE":
        log.warning("scan.partial", reasons=list(scan.errors[:20]), last_good_kept=True)
        return 2

    db = make_conninfo(
        "",
        host=_required_env("DB_HOST"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=_required_env("DB_NAME"),
        # B-15: the scanner writes with its own login. Row-level security (db/manage.py)
        # limits it to git code_scan evidence and the graph rows that evidence proves.
        user=_required_env("DB_INTELLIGENCE_USER"),
        password=_required_env("DB_INTELLIGENCE_PASSWORD"),
        sslmode=os.environ.get("DB_SSL_MODE", "prefer"),
    )
    from app.code_intelligence.canonical import build_scan_evidence

    # m-4: read the commit time and build the evidence once, then reuse both.
    committed_at = _committed_at(scan.repo_root, scan.commit_sha)
    ado_organization = (
        _source_url(arguments.ado_source_instance).rstrip("/")
        if arguments.ado_source_instance
        else None
    )
    archived = build_scan_evidence(
        scan, identity, url, committed_at, ado_organization=ado_organization
    )
    # M-16: remember the previously scanned commit BEFORE storing this scan (which becomes latest).
    from app.code_intelligence import graph

    previous_commit = graph.latest_scanned_commit(db, archived.item_key)
    log.debug("scan.previous_commit", previous_commit=previous_commit)
    state = ingest_complete_scan(
        scan,
        identity,
        url,
        committed_at,
        GcsScanArchive(_required_env("GCS_EVIDENCE_BUCKET")),
        PostgresScanEvidenceStore(db),
        evidence=archived,
    )
    log.info(
        "evidence.stored",
        state=state,
        item_key=archived.item_key,
        revision=archived.revision,
        access_verified=archived.acl_verified,
    )
    # Source evidence has committed before graph projection begins. A failed
    # projection exits nonzero and a repeat is safe (both stores are idempotent).
    if state in {"CREATED", "NEW_REVISION", "UNCHANGED"}:
        # Only source-declared, committed references can become graph edges.
        # Read manifests from pinned Git blobs, never from the working tree.
        def read_committed_manifest(path: str) -> str:
            """Read a bounded committed manifest without executing source code."""
            result = subprocess.run(
                ["git", "-C", scan.repo_root, "show", f"{scan.commit_sha}:{path}"],
                capture_output=True,
                check=True,
                timeout=60,
            )
            if len(result.stdout) > 1_048_576:
                raise ValueError("Committed dependency manifest exceeds 1 MiB")
            return result.stdout.decode("utf-8")

        links = resolve_dependencies(
            ApprovedRepository(identity.repository_id, url, scan),
            read_blob=read_committed_manifest,
        )
        edges = graph.persist_code_graph(
            scan,
            archived,
            db,
            approved_ado_instance=ado_organization,
            previous_commit=previous_commit,
            declared_dependencies=links,
        )
        log.info("graph.persisted", edges=edges, dependency_links=len(links))
    log.info("scan.finished", state=state, duration_ms=round((time.monotonic() - started) * 1000))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
