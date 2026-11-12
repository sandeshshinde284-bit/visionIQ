"""BRF-02 to BRF-08: the brief pipeline, its delivery to the ticket, advice ratings and hotspots.

Intelligence (knowledge schema) owns what it produces: the request queue, the immutable brief with
its advice and model-call log, and hotspot sweeps. The API (workflow schema) owns delivery: one
publication per run and the engineers' advice ratings. Each service still writes only its own
schema (FND-03); the API reads the stored brief by its result id.

Revision ID: 20261110_0023
Revises: 20261106_0022
"""

from alembic import op

revision = "20261110_0023"
down_revision = "20261106_0022"
branch_labels = None
depends_on = None

_STATES = "'PUBLISHED', 'PARTIAL', 'BLOCKED'"


def upgrade() -> None:
    # BRF-03: a request is now accepted first and answered by a worker, so the result columns
    # are empty while it is pending. Rows that existed before this migration are all answered.
    op.execute(
        """
        ALTER TABLE knowledge.analysis_requests
            ALTER COLUMN result_id DROP NOT NULL,
            ALTER COLUMN result_state DROP NOT NULL,
            ADD COLUMN status text NOT NULL DEFAULT 'completed'
                CHECK (status IN ('pending', 'completed')),
            -- The accepted analysis.requested envelope (ids only, no ticket text); the worker
            -- builds analysis.completed from it.
            ADD COLUMN request_event jsonb NULL
                CHECK (request_event IS NULL OR jsonb_typeof(request_event) = 'object'),
            ADD COLUMN attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            ADD COLUMN available_at timestamptz NOT NULL DEFAULT now(),
            ADD COLUMN completed_at timestamptz NULL,
            ADD CONSTRAINT analysis_requests_answered CHECK (
                status = 'pending' OR (result_id IS NOT NULL AND result_state IS NOT NULL));
        UPDATE knowledge.analysis_requests SET completed_at = received_at;
        CREATE INDEX analysis_requests_pending_idx
            ON knowledge.analysis_requests (available_at) WHERE status = 'pending'
        """
    )
    # BRF-03 / BRF-04: one immutable brief per run. `document` is the full brief (schema in
    # contracts/briefs/brief.v1.schema.json); the columns beside it are for queries and reports.
    op.execute(
        f"""
        CREATE TABLE knowledge.briefs (
            brief_id uuid PRIMARY KEY,
            run_id uuid NOT NULL UNIQUE REFERENCES knowledge.analysis_requests (run_id),
            tenant_id text NOT NULL,
            project_id text NOT NULL,
            ticket_id text NOT NULL,
            ticket_revision integer NOT NULL CHECK (ticket_revision >= 1),
            state text NOT NULL CHECK (state IN ({_STATES})),
            reason_codes text[] NOT NULL DEFAULT '{{}}',
            risk_band text NOT NULL
                CHECK (risk_band IN ('LOW', 'MODERATE', 'HIGH', 'INSUFFICIENT_DATA')),
            schema_version integer NOT NULL CHECK (schema_version >= 1),
            document jsonb NOT NULL CHECK (jsonb_typeof(document) = 'object'),
            snapshot_id text NULL,
            routing_revision text NOT NULL,
            -- BRF-07 stage timings; the pickup-to-comment time is on workflow.brief_publications.
            accepted_at timestamptz NOT NULL,
            retrieval_ms integer NOT NULL CHECK (retrieval_ms >= 0),
            generation_ms integer NOT NULL CHECK (generation_ms >= 0),
            total_ms integer NOT NULL CHECK (total_ms >= 0),
            created_at timestamptz NOT NULL DEFAULT now(),
            -- A PARTIAL or BLOCKED brief always says why.
            CHECK (state = 'PUBLISHED' OR cardinality(reason_codes) >= 1)
        );
        CREATE INDEX briefs_ticket_idx ON knowledge.briefs (project_id, ticket_id, created_at DESC);

        -- BRF-04: each advice item has its own id within the brief, so it can be rated later.
        CREATE TABLE knowledge.brief_advice (
            brief_id uuid NOT NULL REFERENCES knowledge.briefs (brief_id),
            advice_id text NOT NULL CHECK (advice_id ~ '^ADV-[0-9]{{3}}$'),
            kind text NOT NULL CHECK (kind IN ('RISK', 'DO', 'DONT', 'APPROACH')),
            statement text NOT NULL CHECK (length(statement) BETWEEN 1 AND 1000),
            evidence_ids text[] NOT NULL CHECK (cardinality(evidence_ids) >= 1),
            evidence_state text NOT NULL DEFAULT 'UNVERIFIED'
                CHECK (evidence_state IN ('UNVERIFIED', 'DISPUTED')),
            PRIMARY KEY (brief_id, advice_id)
        );

        CREATE FUNCTION knowledge.reject_brief_change() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'briefs are immutable; a correction is a new run';
        END;
        $$;
        CREATE TRIGGER briefs_immutable BEFORE UPDATE OR DELETE ON knowledge.briefs
            FOR EACH ROW EXECUTE FUNCTION knowledge.reject_brief_change();
        CREATE TRIGGER brief_advice_immutable BEFORE UPDATE OR DELETE ON knowledge.brief_advice
            FOR EACH ROW EXECUTE FUNCTION knowledge.reject_brief_change()
        """
    )
    # BRF-02: the model calls behind each stored brief, whatever their outcome (calls of an
    # attempt that was abandoned are in the service log only). No prompt or response text.
    op.execute(
        """
        CREATE TABLE knowledge.llm_calls (
            invocation_id uuid PRIMARY KEY,
            run_id uuid NULL,
            task text NOT NULL CHECK (task ~ '^[a-z_]{1,64}$'),
            role text NOT NULL CHECK (role IN ('primary', 'fallback', 'escalation')),
            provider text NOT NULL CHECK (provider ~ '^[a-z_]{1,32}$'),
            model_id text NOT NULL CHECK (length(model_id) BETWEEN 1 AND 200),
            model_version text NULL,
            prompt_template_version text NOT NULL CHECK (length(prompt_template_version) <= 100),
            routing_revision text NOT NULL,
            data_classification text NOT NULL
                CHECK (data_classification IN ('PUBLIC', 'INTERNAL', 'RESTRICTED')),
            evidence_snapshot_id text NULL,
            status text NOT NULL CHECK (status IN ('OK', 'REFUSED', 'ERROR', 'TIMEOUT')),
            error_code text NULL CHECK (error_code ~ '^[A-Za-z0-9_.]{1,120}$'),
            input_tokens integer NOT NULL CHECK (input_tokens >= 0),
            output_tokens integer NOT NULL CHECK (output_tokens >= 0),
            elapsed_ms integer NOT NULL CHECK (elapsed_ms >= 0),
            created_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE INDEX llm_calls_run_idx ON knowledge.llm_calls (run_id)
        """
    )
    # BRF-08: each sweep keeps its own scored areas, so a report can be reproduced later.
    op.execute(
        """
        CREATE TABLE knowledge.hotspot_sweeps (
            sweep_id uuid PRIMARY KEY,
            trigger text NOT NULL CHECK (trigger IN ('schedule', 'on_demand')),
            scoring_version text NOT NULL,
            window_days integer NOT NULL CHECK (window_days BETWEEN 1 AND 3650),
            area_depth integer NOT NULL CHECK (area_depth BETWEEN 1 AND 10),
            areas_scored integer NOT NULL CHECK (areas_scored >= 0),
            areas_flagged integer NOT NULL CHECK (areas_flagged >= 0),
            started_at timestamptz NOT NULL,
            completed_at timestamptz NOT NULL
        );
        CREATE INDEX hotspot_sweeps_completed_idx ON knowledge.hotspot_sweeps (completed_at DESC);

        CREATE TABLE knowledge.hotspot_areas (
            sweep_id uuid NOT NULL REFERENCES knowledge.hotspot_sweeps (sweep_id) ON DELETE CASCADE,
            source_instance text NOT NULL,
            project_id text NOT NULL,
            repository_id text NOT NULL,
            area text NOT NULL CHECK (length(area) BETWEEN 1 AND 1024),
            changes integer NOT NULL CHECK (changes >= 0),
            defect_fixes integer NOT NULL CHECK (defect_fixes >= 0),
            reverted_fixes integer NOT NULL CHECK (reverted_fixes >= 0),
            -- Smoothed incident rate of the area divided by the repository's rate (1.0 = average).
            lift numeric(8, 3) NOT NULL CHECK (lift >= 0),
            band text NOT NULL CHECK (band IN ('HOT', 'WATCH', 'NORMAL', 'INSUFFICIENT_DATA')),
            rank integer NOT NULL CHECK (rank >= 1),
            -- The tickets and fixes behind the counts: [{"ticket_id", "fix_kind", "fix_id", ...}].
            evidence jsonb NOT NULL CHECK (jsonb_typeof(evidence) = 'array'),
            PRIMARY KEY (sweep_id, source_instance, project_id, repository_id, area)
        );
        CREATE INDEX hotspot_areas_flagged_idx ON knowledge.hotspot_areas (sweep_id, rank)
            WHERE band IN ('HOT', 'WATCH')
        """
    )
    # BRF-05 / BRF-07: one comment per run, and the pickup-to-comment time of that run.
    op.execute(
        f"""
        CREATE TABLE workflow.brief_publications (
            run_id uuid PRIMARY KEY REFERENCES workflow.analysis_runs (run_id),
            result_id uuid NOT NULL,
            result_state text NOT NULL CHECK (result_state IN ({_STATES})),
            status text NOT NULL DEFAULT 'pending'
                CHECK (status IN ('pending', 'published', 'failed')),
            -- How it was delivered: 'record' keeps the comment here and calls nothing external.
            mode text NULL CHECK (mode IN ('record', 'ado')),
            comment_id text NULL CHECK (length(comment_id) BETWEEN 1 AND 200),
            comment_sha256 text NULL CHECK (comment_sha256 ~ '^[0-9a-f]{{64}}$'),
            comment_body text NULL,
            reason text NULL CHECK (reason ~ '^[a-z_]{{1,64}}$'),
            attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            available_at timestamptz NOT NULL DEFAULT now(),
            created_at timestamptz NOT NULL DEFAULT now(),
            -- When the ticket was picked up (webhook or sweep stored it), else when the run began.
            picked_up_at timestamptz NOT NULL,
            published_at timestamptz NULL,
            time_to_brief_ms bigint NULL CHECK (time_to_brief_ms >= 0),
            CHECK ((status = 'published') = (published_at IS NOT NULL))
        );
        CREATE INDEX brief_publications_pending_idx ON workflow.brief_publications (available_at)
            WHERE status = 'pending';
        CREATE INDEX brief_publications_published_idx
            ON workflow.brief_publications (published_at) WHERE status = 'published'
        """
    )
    # BRF-06: what the engineer did with one advice item. Append-only: a changed mind is a new
    # row, and the latest row per (result, advice) is the current disposition. This is evidence
    # of adoption, never of correctness (v1.11 section 8.10).
    op.execute(
        """
        CREATE TABLE workflow.advice_feedback (
            feedback_id uuid PRIMARY KEY,
            run_id uuid NOT NULL REFERENCES workflow.analysis_runs (run_id),
            -- The immutable brief revision the advice id belongs to.
            result_id uuid NOT NULL,
            advice_id text NOT NULL CHECK (advice_id ~ '^ADV-[0-9]{3}$'),
            disposition text NOT NULL
                CHECK (disposition IN ('ACCEPTED', 'MODIFIED', 'REJECTED', 'NOT_APPLICABLE')),
            note text NULL CHECK (length(note) <= 500),
            -- ADO identity id of the engineer. Older ADO payloads carry no id; then this is
            -- the unique name, because a rating must be attributable (see the BRF-06 doc).
            reviewer_id text NOT NULL CHECK (length(reviewer_id) BETWEEN 1 AND 200),
            source text NOT NULL CHECK (source IN ('ado_comment')),
            -- ADO's notification id: a redelivered comment records nothing new.
            delivery_id text NOT NULL CHECK (length(delivery_id) BETWEEN 1 AND 200),
            rated_at timestamptz NOT NULL,
            received_at timestamptz NOT NULL DEFAULT now(),
            UNIQUE (source, delivery_id, advice_id)
        );
        CREATE INDEX advice_feedback_advice_idx
            ON workflow.advice_feedback (result_id, advice_id, rated_at DESC);

        CREATE FUNCTION workflow.reject_advice_feedback_change() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'advice feedback is append-only';
        END;
        $$;
        CREATE TRIGGER advice_feedback_append_only
            BEFORE UPDATE OR DELETE ON workflow.advice_feedback
            FOR EACH ROW EXECUTE FUNCTION workflow.reject_advice_feedback_change()
        """
    )


def downgrade() -> None:
    raise RuntimeError(
        "Briefs, advice ratings and model-call logs are audit history; use a forward migration."
    )
