"""Explicit, authorized entry point to persist a commit-pinned LRN-06 code scan.

Runs only when invoked by a trusted job/operator. Does not create cloud resources,
read .env secrets into logs, or expose an unauthenticated HTTP endpoint.
"""

import argparse
import os
import subprocess
from datetime import datetime
from pathlib import Path
from urllib.parse import urlsplit

from psycopg.conninfo import make_conninfo

from app.code_intelligence.canonical import (
    GcsScanArchive,
    PostgresScanEvidenceStore,
    ScanEvidenceIdentity,
    ingest_complete_scan,
)
from app.code_intelligence.graph import persist_code_graph
from app.code_intelligence.parsers import default_parser_registry
from app.code_intelligence.relationships import ApprovedRepository, resolve_dependencies
from app.code_intelligence.snapshots import scan_with_recovery


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
        help="Optional exact approved ADO organization URL for commit-to-file links",
    )
    arguments = parser.parse_args(args)

    url = _source_url(arguments.source_url)
    identity = ScanEvidenceIdentity(
        source_instance=url,
        project_id=arguments.project_id,
        repository_id=arguments.repository_id,
    )
    identity.item_key()
    repository = Path(arguments.repo).resolve()
    scan = scan_with_recovery(repository, arguments.cache, default_parser_registry())
    if scan.status != "COMPLETE":
        print("LRN-06 scan PARTIAL; durable last-good evidence remains unchanged")
        return 2

    db = make_conninfo(
        "",
        host=_required_env("DB_HOST"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=_required_env("DB_NAME"),
        user=_required_env("DB_API_USER"),
        password=_required_env("DB_API_PASSWORD"),
        sslmode=os.environ.get("DB_SSL_MODE", "require"),
    )
    state = ingest_complete_scan(
        scan,
        identity,
        url,
        _committed_at(scan.repo_root, scan.commit_sha),
        GcsScanArchive(_required_env("GCS_EVIDENCE_BUCKET")),
        PostgresScanEvidenceStore(db),
    )
    # Source evidence has committed before graph projection begins. A failed
    # projection exits nonzero and a repeat is safe (both stores are idempotent).
    from app.code_intelligence.canonical import build_scan_evidence

    archived = build_scan_evidence(
        scan, identity, url, _committed_at(scan.repo_root, scan.commit_sha)
    )
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
        edges = persist_code_graph(
            scan,
            archived,
            db,
            approved_ado_instance=(
                _source_url(arguments.ado_source_instance).rstrip("/")
                if arguments.ado_source_instance
                else None
            ),
            declared_dependencies=links,
        )
        print(f"LRN-07 structural graph edges processed: {edges}")
    print(f"LRN-06 canonical scan result: {state}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
