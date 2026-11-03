# LRN-05 — Full backfill, incremental currentness and quarantine

**Status:** code under review; acceptance OPEN (FND-06 dependency).  
**PR:** #7; branch `feature/lrn05-backfill-reconciliation`.  
**Stack:** based on PR #6 (LRN-04); retarget to `feature/learn` after LRN-04 merge.

## Official story

Approved projects should have their complete historical records loaded with
per-source counts, stay current using changes/events plus periodic reconciliation,
and quarantine failed records without failing the entire source sweep.

## Python reconciliation (S05-13)

`app.backfill.reconcile.BackfillReconciler` defines typed async provider,
evidence writer, checkpoint and quarantine boundaries. It:
- processes only explicitly passed/approved source adapters;
- advances the checkpoint only through a **contiguous successful prefix**;
- writes a payload-free quarantine record for each failed item, then continues;
- reports `SYNCED`, `PARTIAL` or `NOT_SYNCED` for each source, never treating
  source failures as empty successful history;
- reconciles source-reported expected count against received count where supplied;
- preserves replayability when checkpoint persistence or quarantine is unavailable.

Tests include an initial ticket, its changed revision in the next cycle,
a failure in an older item followed by a newer success, read outage,
missing count, duplicate source configuration and quarantine failure.
Public methods have summary docstrings.

## .NET event inbox (S05-14 foundation)

Migration `20261030_0005` creates:
- `evidence.source_change_inbox`: unique (source ID, event ID), status,
  claim token/lease, attempts and sanitized failure category;
- `evidence.source_sync_runs`: full/delta/reconcile counts/status and observed
  execution windows, with immutable run IDs.

`SourceChangeProcessor` accepts an **authenticated internal** notification
whose source record is re-fetched by a provider-specific handler. It neither
trusts webhook content as canonical evidence nor exposes a public endpoint.
The PostgreSQL inbox prevents duplicate completed delivery, rejects
same-event-ID/different-item replays, and uses leases to recover interrupted
processing. Exceptions quarantine the current attempt without leaking raw
provider payloads or stack traces into the database.

## FND-06 / Cloud Run dependency (not yet built)

The live Pub/Sub subscription and authenticated push/pull transport, provider
signature and token verification, Cloud Run Jobs scheduling, permitted identity,
backoff/dead-letter policy, and approved Cloud SQL connectivity are **NOT**
implemented in this PR. No Google Cloud resource was created or changed.

Pending integration tasks before story completion:
1. Adapt actual LRN-01/02 sources and LRN-03 evidence archive to the Python
   provider/processor interfaces (or consolidate execution in the .NET service).
2. Implement durable Python checkpoint/quarantine persistence and run-report
   recording, using existing FND-03 schema-role boundaries.
3. Wire periodic full/delta reconciliation and authenticated Pub/Sub delivery
   via FND-06, without creating unauthenticated ingestion endpoints.
4. Execute the full backfill for explicitly approved projects; capture
   verified per-source item counts, limits, failures and their retries.
5. Demonstrate a changed real ticket appearing within one complete sync
   cycle, then verify replay and quarantine recovery end-to-end.

No source data or credentials may be published to this public repo.
CI fixture tests are **not** equivalent to real source or cloud acceptance.
