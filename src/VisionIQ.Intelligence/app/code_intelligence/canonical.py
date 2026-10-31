"""LRN-06 -> LRN-03 immutable, fail-closed code-scan evidence handoff.

Only metadata from a commit-pinned COMPLETE scan is archived. Source code is
never copied into this transport and no source ACL is guessed from filesystem
readability. PARTIAL scans never replace the last complete canonical revision.
"""

import hashlib
from contextlib import suppress
import json
import struct
from dataclasses import asdict, dataclass
from datetime import UTC, datetime
from typing import Protocol
from urllib.parse import urlsplit

from app.code_intelligence.scanner import RepositoryScan

_MAX_BYTES = 10_485_760
_NORMALIZER_VERSION = "metadata-only-v1"


@dataclass(frozen=True, slots=True)
class ScanEvidenceIdentity:
    """Canonical item identity matching the length-prefix contract of .NET LRN-03."""

    source_instance: str
    project_id: str
    repository_id: str

    def item_key(self) -> str:
        """Hash provider, source, project, repository, type, ID without collisions."""
        fields = (
            "git",
            self.source_instance,
            self.project_id,
            self.repository_id,
            "code_scan",
            self.repository_id,
        )
        if any(not value for value in fields):
            raise ValueError("Approved source identity fields must all be populated")
        raw = b"".join(
            struct.pack("<i", len(encoded)) + encoded
            for encoded in (value.encode("utf-8") for value in fields)
        )
        return hashlib.sha256(raw).hexdigest()


@dataclass(frozen=True, slots=True)
class ScanEvidence:
    """One immutable code-scan revision with restricted source authorization."""

    item_key: str
    identity: ScanEvidenceIdentity
    revision: str
    source_url: str
    source_updated_at: datetime
    observed_at: datetime
    content_hash: str
    raw_json: bytes
    redacted_json: str = '{"kind":"code_scan"}'
    acl_verified: bool = False


class ScanArchive(Protocol):
    """Write the original bounded structural metadata to immutable private storage."""

    def store(self, evidence: ScanEvidence) -> str:
        """Return the gs:// object URI only after the archive is safely written."""


class ScanEvidenceStore(Protocol):
    """Persist a canonical revision with transactional immutable semantics."""

    def save(self, evidence: ScanEvidence, archive_uri: str) -> str:
        """Return CREATED, NEW_REVISION, UNCHANGED or CONFLICT."""


def _verified_source_url(source_url: str) -> str:
    """Require an approved HTTPS origin without credentials or query tokens."""
    parsed = urlsplit(source_url)
    if (
        parsed.scheme != "https"
        or not parsed.hostname
        or parsed.username
        or parsed.password
        or parsed.query
        or parsed.fragment
    ):
        raise ValueError("Source URL must be an approved credential-free HTTPS URL")
    return source_url


def build_scan_evidence(
    scan: RepositoryScan,
    identity: ScanEvidenceIdentity,
    source_url: str,
    source_committed_at: datetime,
    observed_at: datetime | None = None,
) -> ScanEvidence:
    """Normalize commit-pinned code metadata without copying source file contents."""
    if scan.status != "COMPLETE":
        raise ValueError("A PARTIAL scan cannot replace canonical last-good evidence")
    if len(scan.commit_sha) not in (40, 64) or not all(
        ch in "0123456789abcdefABCDEF" for ch in scan.commit_sha
    ):
        raise ValueError("Scan revision must be a complete Git commit SHA")
    if source_committed_at.tzinfo is None:
        raise ValueError("Git commit date must be timezone-aware")
    _verified_source_url(source_url)
    observed = observed_at or datetime.now(UTC)
    if observed.tzinfo is None:
        raise ValueError("Observation time must be timezone-aware")

    file_ids = {file.path: file.blob_sha for file in scan.files}
    if len(file_ids) != len(scan.files):
        raise ValueError("Duplicate committed paths are not valid scan evidence")
    for analysis in scan.analyses:
        if file_ids.get(analysis.path) != analysis.blob_sha:
            raise ValueError("Parser evidence did not match committed Git blob identity")
        if analysis.symbol_analysis == "FAILED":
            raise ValueError("A failed parser result cannot be persisted as complete")

    raw_data = {
        "commit_sha": scan.commit_sha.lower(),
        "file_count": len(scan.files),
        "files": [asdict(file) for file in sorted(scan.files, key=lambda x: x.path)],
        "analyses": [
            asdict(analysis) for analysis in sorted(scan.analyses, key=lambda x: x.path)
        ],
        "parser_status": scan.status,
        "schema_version": 1,
    }
    payload = json.dumps(
        raw_data, sort_keys=True, separators=(",", ":"), ensure_ascii=False
    ).encode("utf-8")
    if not 0 < len(payload) <= _MAX_BYTES:
        raise ValueError("Scan metadata exceeds the 10 MiB archive limit")
    return ScanEvidence(
        item_key=identity.item_key(),
        identity=identity,
        revision=scan.commit_sha.lower(),
        source_url=source_url,
        source_updated_at=source_committed_at.astimezone(UTC),
        observed_at=observed.astimezone(UTC),
        content_hash=hashlib.sha256(payload).hexdigest(),
        raw_json=payload,
    )


def ingest_complete_scan(
    scan: RepositoryScan,
    identity: ScanEvidenceIdentity,
    source_url: str,
    source_committed_at: datetime,
    archive: ScanArchive,
    store: ScanEvidenceStore,
) -> str:
    """Archive before DB upsert; report partial scans without replacing last-good."""
    if scan.status != "COMPLETE":
        return "PARTIAL"
    record = build_scan_evidence(
        scan, identity, source_url, source_committed_at
    )
    uri = archive.store(record)
    if not uri.startswith("gs://"):
        raise ValueError("Archive did not return a Google Cloud Storage object URI")
    result = store.save(record, uri)
    if result not in {"CREATED", "NEW_REVISION", "UNCHANGED", "CONFLICT"}:
        raise ValueError("Evidence store returned an unknown status")
    if result == "CONFLICT":
        raise RuntimeError("Canonical immutable revision conflict")
    return result


class GcsScanArchive:
    """Upload immutable code-scan metadata using Google Cloud workload identity."""

    def __init__(self, bucket_name: str) -> None:
        """Bind a pre-approved private bucket, never create one implicitly."""
        if not bucket_name or "/" in bucket_name:
            raise ValueError("A pre-approved Cloud Storage bucket name is required")
        self._bucket_name = bucket_name

    def store(self, evidence: ScanEvidence) -> str:
        """Store content-addressed bytes with a create-only generation precondition."""
        from google.api_core.exceptions import PreconditionFailed
        from google.cloud import storage

        revision_digest = hashlib.sha256(evidence.revision.encode("utf-8")).hexdigest()
        name = (
            f"raw/v1/{evidence.item_key}/{revision_digest}/"
            f"{evidence.content_hash}.json"
        )
        blob = storage.Client().bucket(self._bucket_name).blob(name)
        # An identical content-addressed object may already be archived.
        with suppress(PreconditionFailed):
            blob.upload_from_string(
                evidence.raw_json,
                content_type="application/json",
                if_generation_match=0,
            )
        return f"gs://{self._bucket_name}/{name}"


class PostgresScanEvidenceStore:
    """Use the existing LRN-03 evidence schema with idempotent revision upserts."""

    def __init__(self, connection_string: str) -> None:
        """Accept an authorized runtime-only connection string, never print it."""
        if not connection_string:
            raise ValueError("A runtime PostgreSQL connection string is required")
        self._connection_string = connection_string

    def save(self, evidence: ScanEvidence, archive_uri: str) -> str:
        """Transactionally preserve immutable revisions and the latest good SHA."""
        import psycopg
        from psycopg.types.json import Jsonb

        with (
            psycopg.connect(self._connection_string, connect_timeout=10) as connection,
            connection.cursor() as cursor,
        ):
            cursor.execute(
                """
                INSERT INTO evidence.source_items (
                    item_key, provider, source_instance, project_id, repository_id,
                    kind, external_id, first_observed_at, last_observed_at
                ) VALUES (
                    %(key)s, 'git', %(source)s, %(project)s, %(repository)s,
                    'code_scan', %(repository)s, %(observed)s, %(observed)s
                )
                ON CONFLICT (item_key) DO UPDATE SET
                    last_observed_at = GREATEST(
                        evidence.source_items.last_observed_at, EXCLUDED.last_observed_at
                    )
                RETURNING (xmax = 0)
                """,
                {
                    "key": evidence.item_key,
                    "source": evidence.identity.source_instance,
                    "project": evidence.identity.project_id,
                    "repository": evidence.identity.repository_id,
                    "observed": evidence.observed_at,
                },
            )
            created = bool(cursor.fetchone()[0])
            cursor.execute(
                """
                SELECT content_sha256 FROM evidence.source_revisions
                WHERE item_key = %(key)s AND provider_revision_id = %(revision)s
                """,
                {"key": evidence.item_key, "revision": evidence.revision},
            )
            old = cursor.fetchone()
            if old is not None:
                return "UNCHANGED" if old[0].strip() == evidence.content_hash else "CONFLICT"

            cursor.execute(
                """
                INSERT INTO evidence.source_revisions (
                    item_key, provider_revision_id, source_updated_at,
                    observed_at, content_sha256, source_url,
                    retrieved_under_principal, acl_verified, acl_principals,
                    redacted_payload, redaction_version, normalizer_version,
                    raw_snapshot_uri
                ) VALUES (
                    %(key)s, %(revision)s, %(updated)s,
                    %(observed)s, %(hash)s, %(url)s,
                    'visioniq-code-scanner', FALSE, %(acl)s,
                    %(metadata)s, %(normalizer)s, %(normalizer)s, %(archive)s
                )
                """,
                {
                    "key": evidence.item_key,
                    "revision": evidence.revision,
                    "updated": evidence.source_updated_at,
                    "observed": evidence.observed_at,
                    "hash": evidence.content_hash,
                    "url": evidence.source_url,
                    "acl": Jsonb([]),
                    "metadata": Jsonb({"kind": "code_scan"}),
                    "normalizer": _NORMALIZER_VERSION,
                    "archive": archive_uri,
                },
            )
            cursor.execute(
                """
                UPDATE evidence.source_items
                SET latest_revision_id = %(revision)s,
                    latest_source_updated_at = %(updated)s
                WHERE item_key = %(key)s AND (
                    latest_revision_id IS NULL OR
                    latest_source_updated_at < %(updated)s OR
                    (latest_source_updated_at = %(updated)s
                     AND latest_revision_id < %(revision)s)
                )
                """,
                {
                    "revision": evidence.revision,
                    "updated": evidence.source_updated_at,
                    "key": evidence.item_key,
                },
            )
            return "CREATED" if created else "NEW_REVISION"
