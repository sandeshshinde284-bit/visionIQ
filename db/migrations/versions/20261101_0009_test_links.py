"""LRN-07: declared test associations (ADO "Tested By") and Test Case automation.

Revision ID: 20261101_0009
Revises: 20261101_0008
"""

from alembic import op

revision = "20261101_0009"
down_revision = "20261101_0008"
branch_labels = None
depends_on = None

_RELATIONS = (
    "'ticket_pr','pr_commit','commit_file','file_symbol',"
    "'file_dependency','repository_dependency','ticket_commit',"
    "'design_related','ci_commit','file_renamed','file_test',"
    "'tested_by','automated_by'"
)


def upgrade() -> None:
    """Current declared test links per work item, and each Test Case's automated test identity.

    test_link_sources records the newest source revision applied per work item, so an older replay
    can never resurrect a link that a newer revision removed.

    evidence_ref is "evidence:<64-hex item key>:<revision up to 512 chars>" (586 chars at most);
    PostgreSQL regex repetition counts are limited to 255, so the length is checked separately.
    """
    op.execute(
        """
        CREATE TABLE evidence.test_link_sources (
            source_instance text NOT NULL CHECK (length(source_instance) BETWEEN 1 AND 512),
            project_id text NOT NULL CHECK (length(project_id) BETWEEN 1 AND 256),
            work_item_id text NOT NULL CHECK (work_item_id ~ '^[0-9]{1,18}$'),
            source_asserted_at timestamptz NOT NULL,
            PRIMARY KEY (source_instance, project_id, work_item_id)
        )
        """
    )
    op.execute(
        """
        CREATE TABLE evidence.tested_by_links (
            source_instance text NOT NULL CHECK (length(source_instance) BETWEEN 1 AND 512),
            project_id text NOT NULL CHECK (length(project_id) BETWEEN 1 AND 256),
            work_item_id text NOT NULL CHECK (work_item_id ~ '^[0-9]{1,18}$'),
            test_case_id text NOT NULL CHECK (test_case_id ~ '^[0-9]{1,18}$'),
            evidence_ref text NOT NULL
                CHECK (evidence_ref ~ '^evidence:[0-9a-f]{64}:.+$' AND length(evidence_ref) <= 586),
            source_asserted_at timestamptz NOT NULL,
            observed_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (source_instance, project_id, work_item_id, test_case_id)
        )
        """
    )
    op.execute(
        """
        CREATE TABLE evidence.test_case_automation (
            source_instance text NOT NULL CHECK (length(source_instance) BETWEEN 1 AND 512),
            project_id text NOT NULL CHECK (length(project_id) BETWEEN 1 AND 256),
            test_case_id text NOT NULL CHECK (test_case_id ~ '^[0-9]{1,18}$'),
            automated_test_name text NOT NULL
                CHECK (length(automated_test_name) BETWEEN 1 AND 1024),
            automated_test_storage text NULL CHECK (length(automated_test_storage) <= 1024),
            evidence_ref text NOT NULL
                CHECK (evidence_ref ~ '^evidence:[0-9a-f]{64}:.+$' AND length(evidence_ref) <= 586),
            source_asserted_at timestamptz NOT NULL,
            observed_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (source_instance, project_id, test_case_id)
        )
        """
    )
    op.execute(
        "ALTER TABLE evidence.graph_assertions DROP CONSTRAINT graph_assertions_relation_type_check"
    )
    op.execute(
        "ALTER TABLE evidence.graph_assertions ADD CONSTRAINT "
        f"graph_assertions_relation_type_check CHECK (relation_type IN ({_RELATIONS}))"
    )


def downgrade() -> None:
    """Block destructive rollback of relationship history."""
    raise RuntimeError(
        "Graph relationship history cannot be removed by downgrade; use a forward migration."
    )
