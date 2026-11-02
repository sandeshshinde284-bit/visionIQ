"""LRN-15: publish consistent knowledge snapshots (master design 6.6 stage 9, 6.11, 6.15, KB-5).

Index rows become immutable entries (one per item revision, access list and pipeline versions).
A snapshot lists which entry of each item is served; a candidate is published only after
consistency and access checks, by moving a single pointer; rollback moves it back.

Revision ID: 20261101_0017
Revises: 20261101_0016
"""

from alembic import op

revision = "20261101_0017"
down_revision = "20261101_0016"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Existing index rows become an initial published snapshot: nothing stops serving."""
    op.execute(
        """
        CREATE TABLE knowledge.index_entries (
            entry_key character(64) PRIMARY KEY CHECK (entry_key ~ '^[0-9a-f]{64}$'),
            item_key character(64) NOT NULL,
            provider_revision_id text NOT NULL,
            source_content_sha256 character(64) NOT NULL,
            acl_principals jsonb NOT NULL CHECK (jsonb_typeof(acl_principals) = 'array'),
            chunker_version text NOT NULL,
            redaction_version text NOT NULL,
            model_id text NOT NULL,
            chunk_count integer NOT NULL CHECK (chunk_count >= 0),
            indexed_at timestamptz NOT NULL
        );
        CREATE INDEX index_entries_item ON knowledge.index_entries (item_key, indexed_at DESC);

        INSERT INTO knowledge.index_entries
        SELECT encode(sha256(convert_to(
                   item_key || '|' || provider_revision_id || '|' || source_content_sha256 || '|'
                   || acl_principals::text || '|' || chunker_version || '|' || redaction_version
                   || '|' || model_id, 'UTF8')), 'hex'),
               item_key, provider_revision_id, source_content_sha256, acl_principals,
               chunker_version, redaction_version, model_id, chunk_count, indexed_at
        FROM knowledge.index_items;

        ALTER TABLE knowledge.index_chunks ADD COLUMN entry_key character(64);
        UPDATE knowledge.index_chunks AS c SET entry_key = e.entry_key
        FROM knowledge.index_entries AS e WHERE e.item_key = c.item_key;
        ALTER TABLE knowledge.index_chunks
            ALTER COLUMN entry_key SET NOT NULL,
            DROP CONSTRAINT index_chunks_item_key_fkey,
            ADD CONSTRAINT index_chunks_entry_key_fkey FOREIGN KEY (entry_key)
                REFERENCES knowledge.index_entries (entry_key) ON DELETE CASCADE;
        CREATE INDEX index_chunks_entry ON knowledge.index_chunks (entry_key);
        DROP TABLE knowledge.index_items;

        CREATE TABLE knowledge.snapshots (
            snapshot_id text PRIMARY KEY CHECK (snapshot_id ~ '^snap-[0-9a-z-]{8,64}$'),
            status text NOT NULL CHECK (status IN
                ('candidate', 'published', 'superseded', 'rejected', 'rolled_back', 'expired')),
            parent_snapshot_id text NULL REFERENCES knowledge.snapshots (snapshot_id),
            model_id text NOT NULL,
            chunker_version text NOT NULL,
            redaction_version text NOT NULL,
            counts jsonb NOT NULL DEFAULT '{}',
            checks jsonb NOT NULL DEFAULT '{}',
            reason text NULL,
            created_at timestamptz NOT NULL DEFAULT now(),
            published_at timestamptz NULL
        );
        CREATE TABLE knowledge.snapshot_entries (
            snapshot_id text NOT NULL REFERENCES knowledge.snapshots (snapshot_id),
            item_key character(64) NOT NULL,
            entry_key character(64) NOT NULL REFERENCES knowledge.index_entries (entry_key),
            PRIMARY KEY (snapshot_id, item_key)
        );
        CREATE INDEX snapshot_entries_entry ON knowledge.snapshot_entries (entry_key, snapshot_id);
        -- Exactly one served snapshot: publishing and rollback move this single pointer.
        CREATE TABLE knowledge.snapshot_pointer (
            singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
            snapshot_id text NOT NULL REFERENCES knowledge.snapshots (snapshot_id),
            moved_at timestamptz NOT NULL DEFAULT now()
        );

        ALTER TABLE knowledge.change_events DROP CONSTRAINT change_events_event_type_check,
            ADD CONSTRAINT change_events_event_type_check CHECK (event_type IN (
                'index.item_updated', 'index.item_removed', 'lesson.created', 'lesson.revised',
                'snapshot.published', 'snapshot.rejected', 'snapshot.rolled_back'))
        """
    )
    # Serve what was already indexed as the first published snapshot (only if anything was indexed).
    op.execute(
        """
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM knowledge.index_entries) THEN
                INSERT INTO knowledge.snapshots (snapshot_id, status, model_id, chunker_version,
                    redaction_version, reason, published_at)
                SELECT 'snap-initial-migration', 'published', min(model_id), min(chunker_version),
                       min(redaction_version), 'carried over by migration 0017', now()
                FROM knowledge.index_entries;
                INSERT INTO knowledge.snapshot_entries
                SELECT 'snap-initial-migration', item_key, entry_key FROM knowledge.index_entries;
                INSERT INTO knowledge.snapshot_pointer (snapshot_id)
                VALUES ('snap-initial-migration');
            END IF;
        END $$
        """
    )


def downgrade() -> None:
    """Snapshot history cannot be folded back into a single in-place index without loss."""
    raise RuntimeError(
        "Knowledge snapshots cannot be removed by downgrade; use a forward migration."
    )
