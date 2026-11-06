# LRN-04 — Acceptance and findings

Overall acceptance: OPEN. Local and cloud validation and five team-reviewed real source examples are required.

## F04-01 — Review history has UPDATE and DELETE privileges

Observed 3 November 2026 in user-run effective privilege queries: `visioniq_lrn03_api` has SELECT, INSERT, UPDATE and DELETE on `evidence.ticket_fix_reviews`. The inspected table definition shows no immutability trigger. Migration 0004 creates uniqueness and foreign-key constraints but no UPDATE/DELETE guard; `db/manage.py` grants evidence-schema DML to the API role. The store's review method appends decisions, but this does not establish database-enforced immutability. Confirm with a rollback-only mutation test before closing this finding; implement any correction separately from test evidence.

## F04-02 — Revert-before-link arrival

Code-level risk, not yet reproduced: `RecordRevertAsync` updates existing scoped associations only. Check whether a revert received before its association is retained or recovered through replay. Acceptance pending.

## Pending acceptance

- Actual PostgreSQL replay/review/revert behavior using the restricted role.
- Project/repository identity consistency and source isolation.
- Five known ADO ticket-to-fix links reviewed by the team, with errors logged.
- Cloud test-target configuration and execution evidence.
