# LRN-05: keeping evidence current

Two complementary paths keep VisionIQ current. Both write through the same evidence chain, environment rules
and projections, so they never disagree about the same revision.

## 1. Webhooks (near real time)

`dotnet run --project src/VisionIQ.Ingestor.Cli -- serve-webhooks` hosts `POST /webhooks/ado/work-items`
(and `GET /health`). Requires `INGESTION_OUTPUT_MODE=canonical`, `ADO_WEBHOOK_USERNAME` and
`ADO_WEBHOOK_SECRET` (at least 32 characters). Port: `PORT` (default 8080).

In Azure DevOps: **Project settings -> Service hooks -> Create subscription -> Web Hooks**, for the events
*Work item created*, *Work item updated* and *Work item restored*. Set the URL to the endpoint (HTTPS in any
real environment) and the basic-authentication username and password to the two settings above.

| Behaviour | Detail |
|---|---|
| Authentication | HTTP Basic credentials compared in constant time; anything else is 401 |
| Trust | The payload only says WHICH item changed (event ID, item ID, revision, project). The item is always re-fetched from ADO with VisionIQ's read-only credentials |
| Scope | Other organizations, unapproved projects and other event types are acknowledged (200) and ignored |
| Idempotency | Each event goes through the inbox: duplicates are no-ops; concurrent attempts get 503 |
| Retries | A failed attempt returns 500 so ADO retries; after 5 attempts the event is dead-lettered (200) and the reconciliation sweep picks the item up |
| Limits | Request bodies over 1 MiB are rejected |

## 2. Scheduled sync (the safety net)

| Job | Command | Purpose |
|---|---|---|
| Delta sync (e.g. every 15 minutes) | `sync` | Reads changes since the saved checkpoints |
| Reconciliation sweep (e.g. nightly) | `sync --reconcile-days 7` | Re-reads the last N days (1-90) to catch anything a webhook missed; never moves checkpoints; audited as `reconcile` |

Failed items never stop a run: each one is listed in `evidence.sync_quarantine` (canonical mode) or
`sync-quarantine.jsonl` (local mode) with its reason and attempt count, retried on the next run, and marked
resolved once read. The audit row records how many items were quarantined.

Running these on a schedule (Cloud Run Jobs + Cloud Scheduler, or Pub/Sub delivery) is FND-06 infrastructure.
