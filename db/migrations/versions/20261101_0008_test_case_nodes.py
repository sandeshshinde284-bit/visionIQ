"""LRN-07: test-case nodes and file -> test edges ("defect -> fix -> test").

Revision ID: 20261101_0008
Revises: 20261101_0007
"""

from alembic import op

revision = "20261101_0008"
down_revision = "20261101_0007"
branch_labels = None
depends_on = None

_NODE_KINDS = (
    "'work_item','pull_request','commit','repository',"
    "'file','symbol','design_page','ci_run','test_case'"
)
_RELATIONS = (
    "'ticket_pr','pr_commit','commit_file','file_symbol',"
    "'file_dependency','repository_dependency','ticket_commit',"
    "'design_related','ci_commit','file_renamed','file_test'"
)


def upgrade() -> None:
    """Allow node kind 'test_case' and relation 'file_test'."""
    op.execute("ALTER TABLE evidence.graph_nodes DROP CONSTRAINT graph_nodes_node_kind_check")
    op.execute(
        "ALTER TABLE evidence.graph_nodes ADD CONSTRAINT "
        f"graph_nodes_node_kind_check CHECK (node_kind IN ({_NODE_KINDS}))"
    )
    op.execute(
        "ALTER TABLE evidence.graph_assertions DROP CONSTRAINT graph_assertions_relation_type_check"
    )
    op.execute(
        "ALTER TABLE evidence.graph_assertions ADD CONSTRAINT "
        f"graph_assertions_relation_type_check CHECK (relation_type IN ({_RELATIONS}))"
    )


def downgrade() -> None:
    """Block destructive rollback of immutable relationship history."""
    raise RuntimeError(
        "Graph relationship history cannot be removed by downgrade; use a forward migration."
    )
