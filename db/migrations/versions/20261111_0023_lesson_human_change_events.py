"""LRN-10: let audited human lesson actions use the knowledge outbox.

Revision ID: 20261111_0023
Revises: 20261106_0022

The initial outbox enum permitted automated lesson.created and lesson.revised
but excluded the three event types already emitted by the human-review and
human-fork workflows. Those operations would roll back on a CHECK violation.
"""

from alembic import op

revision = "20261111_0023"
down_revision = "20261106_0022"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Expand the event vocabulary without touching existing outbox rows."""
    op.execute(
        """
        ALTER TABLE knowledge.change_events
            DROP CONSTRAINT change_events_event_type_check,
            ADD CONSTRAINT change_events_event_type_check CHECK (event_type IN (
                'index.item_updated',
                'index.item_removed',
                'lesson.created',
                'lesson.revised',
                'lesson.reviewed',
                'lesson.forked',
                'lesson.observation_resolved',
                'snapshot.published',
                'snapshot.rejected',
                'snapshot.rolled_back'
            ))
        """
    )


def downgrade() -> None:
    """Do not make already-emitted human actions invalid or erase audit history."""
    raise RuntimeError(
        "Human lesson events may already exist; use a forward migration to change event types."
    )
