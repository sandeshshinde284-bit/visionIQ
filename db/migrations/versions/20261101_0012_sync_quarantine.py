"""G-1 (LRN-05): per-item quarantine list for scheduled syncs.

Revision ID: 20261101_0012
Revises: 20261101_0011
"""

from alembic import op

revision = "20261101_0012"
down_revision = "20261101_0011"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Items a sync could not read: listed with reason and attempts, resolved when later read."""
    op.execute(
        """
        CREATE TABLE evidence.sync_quarantine (
            source_id text NOT NULL CHECK (length(source_id) BETWEEN 1 AND 256),
            scope text NOT NULL CHECK (length(scope) BETWEEN 1 AND 512),
            item_kind text NOT NULL CHECK (item_kind ~ '^[a-z_]{1,64}$'),
            native_id text NOT NULL CHECK (length(native_id) BETWEEN 1 AND 256),
            reason_code text NOT NULL CHECK (reason_code ~ '^[a-z_]{1,64}$'),
            reason text NOT NULL CHECK (length(reason) BETWEEN 1 AND 500),
            attempts integer NOT NULL DEFAULT 1 CHECK (attempts > 0),
            first_failed_at timestamptz NOT NULL,
            last_failed_at timestamptz NOT NULL,
            resolved_at timestamptz NULL,
            PRIMARY KEY (source_id, scope, item_kind, native_id),
            CHECK (last_failed_at >= first_failed_at)
        )
        """
    )
    op.execute(
        "CREATE INDEX sync_quarantine_open ON evidence.sync_quarantine "
        "(source_id, scope, item_kind) WHERE resolved_at IS NULL"
    )


def downgrade() -> None:
    """Block removing the operational failure history."""
    raise RuntimeError(
        "The sync quarantine cannot be removed by downgrade; use a forward migration."
    )
