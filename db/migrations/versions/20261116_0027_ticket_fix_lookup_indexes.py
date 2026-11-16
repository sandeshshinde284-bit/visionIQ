"""Phase 2 ingestion performance: index the two ticket-fix lookups that scanned whole tables.

Revision ID: 20261116_0027
Revises: 20261112_0026

1. The revert-state UPDATE in PostgresTicketFixLinkStore.ApplyRevertStateAsync filters on
   lower(commit_sha). The existing ticket_fix_links_commit_idx is on plain commit_sha and cannot
   serve it, so every call scanned the whole table. Measured on 62k rows: 49.4 ms per UPDATE as a
   sequential scan, 1.7 ms with this index. It runs for every distinct commit of every pull request.
   commit_sha is stored as the source sent it, so the query keeps lower(); this expression index
   matches it.
2. FindAffectedCommitsAsync filters ticket_fix_revert_observations on revert_commit_sha; the
   existing lookup index covers original_commit_sha only.

Indexes only: no table, row or permission changes. Built inside the migration transaction (a short
write lock on these two tables while each index is created; ingestion should not run during a
migration anyway).
"""

from alembic import op

revision = "20261116_0027"
down_revision = "20261112_0026"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.execute(
        """
        CREATE INDEX IF NOT EXISTS ticket_fix_links_commit_lower_idx
        ON evidence.ticket_fix_links
            (source_instance, project_id, repository_id, lower(commit_sha))
        """
    )
    op.execute(
        """
        CREATE INDEX IF NOT EXISTS ticket_fix_revert_by_revert_idx
        ON evidence.ticket_fix_revert_observations
            (source_instance, project_id, repository_id, revert_commit_sha)
        """
    )


def downgrade() -> None:
    # Indexes hold no data: dropping them only removes the speed-up.
    op.execute("DROP INDEX IF EXISTS evidence.ticket_fix_revert_by_revert_idx")
    op.execute("DROP INDEX IF EXISTS evidence.ticket_fix_links_commit_lower_idx")
