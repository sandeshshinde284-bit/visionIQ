"""LRN-03 canonical versioned evidence, ACL and archive metadata.

Revision ID: 20261030_0002
Revises: 20261029_0001
"""

from alembic import op

revision = "20261030_0002"
down_revision = "20261029_0001"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.execute(
        """
        CREATE TABLE evidence.source_items (
            item_key CHAR(64) PRIMARY KEY,
            provider TEXT NOT NULL CHECK (length(provider) BETWEEN 1 AND 80),
            source_instance TEXT NOT NULL CHECK (length(source_instance) > 0),
            project_id TEXT NOT NULL CHECK (length(project_id) > 0),
            repository_id TEXT NULL,
            kind TEXT NOT NULL CHECK (length(kind) > 0),
            external_id TEXT NOT NULL CHECK (length(external_id) > 0),
            latest_revision_id TEXT NULL,
            latest_source_updated_at TIMESTAMPTZ NULL,
            first_observed_at TIMESTAMPTZ NOT NULL,
            last_observed_at TIMESTAMPTZ NOT NULL,
            CONSTRAINT source_items_repository_not_empty
                CHECK (repository_id IS NULL OR length(repository_id) > 0),
            CONSTRAINT source_items_observation_order
                CHECK (last_observed_at >= first_observed_at)
        )
        """
    )
    op.execute(
        """
        CREATE TABLE evidence.source_revisions (
            item_key CHAR(64) NOT NULL
                REFERENCES evidence.source_items(item_key) ON DELETE RESTRICT,
            provider_revision_id TEXT NOT NULL CHECK (length(provider_revision_id) > 0),
            source_updated_at TIMESTAMPTZ NOT NULL,
            observed_at TIMESTAMPTZ NOT NULL,
            content_sha256 CHAR(64) NOT NULL,
            source_url TEXT NOT NULL,
            retrieved_under_principal TEXT NOT NULL,
            acl_verified BOOLEAN NOT NULL DEFAULT FALSE,
            acl_principals JSONB NOT NULL DEFAULT '[]'::jsonb,
            redacted_payload JSONB NOT NULL DEFAULT '{}'::jsonb,
            redaction_version TEXT NOT NULL,
            normalizer_version TEXT NOT NULL,
            raw_snapshot_uri TEXT NOT NULL,
            archive_status TEXT NOT NULL DEFAULT 'ARCHIVED'
                CHECK (archive_status IN ('ARCHIVED')),
            PRIMARY KEY (item_key, provider_revision_id),
            CHECK (jsonb_typeof(acl_principals) = 'array'),
            CHECK (jsonb_typeof(redacted_payload) = 'object')
        )
        """
    )
    op.execute(
        """
        CREATE INDEX source_revisions_acl_idx
        ON evidence.source_revisions USING gin(acl_principals)
        """
    )
    op.execute(
        """
        CREATE INDEX source_revisions_source_date_idx
        ON evidence.source_revisions(item_key, source_updated_at DESC)
        """
    )
    op.execute(
        """
        CREATE INDEX source_items_project_idx
        ON evidence.source_items(provider, source_instance, project_id, kind)
        """
    )
    # Latest revision references an existing revision of the SAME source.
    op.execute(
        """
        ALTER TABLE evidence.source_items
        ADD CONSTRAINT source_items_latest_revision_fkey
        FOREIGN KEY (item_key, latest_revision_id)
        REFERENCES evidence.source_revisions(item_key, provider_revision_id)
        DEFERRABLE INITIALLY DEFERRED
        """
    )


def downgrade() -> None:
    raise RuntimeError(
        "LRN-03 evidence history cannot be downgraded automatically: immutable source data."
    )
