"""LRN-07: CI build -> commit / pull request links.

Revision ID: 20261101_0010
Revises: 20261101_0009
"""

from alembic import op

revision = "20261101_0010"
down_revision = "20261101_0009"
branch_labels = None
depends_on = None

_RELATIONS = (
    "'ticket_pr','pr_commit','commit_file','file_symbol',"
    "'file_dependency','repository_dependency','ticket_commit',"
    "'design_related','ci_commit','file_renamed','file_test',"
    "'tested_by','automated_by','ci_pull_request'"
)


def upgrade() -> None:
    """What each build built, as stated by its newest evidence revision.

    evidence_ref is "evidence:<64-hex item key>:<revision up to 512 chars>" (586 chars at most);
    PostgreSQL regex repetition counts are limited to 255, so the length is checked separately.
    """
    op.execute(
        """
        CREATE TABLE evidence.build_links (
            source_instance text NOT NULL CHECK (length(source_instance) BETWEEN 1 AND 512),
            project_id text NOT NULL CHECK (length(project_id) BETWEEN 1 AND 256),
            build_id text NOT NULL CHECK (build_id ~ '^[0-9]{1,18}$'),
            repository_id text NOT NULL CHECK (length(repository_id) BETWEEN 1 AND 256),
            commit_sha text NULL CHECK (commit_sha ~ '^[0-9a-f]{40}$'),
            pull_request_id text NULL CHECK (pull_request_id ~ '^[0-9]{1,18}$'),
            evidence_ref text NOT NULL
                CHECK (evidence_ref ~ '^evidence:[0-9a-f]{64}:.+$' AND length(evidence_ref) <= 586),
            source_asserted_at timestamptz NOT NULL,
            observed_at timestamptz NOT NULL DEFAULT now(),
            PRIMARY KEY (source_instance, project_id, build_id),
            CHECK (commit_sha IS NOT NULL OR pull_request_id IS NOT NULL)
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
