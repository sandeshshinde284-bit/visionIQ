"""Connector checkpoints for source ingestion (LRN-01, S04-14).

Revision ID: 20261030_0003
Revises: 20261030_0002
"""

from alembic import op

revision = "20261030_0003"
down_revision = "20261030_0002"
branch_labels = None
depends_on = None


def upgrade() -> None:
    # Written by the .NET Ingestor with the API role. The FND-03 default privileges give the API
    # role read-write access and the Intelligence role read access to new tables in `evidence`.
    op.execute(
        """
        CREATE TABLE IF NOT EXISTS evidence.connector_checkpoints (
            source_id    text        NOT NULL,
            scope        text        NOT NULL,
            cursor_value text        NOT NULL,
            updated_at   timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (source_id, scope)
        )
        """
    )


def downgrade() -> None:
    op.execute("DROP TABLE IF EXISTS evidence.connector_checkpoints")
