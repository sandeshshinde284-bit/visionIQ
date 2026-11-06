"""FND-05: transactional outbox and idempotent inbox for events between .NET and Python.

Each service owns its own pair in its own schema (workflow = API, knowledge = Intelligence), so the
FND-03 write isolation still holds: a service only writes events it produces or consumes.

Revision ID: 20261102_0018
Revises: 20261101_0017

Originally authored as 20261102_0002 on top of the foundation; re-pointed after the LEARN chain
(0002-0017) so the repository keeps a single Alembic head.
"""

from alembic import op

revision = "20261102_0018"
down_revision = "20261101_0017"
branch_labels = None
depends_on = None

_OUTBOX = """
CREATE TABLE {schema}.event_outbox (
    event_id uuid PRIMARY KEY,
    event_type text NOT NULL CHECK (event_type ~ '^[a-z]+(\\.[a-z_]+)+$'),
    schema_version integer NOT NULL CHECK (schema_version >= 1),
    topic text NOT NULL CHECK (length(topic) BETWEEN 3 AND 255),
    trace_id text NOT NULL CHECK (trace_id ~ '^[0-9a-f]{{32}}$'),
    payload jsonb NOT NULL CHECK (jsonb_typeof(payload) = 'object'),
    created_at timestamptz NOT NULL DEFAULT now(),
    available_at timestamptz NOT NULL DEFAULT now(),
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    last_error_code text NULL CHECK (last_error_code ~ '^[A-Za-z0-9_.]{{1,120}}$'),
    published_at timestamptz NULL,
    -- Set when a row can never be published; it leaves the queue and waits for a human.
    parked_at timestamptz NULL
);
CREATE INDEX event_outbox_pending_idx ON {schema}.event_outbox (available_at)
    WHERE published_at IS NULL AND parked_at IS NULL;
CREATE INDEX event_outbox_parked_idx ON {schema}.event_outbox (parked_at)
    WHERE parked_at IS NOT NULL
"""

_INBOX = """
CREATE TABLE {schema}.event_inbox (
    event_id uuid PRIMARY KEY,
    event_type text NOT NULL CHECK (event_type ~ '^[a-z]+(\\.[a-z_]+)+$'),
    schema_version integer NOT NULL CHECK (schema_version >= 1),
    trace_id text NOT NULL CHECK (trace_id ~ '^[0-9a-f]{{32}}$'),
    received_at timestamptz NOT NULL DEFAULT now(),
    -- Set when the event was seen but rejected (conflict with stored state);
    -- its handler wrote nothing.
    rejected_reason text NULL CHECK (rejected_reason ~ '^[a-z_]{{1,64}}$')
);
CREATE INDEX event_inbox_received_idx ON {schema}.event_inbox (received_at)
"""


def upgrade() -> None:
    for schema in ("workflow", "knowledge"):
        op.execute(_OUTBOX.format(schema=schema))
        op.execute(_INBOX.format(schema=schema))

    # API side: the run it asked for and the result reference it was told about.
    op.execute(
        """
        CREATE TABLE workflow.analysis_runs (
            run_id uuid PRIMARY KEY,
            tenant_id text NOT NULL,
            project_id text NOT NULL,
            ticket_id text NOT NULL,
            ticket_revision integer NOT NULL CHECK (ticket_revision >= 1),
            repository_id text NOT NULL,
            base_commit_sha text NOT NULL,
            analysis_mode text NOT NULL CHECK (analysis_mode IN ('brief', 'deep')),
            status text NOT NULL DEFAULT 'requested'
                CHECK (status IN ('requested', 'completed')),
            trace_id text NOT NULL,
            requested_at timestamptz NOT NULL DEFAULT now(),
            completed_at timestamptz NULL
        );
        CREATE TABLE workflow.analysis_results (
            result_id uuid PRIMARY KEY,
            run_id uuid NOT NULL UNIQUE REFERENCES workflow.analysis_runs (run_id),
            result_state text NOT NULL CHECK (result_state IN ('PUBLISHED', 'PARTIAL', 'BLOCKED')),
            evidence_ids text[] NOT NULL DEFAULT '{}',
            advice_ids text[] NOT NULL DEFAULT '{}',
            reason_codes text[] NOT NULL DEFAULT '{}',
            event_id uuid NOT NULL REFERENCES workflow.event_inbox (event_id),
            received_at timestamptz NOT NULL DEFAULT now()
        )
        """
    )

    # Intelligence side: one accepted request per run and the result it produced.
    op.execute(
        """
        CREATE TABLE knowledge.analysis_requests (
            run_id uuid PRIMARY KEY,
            event_id uuid NOT NULL UNIQUE REFERENCES knowledge.event_inbox (event_id),
            tenant_id text NOT NULL,
            project_id text NOT NULL,
            ticket_id text NOT NULL,
            ticket_revision integer NOT NULL CHECK (ticket_revision >= 1),
            repository_id text NOT NULL,
            base_commit_sha text NOT NULL,
            analysis_mode text NOT NULL CHECK (analysis_mode IN ('brief', 'deep')),
            security_scope_id text NOT NULL,
            result_id uuid NOT NULL UNIQUE,
            result_state text NOT NULL CHECK (result_state IN ('PUBLISHED', 'PARTIAL', 'BLOCKED')),
            reason_codes text[] NOT NULL DEFAULT '{}',
            received_at timestamptz NOT NULL DEFAULT now()
        )
        """
    )


def downgrade() -> None:
    raise RuntimeError(
        "Event outbox/inbox hold unpublished events and dedup history; use a forward migration."
    )
