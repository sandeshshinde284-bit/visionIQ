"""LRN-04 review R4-02: retract a ticket/fix association once its source stops asserting it.

Revision ID: 20261104_0019
Revises: 20261103_0018

Additive only. Existing evidence.ticket_fix_links rows get provenance_status='unreconciled' and
retracted_at=NULL (never retroactively marked retracted: the pre-existing source_evidence_refs
column is append-only and cannot tell us what a row's source asserted at any earlier point). A
row becomes eligible for retraction only after the next successful sync of its originating work
item or pull request re-establishes real per-source provenance in the tables below.

Shape mirrors the existing evidence.test_link_sources / evidence.tested_by_links pattern: one
small guard row per originating source entity (source_revision_time, never our own processing
clock, so an out-of-order replay is rejected the same way PostgresTestLinkStore.ReplaceAsync
already rejects one), plus a join table recording which associations that source currently
asserts.
"""

from alembic import op

revision = "20261104_0019"
down_revision = "20261103_0018"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Add per-source provenance tracking and a retraction summary on ticket_fix_links."""
    op.execute(
        """
        ALTER TABLE evidence.ticket_fix_links
            ADD COLUMN retracted_at timestamptz NULL,
            ADD COLUMN retraction_reason text NULL
                CHECK (retraction_reason IS NULL OR retraction_reason IN ('missing_in_source')),
            ADD COLUMN provenance_status text NOT NULL DEFAULT 'unreconciled'
                CHECK (provenance_status IN ('unreconciled', 'reconciled')),
            ADD CONSTRAINT ticket_fix_links_retraction_consistent
                CHECK ((retracted_at IS NULL) = (retraction_reason IS NULL))
        """
    )
    op.execute(
        """
        -- One row per originating work item or pull request. source_revision_time is that
        -- entity's own source time (ChangedDate / LastActivity), never our processing clock.
        CREATE TABLE evidence.ticket_fix_link_sources (
            source_item_key CHAR(64) PRIMARY KEY,
            source_revision_id text NOT NULL CHECK (length(source_revision_id) BETWEEN 1 AND 256),
            source_revision_time timestamptz NOT NULL,
            updated_at timestamptz NOT NULL DEFAULT now()
        )
        """
    )
    op.execute(
        """
        -- Which associations the source's most recently accepted revision currently asserts.
        -- ended_at marks a transition out of the current set (soft; never deleted), so the
        -- lifecycle -- asserted, then ended -- stays on the record rather than being discarded.
        CREATE TABLE evidence.ticket_fix_link_source_associations (
            source_item_key CHAR(64) NOT NULL
                REFERENCES evidence.ticket_fix_link_sources(source_item_key) ON DELETE RESTRICT,
            association_key text NOT NULL
                REFERENCES evidence.ticket_fix_links(association_key) ON DELETE RESTRICT,
            first_asserted_at timestamptz NOT NULL DEFAULT now(),
            ended_at timestamptz NULL,
            PRIMARY KEY (source_item_key, association_key)
        )
        """
    )
    op.execute(
        """
        CREATE INDEX ticket_fix_link_source_associations_key_idx
        ON evidence.ticket_fix_link_source_associations(association_key)
        WHERE ended_at IS NULL
        """
    )


def downgrade() -> None:
    """Do not silently discard retraction provenance; use a reviewed forward migration."""
    raise RuntimeError(
        "LRN-04 retraction provenance is audit evidence; use a reviewed forward migration."
    )
