"""LRN-10: what each fix changed, and the lesson lifecycle (master design 6.10, 8.9, 6.20).

Revision ID: 20261101_0013
Revises: 20261101_0012
"""

from alembic import op

revision = "20261101_0013"
down_revision = "20261101_0012"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Fix change sets (written by ingestion) and append-only lessons (written by intelligence)."""
    # The files a fix CHANGED (graph commit_file edges mean "contains", not "changed").
    op.execute(
        """
        CREATE TABLE evidence.fix_change_sets (
            source_instance text NOT NULL CHECK (source_instance ~ '^https://'),
            project_id text NOT NULL CHECK (length(project_id) BETWEEN 1 AND 256),
            repository_id text NOT NULL CHECK (length(repository_id) BETWEEN 1 AND 128),
            fix_kind text NOT NULL CHECK (fix_kind IN ('pull_request', 'commit')),
            fix_id text NOT NULL CHECK (length(fix_id) BETWEEN 1 AND 128),
            paths text[] NOT NULL CHECK (cardinality(paths) <= 50000),
            truncated boolean NOT NULL DEFAULT false,
            provenance_item_key character(64) NOT NULL,
            observed_at timestamptz NOT NULL,
            PRIMARY KEY (source_instance, project_id, repository_id, fix_kind, fix_id)
        )
        """
    )
    op.execute(
        """
        CREATE TABLE knowledge.lesson_observations (
            observation_key character(64) PRIMARY KEY CHECK (observation_key ~ '^[0-9a-f]{64}$'),
            association_key text NOT NULL UNIQUE,
            source_instance text NOT NULL,
            project_id text NOT NULL,
            repository_id text NOT NULL,
            ticket_id text NOT NULL,
            fix_kind text NOT NULL CHECK (fix_kind IN ('pull_request', 'commit')),
            fix_id text NOT NULL,
            paths text[] NOT NULL CHECK (cardinality(paths) BETWEEN 1 AND 50000),
            recorded_at timestamptz NOT NULL
        );
        CREATE TABLE knowledge.lesson_patterns (
            pattern_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
            source_instance text NOT NULL,
            project_id text NOT NULL,
            repository_id text NOT NULL,
            latest_revision integer NOT NULL CHECK (latest_revision > 0),
            created_at timestamptz NOT NULL
        );
        CREATE TABLE knowledge.lesson_revisions (
            pattern_id uuid NOT NULL REFERENCES knowledge.lesson_patterns (pattern_id),
            revision integer NOT NULL CHECK (revision > 0),
            status text NOT NULL CHECK (status IN ('candidate', 'current', 'disputed', 'stale')),
            cause_status text NOT NULL CHECK (cause_status IN ('unverified', 'verified')),
            title text NOT NULL CHECK (length(title) BETWEEN 1 AND 300),
            signature_paths text[] NOT NULL CHECK (cardinality(signature_paths) >= 1),
            observation_keys text[] NOT NULL CHECK (cardinality(observation_keys) >= 2),
            decision text NOT NULL CHECK (decision IN
                ('CREATE_NEW', 'LINK_EXISTING', 'REVISE_EXISTING', 'FORK_BY_SCOPE')),
            rationale text NOT NULL CHECK (length(rationale) BETWEEN 1 AND 2000),
            generated_by text NOT NULL,
            verified_by text NULL,
            okf_path text NULL,
            okf_revision text NULL,
            created_at timestamptz NOT NULL,
            PRIMARY KEY (pattern_id, revision),
            -- OKF rule: never "current" or "verified" without a named human verifier.
            CHECK ((status <> 'current' AND cause_status <> 'verified') OR verified_by IS NOT NULL)
        );
        CREATE TABLE knowledge.lesson_memberships (
            observation_key character(64) PRIMARY KEY
                REFERENCES knowledge.lesson_observations (observation_key),
            pattern_id uuid NOT NULL REFERENCES knowledge.lesson_patterns (pattern_id),
            joined_at timestamptz NOT NULL
        );
        CREATE TABLE knowledge.lesson_decisions (
            decision_id bigserial PRIMARY KEY,
            observation_key character(64) NOT NULL
                REFERENCES knowledge.lesson_observations (observation_key),
            decision text NOT NULL CHECK (decision IN ('CREATE_NEW', 'LINK_EXISTING',
                'REVISE_EXISTING', 'FORK_BY_SCOPE', 'HUMAN_REVIEW', 'WAIT_FOR_EVIDENCE')),
            pattern_ids uuid[] NOT NULL DEFAULT '{}',
            rationale text NOT NULL CHECK (length(rationale) BETWEEN 1 AND 2000),
            decided_at timestamptz NOT NULL
        )
        """
    )
    # Lesson history is append-only: revisions can never be edited or removed.
    op.execute(
        """
        CREATE FUNCTION knowledge.reject_lesson_revision_change() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'lesson revisions are append-only';
        END;
        $$;
        CREATE TRIGGER lesson_revisions_append_only
            BEFORE UPDATE OR DELETE ON knowledge.lesson_revisions
            FOR EACH ROW EXECUTE FUNCTION knowledge.reject_lesson_revision_change()
        """
    )


def downgrade() -> None:
    """Block deleting lesson history."""
    raise RuntimeError("Lesson history cannot be removed by downgrade; use a forward migration.")
