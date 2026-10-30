"""LRN-05 durable source change inbox and reconciliation run reports.

Revision ID: 20261030_0005
Revises: 20261030_0004
"""

from alembic import op

revision = "20261030_0005"
down_revision = "20261030_0004"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Create the idempotent event inbox and per-source job audit tables."""
    op.execute(
        """
        CREATE TABLE evidence.source_change_inbox (
            source_id text NOT NULL CHECK (length(source_id) BETWEEN 1 AND 256),
            event_id text NOT NULL CHECK (length(event_id) BETWEEN 1 AND 256),
            provider_kind text NOT NULL CHECK (length(provider_kind) BETWEEN 1 AND 80),
            native_id text NOT NULL CHECK (length(native_id) BETWEEN 1 AND 256),
            source_revision text NOT NULL CHECK (length(source_revision) BETWEEN 1 AND 256),
            status text NOT NULL DEFAULT 'processing'
                CHECK (status IN ('processing', 'completed', 'quarantined')),
            claim_token uuid NOT NULL,
            claim_expires_at timestamptz NOT NULL,
            attempt_count integer NOT NULL DEFAULT 1 CHECK (attempt_count > 0),
            last_error_code text NULL CHECK (last_error_code ~ '^[A-Z_]{1,80}$'),
            received_at timestamptz NOT NULL DEFAULT now(),
            updated_at timestamptz NOT NULL DEFAULT now(),
            completed_at timestamptz NULL,
            PRIMARY KEY (source_id, event_id)
        )
        """
    )
    op.execute(
        """
        CREATE INDEX source_change_inbox_pending_idx
        ON evidence.source_change_inbox(status, claim_expires_at)
        WHERE status IN ('processing','quarantined')
        """
    )
    op.execute(
        """
        CREATE TABLE evidence.source_sync_runs (
            run_id uuid PRIMARY KEY,
            source_id text NOT NULL CHECK (length(source_id) BETWEEN 1 AND 256),
            mode text NOT NULL CHECK (mode IN ('full', 'delta', 'reconcile')),
            status text NOT NULL CHECK (status IN ('synced', 'partial', 'not_synced')),
            records_received integer NOT NULL CHECK (records_received >= 0),
            records_stored integer NOT NULL CHECK (records_stored >= 0),
            records_quarantined integer NOT NULL CHECK (records_quarantined >= 0),
            expected_count integer NULL CHECK (expected_count >= 0),
            result_cursor text NULL,
            started_at timestamptz NOT NULL,
            completed_at timestamptz NOT NULL,
            CHECK (completed_at >= started_at)
        )
        """
    )
    op.execute(
        """
        CREATE INDEX source_sync_runs_recent_idx
        ON evidence.source_sync_runs(source_id, completed_at DESC)
        """
    )


def downgrade() -> None:
    """Prevent accidental deletion of audit and event-processing records."""
    raise RuntimeError(
        "LRN-05 reconciliation and source-change audit is immutable; use a forward migration."
    )
