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

### Pull request events (LRN-12)

The same host also serves `POST /webhooks/ado/pull-requests`. Subscribe it (same credentials) to *Pull request
created*, *Pull request updated* (this includes reviewer votes and abandonment), *Pull request merge attempted*
and *Pull request commented on*.

| Behaviour | Detail |
|---|---|
| Re-fetch | The event only identifies the PR; VisionIQ re-reads its current status, draft flag, reviewers and votes, linked work items and commits from ADO |
| Stored once | Each distinct PR state is one revision; a replayed or late event that finds the same state stores nothing |
| Order | Late or out-of-order events end in the same result, because the current state is always re-read |
| Lifecycle | `Lifecycle` on the PR record: merged, abandoned, draft, rejected, waiting_for_author, approved or active |
| Never verified | PR events only record facts and explicit/inferred ticket-fix claims; "verified" exists only as a human review |
| Privacy | Reviewers and comment authors are stored by ADO identity ID only, never names or e-mails |
| §8.12 evidence | Source/target commit, every iteration, cumulative changed files, review threads (no system messages), file-level patch fingerprint, branch-policy evaluations; unreadable parts are listed in `enrichmentGaps` |
| Permissions | The PAT also needs *Code (read)* for iterations/threads and policy read for evaluations; otherwise those parts are recorded as unavailable |

**Current scheduled behavior:** Every active PR is re-enriched on each delta, including title and reviewer updates even when the creation/closure cursor does not move. An unchanged active PR may appear in a new local JSONL run folder; canonical revision deduplication is a separate persistence test. Closed PRs use the ordinary cursor. Webhooks remain the near-real-time path, with reconciliation as the safety net.

## 2. Scheduled sync (the safety net)

| Job | Command | Purpose |
|---|---|---|
| Delta sync (e.g. every 15 minutes) | `sync` | Reads changes since the saved checkpoints |
| Reconciliation sweep (e.g. nightly) | `sync --reconcile-days 7` | Re-reads the last N days (1-90) to catch anything a webhook missed; never moves checkpoints; audited as `reconcile` |

Failed items never stop a run: each one is listed in `evidence.sync_quarantine` (canonical mode) or
`sync-quarantine.jsonl` (local mode) with its reason and attempt count, retried on the next run, and marked
resolved once read. The audit row records how many items were quarantined.

Running these on a schedule (Cloud Run Jobs + Cloud Scheduler, or Pub/Sub delivery) is FND-06 infrastructure.

## Acceptance scope (November 2026)

A separately tested private company checkout demonstrated new PR detection, updated open PR titles, changed Task revisions, and seven-day reconciliation with unchanged checkpoint hash. Automated failure/quarantine/webhook tests and disposable PostgreSQL evidence replay tests passed. This **does not** prove a live deployed webhook or Scheduler/Cloud Run Job.

ADO work items and builds are project-scoped; `ADO_REPOS` only narrows commit/PR streams. A Confluence `--since` filter does not prevent initial scanning of the configured space's permissions. JSONL intentionally withholds restricted/unknown-access pages and may return `Partial` (exit 2) without a checkpoint. Full-history import and live private GCS/Cloud SQL/Cloud Run validation remain **pending** under the acceptance/FND-06 work. See [LRN-05 validation](BACKFILL-OVERVIEW.md).
