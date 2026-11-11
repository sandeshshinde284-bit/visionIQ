# LRN-13 — Manual evidence-based learning for the hackathon

LRN-13 indexing and candidate/disputed lesson history are implemented. This runbook deliberately uses an **operator-run** manual workflow, not automatic ADO-triggered indexing, scheduling, or Pub/Sub delivery.

## Run sequence
1. Ingest the real approved historical Bug, PR and explicit fix links into canonical PostgreSQL/GCS using the trusted ingestor. Validate current source permissions.
2. Publish an initial full LRN-08 snapshot. LRN-10 requires redacted bug reproduction/title chunks and explicit undisputed fixes, not file overlap alone.
3. Look up the 64-character canonical PR item_key in evidence.source_items, and invoke the operator workflow:

```powershell
$env:PYTHONPATH = 'src/VisionIQ.Intelligence'
.\.venv\Scripts\python.exe -m app.knowledge.lrn13_manual_cli --item-key <canonical-pr-item-key> --output artifacts/lrn13/manual-fix.json
```

4. Inspect snapshot_before/after, index item/chunk/embedding counts, changed_lessons, append-only revision history, and change_events. A candidate remains root-cause unverified.
5. Ingest the **real historical revert** using the normal source connector, never by editing production SQL or inventing a commit.
6. Rerun targeted refresh on the affected PR/work-item keys and write manual-revert.json. Check candidate -> disputed, preserved old revision, original verified link, actual revert SHA.
7. Rerun unchanged and confirm no new revision. If any source evidence is missing, report WAIT_FOR_EVIDENCE; do not fake a success.

## Safety boundaries
- Requires 1–20 unique lowercase 64-character canonical item keys; only PR or current work-item source records.
- Requires private DB_INTELLIGENCE credentials and GCS archive access. No secrets or raw source bodies appear in the JSON report.
- Publishes a checked targeted snapshot **before** running lesson consolidation. Any failed indexing or rejected publication stops the workflow and returns nonzero.
- Uses existing LRN-10 idempotency. The consolidator currently scans all qualifying lessons; only relevant lesson changes are shown in the report.
- An internal knowledge.change_events record is not proof of Pub/Sub publication. No automatic webhook-to-learning link or Pub/Sub dispatcher is claimed.

## Acceptance
Focused CI tests use synthetic source records and disposable PostgreSQL. They are not proof of a real ADO fix-then-revert.
After merge, test the approved real historical ADO fix and revert manually in Cloud SQL/GCS and retain source IDs, snapshot IDs and report JSONs. Verify no access-scope leaks before showing any Brief.