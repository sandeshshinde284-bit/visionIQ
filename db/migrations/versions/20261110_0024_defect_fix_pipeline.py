"""DEF-01 to DEF-06: the fix pipeline's stored results and runner executions.

Intelligence (knowledge schema) owns what it produces: one immutable fix result per deep run and
one row per runner execution behind it. The API reads the result by its id to open the draft
pull request; pull-request delivery state belongs to the workflow schema and comes with that
story (FND-03: each service writes only its own schema).

Revision ID: 20261110_0024
Revises: 20261110_0023
"""

from alembic import op

revision = "20261110_0024"
down_revision = "20261110_0023"
branch_labels = None
depends_on = None

_OUTCOMES = (
    "'PASS', 'FAIL_TARGET_SIGNATURE', 'FAIL_DIFFERENT_SIGNATURE', 'FLAKY', "
    "'ENVIRONMENT_ERROR', 'TIMEOUT', 'NOT_RUN', 'UNAVAILABLE', 'INCONCLUSIVE'"
)


def upgrade() -> None:
    # `document` is the full result (schema in contracts/fixes/fix-result.v1.schema.json); the
    # columns beside it are for queries and reports.
    op.execute(
        """
        CREATE TABLE knowledge.fix_runs (
            fix_id uuid PRIMARY KEY,
            run_id uuid NOT NULL UNIQUE REFERENCES knowledge.analysis_requests (run_id),
            tenant_id text NOT NULL,
            project_id text NOT NULL,
            ticket_id text NOT NULL,
            ticket_revision integer NOT NULL CHECK (ticket_revision >= 1),
            repository_id text NOT NULL,
            base_commit_sha text NOT NULL,
            candidate_commit_sha text NULL CHECK (candidate_commit_sha ~ '^[0-9a-f]{40,64}$'),
            -- The content identity of the candidate: results apply to any commit with this tree.
            candidate_tree_sha text NULL CHECK (candidate_tree_sha ~ '^[0-9a-f]{40,64}$'),
            state text NOT NULL CHECK (state IN ('PUBLISHED', 'PARTIAL', 'BLOCKED')),
            verdict text NOT NULL
                CHECK (verdict IN ('VERIFIED', 'FAIL', 'INCONCLUSIVE', 'NEEDS_REVIEW', 'BLOCKED')),
            reason_codes text[] NOT NULL DEFAULT '{}',
            reproducibility text NOT NULL CHECK (reproducibility IN (
                'REPRODUCED', 'NOT_REPRODUCED', 'UNAVAILABLE', 'FLAKY', 'ENVIRONMENT_ERROR',
                'INCONCLUSIVE')),
            gates_overall text NOT NULL CHECK (gates_overall IN ('GREEN', 'RED', 'NOT_GREEN')),
            schema_version integer NOT NULL CHECK (schema_version >= 1),
            document jsonb NOT NULL CHECK (jsonb_typeof(document) = 'object'),
            policy_revision text NULL,
            routing_revision text NOT NULL,
            accepted_at timestamptz NOT NULL,
            total_ms integer NOT NULL CHECK (total_ms >= 0),
            created_at timestamptz NOT NULL DEFAULT now(),
            -- Only a verified fix is PUBLISHED, and only a reproduced defect can be verified.
            CHECK ((state = 'PUBLISHED') = (verdict = 'VERIFIED')),
            CHECK (verdict <> 'VERIFIED'
                   OR (reproducibility = 'REPRODUCED' AND candidate_commit_sha IS NOT NULL)),
            -- A PARTIAL or BLOCKED result always says why.
            CHECK (state = 'PUBLISHED' OR cardinality(reason_codes) >= 1)
        );
        CREATE INDEX fix_runs_ticket_idx
            ON knowledge.fix_runs (project_id, ticket_id, created_at DESC);

        CREATE FUNCTION knowledge.reject_fix_change() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'fix results are immutable; a correction is a new run';
        END;
        $$;
        CREATE TRIGGER fix_runs_immutable BEFORE UPDATE OR DELETE ON knowledge.fix_runs
            FOR EACH ROW EXECUTE FUNCTION knowledge.reject_fix_change()
        """
    )
    # 8.6.1: every result keeps the commit it ran on, the command's exit code and its log.
    op.execute(
        f"""
        CREATE TABLE knowledge.fix_executions (
            fix_id uuid NOT NULL REFERENCES knowledge.fix_runs (fix_id),
            execution_id text NOT NULL CHECK (execution_id ~ '^X[0-9]{{1,4}}$'),
            manifest_id text NOT NULL CHECK (manifest_id ~ '^M[0-9]{{1,3}}$'),
            purpose text NOT NULL CHECK (purpose IN (
                'reproduction', 'candidate', 'regression', 'suite', 'benchmark')),
            code text NOT NULL CHECK (code IN ('baseline', 'candidate')),
            commit_sha text NOT NULL,
            outcome text NOT NULL CHECK (outcome IN ({_OUTCOMES})),
            test_id text NULL CHECK (length(test_id) <= 200),
            exit_code integer NULL,
            timed_out boolean NOT NULL DEFAULT false,
            duration_ms integer NOT NULL CHECK (duration_ms >= 0),
            log_uri text NOT NULL CHECK (length(log_uri) BETWEEN 1 AND 2000),
            log_sha256 text NOT NULL CHECK (log_sha256 ~ '^[0-9a-f]{{64}}$'),
            started_at timestamptz NOT NULL,
            PRIMARY KEY (fix_id, execution_id)
        );
        CREATE TRIGGER fix_executions_immutable
            BEFORE UPDATE OR DELETE ON knowledge.fix_executions
            FOR EACH ROW EXECUTE FUNCTION knowledge.reject_fix_change()
        """
    )


def downgrade() -> None:
    raise RuntimeError(
        "Fix results and their executions are audit history; use a forward migration."
    )
