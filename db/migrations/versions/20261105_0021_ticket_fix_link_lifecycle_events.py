"""LRN-04 review, second-pass correction (Finding #4): append-only retraction/relink history.

Revision ID: 20261105_0021
Revises: 20261104_0020

evidence.ticket_fix_links.retracted_at and evidence.ticket_fix_link_source_associations.ended_at
each only record the *current* state of a transition (retracted or not; a source's association
currently ended or not). A link that is retracted, later relinked, then retracted again a second
time leaves no trace of the first retraction once ended_at/retracted_at are reset -- an auditor
or a later investigation cannot tell the cycle happened at all, only where it stands today.

This table is purely additive and append-only: every transition is INSERTed, never UPDATEd or
DELETEd, so the full retract/relink history survives any number of cycles. The existing
current-state columns are unchanged and remain the fast path for "is this retracted right now".
"""

from alembic import op

revision = "20261105_0021"
down_revision = "20261104_0020"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Add an append-only lifecycle-event log for ticket_fix_links retraction/relink cycles."""
    op.execute(
        """
        CREATE TABLE evidence.ticket_fix_link_lifecycle_events (
            id bigserial PRIMARY KEY,
            association_key text NOT NULL
                REFERENCES evidence.ticket_fix_links(association_key) ON DELETE RESTRICT,
            event_type text NOT NULL CHECK (event_type IN ('retracted', 'cleared')),
            reason text NULL,
            occurred_at timestamptz NOT NULL DEFAULT now()
        )
        """
    )
    op.execute(
        """
        CREATE INDEX ticket_fix_link_lifecycle_events_key_idx
        ON evidence.ticket_fix_link_lifecycle_events(association_key, occurred_at)
        """
    )


def downgrade() -> None:
    """Do not silently discard lifecycle history; use a reviewed forward migration."""
    raise RuntimeError(
        "LRN-04 retraction lifecycle history is audit evidence; use a reviewed forward migration."
    )
