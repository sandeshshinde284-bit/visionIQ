"""Shared demo-history builder for LEARN database tests (evidence written as ingestion would)."""

import hashlib
import json
import os
import uuid

import psycopg
from psycopg.conninfo import make_conninfo


def conninfo(prefix: str) -> str:
    return make_conninfo(
        "",
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ[f"DB_{prefix}_USER"],
        password=os.environ[f"DB_{prefix}_PASSWORD"],
    )


class Corpus:
    """One isolated organisation per test: its own source instance and access scope."""

    def __init__(self) -> None:
        self.org = f"https://dev.azure.com/t{uuid.uuid4().hex[:10]}"
        self.scope = f"ado-project:{self.org}/Payments"
        self.archive: dict[str, bytes] = {}

    def key(self, kind: str, native: str) -> str:
        return hashlib.sha256(f"{self.org}|{kind}|{native}".encode()).hexdigest()

    def add(self, kind, record, *, scope=None, revision="1", deleted=False, verified=True):
        native = str(record.get("id"))
        item = self.key(kind, native)
        body = json.dumps(record).encode()
        uri = f"mem://{item}/{revision}"
        self.archive[uri] = body
        meta = {"kind": kind, "workItemType": record.get("workItemType")}
        with psycopg.connect(conninfo("API")) as conn:
            conn.execute(
                "INSERT INTO evidence.source_items (item_key, provider, source_instance, "
                "project_id, kind, external_id, latest_revision_id, latest_source_updated_at, "
                "first_observed_at, last_observed_at, deleted_at, deletion_reason) VALUES "
                "(%s, 'azure_devops', %s, 'Payments', %s, %s, %s, now(), now(), now(), %s, %s) "
                "ON CONFLICT (item_key) DO UPDATE SET "
                "latest_revision_id = EXCLUDED.latest_revision_id, "
                "deleted_at = EXCLUDED.deleted_at, deletion_reason = EXCLUDED.deletion_reason",
                (
                    item,
                    self.org,
                    kind,
                    native,
                    revision,
                    "2026-01-01T00:00:00Z" if deleted else None,
                    "deleted_in_source" if deleted else None,
                ),
            )
            conn.execute(
                "INSERT INTO evidence.source_revisions (item_key, provider_revision_id, "
                "source_updated_at, observed_at, content_sha256, source_url, "
                "retrieved_under_principal, redacted_payload, redaction_version, "
                "normalizer_version, raw_snapshot_uri, archive_status, acl_verified, "
                "acl_principals) VALUES (%s, %s, now(), now(), %s, %s, 't', %s, 'v', 'v', %s, "
                "'ARCHIVED', %s, %s)",
                (
                    item,
                    revision,
                    hashlib.sha256(body).hexdigest(),
                    f"{self.org}/Payments/_workitems/edit/{native}",
                    json.dumps(meta),
                    uri,
                    verified,
                    json.dumps([scope or self.scope]),
                ),
            )
        return item

    def fix(self, ticket: str, pr: str, paths: list[str], reverted: bool = False) -> None:
        """An explicit ticket -> PR fix link and the files that PR changed."""
        with psycopg.connect(conninfo("API")) as conn:
            conn.execute(
                "INSERT INTO evidence.ticket_fix_links (association_key, source_instance, "
                "project_id, ticket_id, repository_id, pull_request_id, source_classification, "
                "revert_commit_shas) VALUES (%s, %s, 'Payments', %s, 'repo-1', %s, 'explicit', %s)",
                (
                    hashlib.sha256(f"{self.org}|{ticket}|{pr}".encode()).hexdigest(),
                    self.org,
                    ticket,
                    pr,
                    json.dumps(["deadbeef"] if reverted else []),
                ),
            )
            conn.execute(
                "INSERT INTO evidence.fix_change_sets (source_instance, project_id, repository_id, "
                "fix_kind, fix_id, paths, provenance_item_key, observed_at) "
                "VALUES (%s, 'Payments', 'repo-1', 'pull_request', %s, %s, %s, now())",
                (self.org, pr, paths, "a" * 64),
            )

    def read(self, uri: str) -> bytes:
        return self.archive[uri]
