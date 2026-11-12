"""DEF-05 / DEF-06: delivery of a fix result as a draft pull request.

The API (workflow schema) owns delivery: one row per deep run, from "queued" to the pull request
that was opened, the commit its results apply to, and the head last seen on it. Intelligence owns
the fix result itself (knowledge.fix_runs); the API reads it by its result id (FND-03).

Revision ID: 20261111_0025
Revises: 20261110_0024
"""

from alembic import op

revision = "20261111_0025"
down_revision = "20261110_0024"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.execute(
        """
        CREATE TABLE workflow.fix_pull_requests (
            run_id uuid PRIMARY KEY REFERENCES workflow.analysis_runs (run_id),
            result_id uuid NOT NULL,
            result_state text NOT NULL CHECK (result_state IN ('PUBLISHED', 'PARTIAL', 'BLOCKED')),
            -- skipped: the result has no pull request to open (not verified, blocked, in review).
            status text NOT NULL DEFAULT 'pending'
                CHECK (status IN ('pending', 'published', 'skipped', 'failed')),
            -- 'record' keeps what would have been sent here and calls nothing external.
            mode text NULL CHECK (mode IN ('record', 'ado')),
            repository_id text NULL CHECK (length(repository_id) BETWEEN 1 AND 256),
            pull_request_id text NULL CHECK (length(pull_request_id) BETWEEN 1 AND 200),
            source_branch text NULL CHECK (length(source_branch) BETWEEN 1 AND 400),
            -- The commit on the pull request that carries the tested content (S11-17): the sandbox
            -- results apply to this commit and to no other.
            verified_head_sha text NULL CHECK (verified_head_sha ~ '^[0-9a-f]{40,64}$'),
            -- The head last reported for the pull request. When it differs, the checks are stale.
            current_head_sha text NULL CHECK (current_head_sha ~ '^[0-9a-f]{40,64}$'),
            head_changed_at timestamptz NULL,
            -- The statuses last posted: [{"name", "state", "description"}].
            checks jsonb NULL CHECK (checks IS NULL OR jsonb_typeof(checks) = 'array'),
            request_sha256 text NULL CHECK (request_sha256 ~ '^[0-9a-f]{64}$'),
            request_body text NULL,
            reason text NULL CHECK (reason ~ '^[a-z_]{1,64}$'),
            attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            available_at timestamptz NOT NULL DEFAULT now(),
            created_at timestamptz NOT NULL DEFAULT now(),
            published_at timestamptz NULL,
            CHECK ((status = 'published') = (published_at IS NOT NULL)),
            CHECK (status <> 'published'
                   OR (pull_request_id IS NOT NULL AND verified_head_sha IS NOT NULL))
        );
        CREATE INDEX fix_pull_requests_pending_idx ON workflow.fix_pull_requests (available_at)
            WHERE status = 'pending';
        CREATE INDEX fix_pull_requests_opened_idx
            ON workflow.fix_pull_requests (pull_request_id, repository_id)
            WHERE status = 'published'
        """
    )


def downgrade() -> None:
    raise RuntimeError("Pull request delivery records are audit history; use a forward migration.")
