"""LRN-04: keep source-observed reverts before ticket/fix associations arrive.

Revision ID: 20261103_0018
Revises: 20261101_0017

Only full 40-character Git SHA-1 references are accepted by the .NET writer.
Historical revert markers in ticket_fix_links remain unchanged.
"""

from alembic import op

revision = "20261103_0018"
down_revision = "20261101_0017"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Persist scoped revert observations independently of event arrival order."""
    op.execute(
        """
        CREATE TABLE evidence.ticket_fix_revert_observations (
            source_instance text NOT NULL
                CHECK (length(source_instance) BETWEEN 1 AND 1024),
            project_id text NOT NULL
                CHECK (length(project_id) BETWEEN 1 AND 256),
            repository_id text NOT NULL
                CHECK (length(repository_id) BETWEEN 1 AND 256),
            original_commit_sha text NOT NULL
                CHECK (original_commit_sha ~ '^[0-9a-f]{40}$'),
            revert_commit_sha text NOT NULL
                CHECK (revert_commit_sha ~ '^[0-9a-f]{40}$'),
            first_seen_at timestamptz NOT NULL DEFAULT now(),
            last_seen_at timestamptz NOT NULL DEFAULT now(),
            CHECK (original_commit_sha <> revert_commit_sha),
            PRIMARY KEY (
                source_instance, project_id, repository_id,
                original_commit_sha, revert_commit_sha
            )
        )
        """
    )
    op.execute(
        """
        CREATE INDEX ticket_fix_revert_lookup_idx
        ON evidence.ticket_fix_revert_observations(
            source_instance, project_id, repository_id, original_commit_sha
        )
        """
    )


def downgrade() -> None:
    """Do not silently delete immutable source-observed revert evidence."""
    raise RuntimeError(
        "LRN-04 revert observations are audit evidence; use a reviewed forward migration."
    )
