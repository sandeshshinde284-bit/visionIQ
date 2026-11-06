"""Merge the BRF-01 and LRN-04 review migration branches into a single head.

Revision ID: 20261106_0022
Revises: 20261105_0019, 20261105_0021

PR #12 (BRF-01 ticket pickup, 20261105_0019) and PR #13 (LRN-04 review, 20261103_0018 ..
20261105_0021) both branched from earlier heads, leaving two heads on dev, so
`alembic upgrade head` (and therefore db-migrate) failed. This merge joins them without
changing any schema or rewriting a migration that may already have been applied.
"""

revision = "20261106_0022"
down_revision = ("20261105_0019", "20261105_0021")
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Nothing to change: this revision only joins the two branches."""


def downgrade() -> None:
    """Nothing to change: this revision only joins the two branches."""
