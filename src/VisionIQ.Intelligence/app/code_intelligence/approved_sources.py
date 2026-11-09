"""Discover approved cross-repository targets from immutable canonical Git scans.

No project names, URLs, or repository mappings are embedded in the application.
A target must already have been scanned by the trusted ingestion workflow, be
current, archived, and have an explicitly verified source access list.

Only the source identities and commit pins are loaded: target raw blobs, source
code, and archived parser metadata are never read or re-parsed.
"""

from __future__ import annotations

from dataclasses import dataclass

import psycopg

from app.code_intelligence.canonical import ScanEvidence, ScanEvidenceIdentity
from app.code_intelligence.relationships import ApprovedRepository, _normalized_remote
from app.code_intelligence.scanner import RepositoryScan


@dataclass(frozen=True)
class ApprovedCodeScanTarget:
    """Minimal immutable target evidence used by the graph projection."""

    item_key: str
    identity: ScanEvidenceIdentity
    revision: str


def load_approved_code_scans(
    connection_string: str,
    source: ScanEvidence,
) -> tuple[tuple[ApprovedRepository, ...], dict[str, ApprovedCodeScanTarget]]:
    """Discover target scans already approved and archived in canonical evidence.

    Scans with missing/contradictory identities, ACLs or commit pins are ignored.
    Ambiguous repository IDs or Git origins are *all* excluded, not resolved by
    arbitrary ordering. On a database error the caller fails closed instead of
    pretending that no cross-repository dependencies exist.

    This is a trusted ingestion-time registry, not a claim that the invoking
    user has permission to read each target. Graph retrieval must still apply
    its normal live, per-source access checks.
    """
    if not source.acl_verified:
        return (), {}

    with psycopg.connect(connection_string, connect_timeout=10) as connection:
        rows = connection.execute(
            """
            SELECT i.item_key, i.source_instance, i.project_id,
                   i.repository_id, i.latest_revision_id, r.source_url
            FROM evidence.source_items AS i
            JOIN evidence.source_revisions AS r
              ON r.item_key = i.item_key
             AND r.provider_revision_id = i.latest_revision_id
            WHERE i.provider = 'git'
              AND i.kind = 'code_scan'
              AND i.deleted_at IS NULL
              AND i.latest_revision_id IS NOT NULL
              AND i.item_key <> %s
              AND r.acl_verified = TRUE
              AND jsonb_array_length(r.acl_principals) > 0
              AND r.archive_status = 'ARCHIVED'
              AND r.raw_snapshot_uri LIKE 'gs://%%'
            ORDER BY i.item_key
            """,
            (source.item_key,),
        ).fetchall()

    candidates: list[tuple[str, str, ApprovedRepository, ApprovedCodeScanTarget]] = []
    for item_key, origin, project_id, repository_id, revision, source_url in rows:
        if not all(
            isinstance(value, str) and value
            for value in (item_key, origin, project_id, repository_id, revision, source_url)
        ):
            continue
        remote = _normalized_remote(origin)
        if remote is None or source_url != origin:
            continue
        if len(revision) not in (40, 64) or any(c not in "0123456789abcdefABCDEF" for c in revision):
            continue
        identity = ScanEvidenceIdentity(origin, project_id, repository_id)
        if identity.item_key() != item_key:
            continue
        target = ApprovedCodeScanTarget(item_key, identity, revision.lower())
        # Resolver needs only the target's approved commit identity; no target
        # source files, parser output or Cloud Storage reads are necessary.
        scan = RepositoryScan("", revision.lower(), "COMPLETE", (), ())
        candidates.append(
            (remote, repository_id, ApprovedRepository(repository_id, origin, scan), target)
        )

    # Provider-native repository IDs may be reused across organizations. Git
    # origins may likewise appear under multiple source scopes. Neither case
    # may silently choose an arbitrary target by dictionary overwrite.
    origin_counts: dict[str, int] = {}
    repo_counts: dict[str, int] = {}
    for remote, repo_id, _, _ in candidates:
        origin_counts[remote] = origin_counts.get(remote, 0) + 1
        repo_counts[repo_id] = repo_counts.get(repo_id, 0) + 1

    approved = [
        (repository, target)
        for remote, repo_id, repository, target in candidates
        if origin_counts[remote] == 1 and repo_counts[repo_id] == 1
    ]
    return (
        tuple(repository for repository, _ in approved),
        {target.identity.repository_id: target for _, target in approved},
    )
