"""BRF-01: automatic ticket pickup (S04-02 webhook receiver, S10-01 rules, S15-14 sweep).

Every pickup that passes the project's rules is stored once (``workflow.ticket_pickups``), whether
it came from the ADO service hook or from the reconciliation sweep. A worker turns it into exactly
one ``workflow.analysis_runs`` row per (project, ticket, pickup revision, policy revision).

Revision ID: 20261105_0019
Revises: 20261102_0018
"""

from alembic import op

revision = "20261105_0019"
down_revision = "20261102_0018"
branch_labels = None
depends_on = None


def upgrade() -> None:
    # Runs created by pickup carry the policy that chose them; the dev endpoint leaves these NULL.
    op.execute(
        """
        ALTER TABLE workflow.analysis_runs
            ADD COLUMN trigger_source text NULL
                CHECK (trigger_source IN ('ado_webhook', 'ado_sweep')),
            ADD COLUMN ticket_kind text NULL CHECK (ticket_kind IN ('defect', 'enhancement')),
            ADD COLUMN policy_revision text NULL
                CHECK (policy_revision ~ '^[A-Za-z0-9_.:-]{1,64}$');
        -- One run per pickup: the same ticket event never starts a second run (S10-01).
        CREATE UNIQUE INDEX analysis_runs_pickup_uq
            ON workflow.analysis_runs
                (tenant_id, project_id, ticket_id, ticket_revision, policy_revision)
            WHERE policy_revision IS NOT NULL
        """
    )
    op.execute(
        """
        CREATE TABLE workflow.ticket_pickups (
            pickup_id uuid PRIMARY KEY,
            trigger_source text NOT NULL CHECK (trigger_source IN ('ado_webhook', 'ado_sweep')),
            -- ADO's notification id for a webhook; project/ticket/revision for the sweep.
            delivery_id text NOT NULL CHECK (length(delivery_id) BETWEEN 1 AND 200),
            project_id text NOT NULL,
            ticket_id text NOT NULL,
            ticket_revision integer NOT NULL CHECK (ticket_revision >= 1),
            ticket_kind text NOT NULL CHECK (ticket_kind IN ('defect', 'enhancement')),
            work_item_type text NOT NULL,
            pickup_state text NOT NULL,
            -- ADO identity id of whoever made the change, never a name or e-mail.
            triggered_by text NULL,
            policy_revision text NOT NULL CHECK (policy_revision ~ '^[A-Za-z0-9_.:-]{1,64}$'),
            -- SHA-256 of the webhook body; the body itself (ticket text) is not kept.
            payload_sha256 text NULL CHECK (payload_sha256 ~ '^[0-9a-f]{64}$'),
            trace_id text NOT NULL CHECK (trace_id ~ '^[0-9a-f]{32}$'),
            status text NOT NULL DEFAULT 'pending'
                CHECK (status IN ('pending', 'started', 'duplicate', 'failed')),
            reason text NULL CHECK (reason ~ '^[a-z_]{1,64}$'),
            run_id uuid NULL REFERENCES workflow.analysis_runs (run_id),
            attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            available_at timestamptz NOT NULL DEFAULT now(),
            received_at timestamptz NOT NULL DEFAULT now(),
            processed_at timestamptz NULL,
            UNIQUE (trigger_source, delivery_id)
        );
        CREATE INDEX ticket_pickups_pending_idx ON workflow.ticket_pickups (available_at)
            WHERE status = 'pending';
        CREATE INDEX ticket_pickups_ticket_idx
            ON workflow.ticket_pickups (project_id, ticket_id, ticket_revision)
        """
    )


def downgrade() -> None:
    raise RuntimeError(
        "Ticket pickups hold the audit trail of started runs; use a forward migration."
    )
