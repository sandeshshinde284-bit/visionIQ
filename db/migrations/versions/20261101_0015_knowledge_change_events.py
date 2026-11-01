"""LRN-13 (S12-15): transactional outbox of knowledge.changed events.

Revision ID: 20261101_0015
Revises: 20261101_0014
"""

from alembic import op

revision = "20261101_0015"
down_revision = "20261101_0014"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Written in the same transaction as each change; a publisher drains it to Pub/Sub."""
    op.execute(
        """
        CREATE TABLE knowledge.change_events (
            event_id bigserial PRIMARY KEY,
            event_type text NOT NULL CHECK (event_type IN (
                'index.item_updated', 'index.item_removed', 'lesson.created', 'lesson.revised')),
            subject text NOT NULL CHECK (length(subject) BETWEEN 1 AND 128),
            payload jsonb NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
            occurred_at timestamptz NOT NULL DEFAULT now(),
            published_at timestamptz NULL
        );
        CREATE INDEX change_events_unpublished ON knowledge.change_events (event_id)
            WHERE published_at IS NULL
        """
    )


def downgrade() -> None:
    """The outbox can be recreated; unpublished events would be lost, so refuse."""
    raise RuntimeError("Drop knowledge.change_events only after every event is published.")
