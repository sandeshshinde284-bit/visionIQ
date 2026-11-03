"""LRN-07 versioned, source-permission-bound engineering relationship graph.

Revision ID: 20261031_0006
Revises: 20261030_0005
"""

from alembic import op

revision = "20261031_0006"
down_revision = "20261030_0005"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Create typed nodes and immutable revision-provenance edge assertions."""
    op.execute(
        """
        CREATE TABLE evidence.graph_nodes (
            node_key char(64) PRIMARY KEY
                CHECK (node_key ~ '^[0-9a-f]{64}$'),
            anchor_item_key char(64) NOT NULL
                REFERENCES evidence.source_items(item_key) ON DELETE RESTRICT,
            node_kind text NOT NULL CHECK (node_kind IN (
                'work_item','pull_request','commit','repository',
                'file','symbol','design_page','ci_run'
            )),
            native_id text NOT NULL CHECK (length(native_id) BETWEEN 1 AND 2048),
            created_at timestamptz NOT NULL DEFAULT now()
        )
        """
    )
    op.execute(
        """
        CREATE TABLE evidence.graph_assertions (
            assertion_key char(64) PRIMARY KEY
                CHECK (assertion_key ~ '^[0-9a-f]{64}$'),
            relationship_key char(64) NOT NULL
                CHECK (relationship_key ~ '^[0-9a-f]{64}$'),
            from_node_key char(64) NOT NULL
                REFERENCES evidence.graph_nodes(node_key) ON DELETE RESTRICT,
            to_node_key char(64) NOT NULL
                REFERENCES evidence.graph_nodes(node_key) ON DELETE RESTRICT,
            relation_type text NOT NULL CHECK (relation_type IN (
                'ticket_pr','pr_commit','commit_file','file_symbol',
                'file_dependency','repository_dependency','ticket_commit',
                'design_related','ci_commit'
            )),
            classification text NOT NULL
                CHECK (classification IN ('explicit','inferred','verified')),
            assertion_state text NOT NULL
                CHECK (assertion_state IN ('active','disputed','retracted')),
            provenance_item_key char(64) NOT NULL,
            provenance_revision_id text NOT NULL
                CHECK (length(provenance_revision_id) BETWEEN 1 AND 512),
            source_asserted_at timestamptz NOT NULL,
            observed_at timestamptz NOT NULL DEFAULT now(),
            content_sha256 char(64) NOT NULL
                CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
            ticket_fix_association_key text NULL
                REFERENCES evidence.ticket_fix_links(association_key) ON DELETE RESTRICT,
            FOREIGN KEY (provenance_item_key, provenance_revision_id)
                REFERENCES evidence.source_revisions(item_key, provider_revision_id)
                ON DELETE RESTRICT,
            CHECK (from_node_key <> to_node_key)
        )
        """
    )
    op.execute(
        """
        CREATE INDEX graph_assertions_from_latest_idx
        ON evidence.graph_assertions(from_node_key, relationship_key, source_asserted_at DESC)
        """
    )
    op.execute(
        """
        CREATE INDEX graph_assertions_to_latest_idx
        ON evidence.graph_assertions(to_node_key, relationship_key, source_asserted_at DESC)
        """
    )
    op.execute(
        """
        CREATE INDEX graph_assertions_fix_assoc_idx
        ON evidence.graph_assertions(ticket_fix_association_key)
        WHERE ticket_fix_association_key IS NOT NULL
        """
    )
    op.execute(
        """
        CREATE INDEX graph_nodes_anchor_idx
        ON evidence.graph_nodes(anchor_item_key)
        """
    )


def downgrade() -> None:
    """Block destructive rollback of immutable relationship history."""
    raise RuntimeError(
        "LRN-07 graph audit history cannot be removed by downgrade; use a forward migration."
    )
