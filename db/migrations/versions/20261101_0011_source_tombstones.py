"""N-2 tombstones for deleted or unreadable items; N-7 last confirmation of the latest revision.

Revision ID: 20261101_0011
Revises: 20261101_0010
"""

from alembic import op

revision = "20261101_0011"
down_revision = "20261101_0010"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """A tombstoned item is never served; a newer source revision (e.g. a restore) clears it."""
    op.execute(
        """
        ALTER TABLE evidence.source_items
            ADD COLUMN latest_observed_at timestamptz NULL,
            ADD COLUMN deleted_at timestamptz NULL,
            ADD COLUMN deletion_reason text NULL
                CHECK (deletion_reason IN ('deleted_in_source', 'missing_in_source')),
            ADD CONSTRAINT source_items_tombstone_complete
                CHECK ((deleted_at IS NULL) = (deletion_reason IS NULL))
        """
    )


def downgrade() -> None:
    """Block silently un-hiding deleted evidence."""
    raise RuntimeError("Tombstones cannot be removed by downgrade; use a forward migration.")
