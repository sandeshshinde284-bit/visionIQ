"""LRN-10: lesson statuses per master design 6.6 stage 8: candidate, reviewed, disputed, retired.

Replaces the OKF-style names (current, stale) first used; an OKF export maps reviewed -> current and
retired -> stale (6.20).

Revision ID: 20261101_0016
Revises: 20261101_0015
"""

from alembic import op

revision = "20261101_0016"
down_revision = "20261101_0015"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Revisions are append-only, so the rename is only safe while no row uses an old name."""
    op.execute(
        """
        DO $$
        BEGIN
            IF EXISTS (SELECT 1 FROM knowledge.lesson_revisions
                       WHERE status IN ('current', 'stale')) THEN
                RAISE EXCEPTION 'lesson revisions use the old status names; migrate them first';
            END IF;
        END $$;
        ALTER TABLE knowledge.lesson_revisions
            DROP CONSTRAINT lesson_revisions_status_check,
            DROP CONSTRAINT lesson_revisions_check,
            ADD CONSTRAINT lesson_revisions_status_check
                CHECK (status IN ('candidate', 'reviewed', 'disputed', 'retired')),
            -- Never "reviewed" or a verified cause without a named human verifier (6.6, 6.20).
            ADD CONSTRAINT lesson_revisions_review_needs_human
                CHECK ((status <> 'reviewed' AND cause_status <> 'verified')
                       OR verified_by IS NOT NULL)
        """
    )


def downgrade() -> None:
    """Restore the previous names (only while no row uses the new ones)."""
    op.execute(
        """
        ALTER TABLE knowledge.lesson_revisions
            DROP CONSTRAINT lesson_revisions_status_check,
            DROP CONSTRAINT lesson_revisions_review_needs_human,
            ADD CONSTRAINT lesson_revisions_status_check
                CHECK (status IN ('candidate', 'current', 'disputed', 'stale')),
            ADD CONSTRAINT lesson_revisions_check
                CHECK ((status <> 'current' AND cause_status <> 'verified')
                       OR verified_by IS NOT NULL)
        """
    )
