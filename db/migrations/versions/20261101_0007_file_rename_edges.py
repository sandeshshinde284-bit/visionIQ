"""LRN-07 / M-16: file rename edges keep code history across renames.

Revision ID: 20261101_0007
Revises: 20261031_0006
"""

from alembic import op

revision = "20261101_0007"
down_revision = "20261031_0006"
branch_labels = None
depends_on = None

_RELATIONS = (
    "'ticket_pr','pr_commit','commit_file','file_symbol',"
    "'file_dependency','repository_dependency','ticket_commit',"
    "'design_related','ci_commit'"
)


def upgrade() -> None:
    """Allow 'file_renamed' (old path -> new path), a permanent historical fact."""
    op.execute(
        "ALTER TABLE evidence.graph_assertions DROP CONSTRAINT graph_assertions_relation_type_check"
    )
    op.execute(
        "ALTER TABLE evidence.graph_assertions ADD CONSTRAINT "
        f"graph_assertions_relation_type_check CHECK (relation_type IN ({_RELATIONS},"
        "'file_renamed'))"
    )


def downgrade() -> None:
    """Block destructive rollback of immutable relationship history."""
    raise RuntimeError(
        "Graph relationship history cannot be removed by downgrade; use a forward migration."
    )
