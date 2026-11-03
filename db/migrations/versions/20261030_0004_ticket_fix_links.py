"""LRN-04: source-scoped ticket-to-fix associations and immutable review decisions.

Revision ID: 20261030_0004
Revises: 20261030_0003

Schema changes are maintained by Alembic, never the runtime .NET service.
"""

from alembic import op

revision = "20261030_0004"
down_revision = "20261030_0003"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Create source-observed association and immutable human-review tables."""
    op.execute(
        """
        CREATE TABLE evidence.ticket_fix_links (
            association_key text PRIMARY KEY
                CHECK (association_key ~ '^[0-9a-f]{64}$'),
            source_instance text NOT NULL CHECK (length(source_instance) BETWEEN 1 AND 1024),
            project_id text NOT NULL CHECK (length(project_id) BETWEEN 1 AND 256),
            ticket_id text NOT NULL CHECK (length(ticket_id) BETWEEN 1 AND 256),
            repository_id text NULL,
            pull_request_id text NULL,
            commit_sha text NULL,
            source_classification text NOT NULL
                CHECK (source_classification IN ('explicit', 'inferred', 'unknown')),
            review_status text NULL
                CHECK (review_status IN ('verified', 'disputed')),
            source_evidence_refs jsonb NOT NULL DEFAULT '[]'::jsonb
                CHECK (jsonb_typeof(source_evidence_refs) = 'array'),
            revert_commit_shas jsonb NOT NULL DEFAULT '[]'::jsonb
                CHECK (jsonb_typeof(revert_commit_shas) = 'array'),
            first_seen_at timestamptz NOT NULL DEFAULT now(),
            last_seen_at timestamptz NOT NULL DEFAULT now(),
            CHECK (pull_request_id IS NOT NULL OR commit_sha IS NOT NULL)
        )
        """
    )
    op.execute(
        """
        CREATE TABLE evidence.ticket_fix_reviews (
            review_id uuid PRIMARY KEY,
            association_key text NOT NULL
                REFERENCES evidence.ticket_fix_links(association_key) ON DELETE RESTRICT,
            review_reference text NOT NULL CHECK (length(review_reference) BETWEEN 1 AND 1024),
            result text NOT NULL CHECK (result IN ('verified', 'disputed')),
            recorded_at timestamptz NOT NULL DEFAULT now(),
            UNIQUE (association_key, review_reference)
        )
        """
    )
    op.execute(
        """
        CREATE INDEX ticket_fix_links_ticket_idx
        ON evidence.ticket_fix_links(source_instance, project_id, ticket_id)
        """
    )
    op.execute(
        """
        CREATE INDEX ticket_fix_links_commit_idx
        ON evidence.ticket_fix_links(source_instance, project_id, repository_id, commit_sha)
        WHERE commit_sha IS NOT NULL
        """
    )


def downgrade() -> None:
    """Block destructive rollback of audited link and review history."""
    raise RuntimeError(
        "LRN-04 ticket-to-fix review history is immutable; use a reviewed forward migration."
    )
