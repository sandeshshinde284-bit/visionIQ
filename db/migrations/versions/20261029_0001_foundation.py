"""Initial schemas and vector extension.

Revision ID: 20261029_0001
Revises:
"""

from alembic import op

revision = "20261029_0001"
down_revision = None
branch_labels = None
depends_on = None


def upgrade() -> None:
    # Works with an existing FND-02 Docker volume as well as a fresh checkout.
    op.execute("CREATE EXTENSION IF NOT EXISTS vector")
    op.execute("CREATE SCHEMA IF NOT EXISTS workflow")
    op.execute("CREATE SCHEMA IF NOT EXISTS evidence")
    op.execute("CREATE SCHEMA IF NOT EXISTS knowledge")


def downgrade() -> None:
    raise RuntimeError("Foundation schemas cannot be dropped automatically: possible data loss.")
