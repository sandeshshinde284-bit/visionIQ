# Architecture Diagrams

## Visual Diagrams

For interactive, mechanism-focused diagrams showing data flow, checkpoint logic, and state machines, see:

- **[learn-pipeline-flow.html](images/learn-pipeline-flow.html)** — Visual pipeline showing source→links→archive→sync→graph data flow, checkpoint mechanisms, and quarantine retry logic
- **[webhook-at-least-once.html](images/webhook-at-least-once.html)** — At-least-once webhook processing with deduplication, lease expiry, and dead-letter handling

---

## 1. LRN-01-05 Data Pipeline

The LEARN phase implements a 5-stage data pipeline:

```
┌─────────────────────────────────────────────────────────────────────────┐
│ STAGE 1: SOURCE READING (LRN-01)                                        │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  Azure DevOps (ADO)  ──┬──> Work Items (WI)                            │
│                        ├──> Pull Requests (PR)                          │
│                        ├──> Commits                                     │
│                        └──> Builds                                      │
│                                                                          │
│  Confluence          ──────> Design Decisions (LRN-02)                 │
│                               with ACL verification                     │
│                                                                          │
│  Key Concept: CHECKPOINTS                                              │
│  • Resume sync from last successful record (TimeCursor)                │
│  • Prevents duplicates: items older than checkpoint are skipped        │
│  • Quarantine failed items for manual retry                            │
└─────────────────────────────────────────────────────────────────────────┘
                              │
                              │ Raw source data
                              ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ STAGE 2: LINK EXTRACTION (LRN-04)                                       │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  LinkExtractor parses:                                                  │
│  • Explicit: "PR #123 fixes ADO ticket 456"                           │
│  • Inferred: Commit message mentions "fixes #789"                      │
│  • Revert tracking: Detects reverts by commit title matching           │
│                                                                          │
│  Output: TicketFixClaim                                                │
│  ├─ source_instance, project_id, ticket_id                            │
│  ├─ pull_request_id / commit_sha                                       │
│  ├─ Classification (Explicit, Inferred, Unknown)                       │
│  └─ source_evidence_refs[] (proof)                                     │
└─────────────────────────────────────────────────────────────────────────┘
                              │
                              │ Structured claims
                              ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ STAGE 3: IMMUTABLE ARCHIVAL (LRN-03)                                    │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  PostgreSQL (evidence schema)                                           │
│  ├─ ticket_fix_links                  (current associations)            │
│  ├─ ticket_fix_link_lifecycle_events  (retraction audit trail)         │
│  ├─ evidence_revisions                (versioned records)               │
│  └─ evidence_access_log               (audit trail)                    │
│                                                                          │
│  Google Cloud Storage (gs://bucket/raw/v1/...)                        │
│  ├─ Raw JSON indexed by content hash (SHA256)                          │
│  ├─ Immutable: same content = same object                             │
│  └─ IfGenerationMatch=0: never overwrite                              │
│                                                                          │
│  Key Concept: CONTENT-HASH REVISIONS                                   │
│  • Identical evidence = same revision ID (no duplication)              │
│  • Each change = new revision (append-only)                            │
│  • No overwrites, no conflicts                                         │
└─────────────────────────────────────────────────────────────────────────┘
                              │
                              │ Versioned, deduplicated evidence
                              ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ STAGE 4: CURRENCY & SYNC (LRN-05)                                       │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  Three sync strategies in parallel:                                    │
│                                                                          │
│  ┌─ Full Backfill (LRN-05: backfill run)                              │
│  │  └─ Reads entire source history on first sync                      │
│  │                                                                      │
│  ├─ Delta Sync (LRN-05: sync run)                                     │
│  │  └─ Resumes from checkpoint, only new/changed items                │
│  │                                                                      │
│  └─ Webhook Events (LRN-05: webhook handler)                          │
│     ├─ At-least-once: stored in source_change_inbox                   │
│     ├─ Deduplication: (source_id, event_id) unique constraint         │
│     └─ Lease-based: 5-minute timeout, then retried                    │
│                                                                          │
│  Reconciliation:                                                        │
│  • Scheduled run compares what we have vs. source of truth             │
│  • Catches items missed by checkpoints or webhooks                     │
│  • Updates quarantined items                                           │
└─────────────────────────────────────────────────────────────────────────┘
                              │
                              │ Latest evidence, always current
                              ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ STAGE 5: RELATIONSHIP GRAPH (LRN-06, LRN-07)                           │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                          │
│  Knowledge Base Built:                                                  │
│  ├─ Tickets ──explicit link──> Pull Requests                          │
│  ├─ PRs ──contains──> Commits                                          │
│  ├─ Commits ──touches──> Code Files & Symbols                          │
│  └─ All edges verify ACLs at traversal time (fail-closed)             │
│                                                                          │
│  Queryable via retrieval APIs (LRN-06, LRN-07)                         │
│  with proof strength ratings (explicit > inferred > unknown)           │
└─────────────────────────────────────────────────────────────────────────┘
```

## 2. Sync Runner Checkpoint Logic

```
┌───────────────────────────────────────────────────────────────────┐
│ SyncRunner.SyncWorkItemsAsync Flow                               │
├───────────────────────────────────────────────────────────────────┤
│                                                                   │
│  1. LOAD CHECKPOINT                                              │
│     TimeCursor? cursor = await LoadCursorAsync()                │
│     (Resume from last successful write, or start from SINCE)    │
│                                                                   │
│  2. QUERY SOURCE (with snapshot)                                 │
│     var query = await workItems.QueryIdsAsync(                  │
│         project, cursor?.At ?? request.Since)                   │
│     ↳ Get all work item IDs changed since checkpoint            │
│     ↳ Remember query.AsOf (snapshot time)                       │
│                                                                   │
│  3. BATCH READ                                                   │
│     var outcomes = await workItems.GetWorkItemsAsync(           │
│         project, ids, query.AsOf)  ← Use SAME snapshot         │
│     ↳ Ensures consistency between list and detail queries       │
│                                                                   │
│  4. PROCESS OUTCOMES (one per queried ID, in order)             │
│     ├─ OK: Add to fresh[]                                       │
│     ├─ NULL: → Quarantine (retry next run)                      │
│     └─ Previously Quarantined? → Mark resolved                  │
│                                                                   │
│  5. WRITE RECORDS (ordered by ChangedDate, Id)                  │
│     foreach (var record in fresh.OrderBy(...))                  │
│     {                                                             │
│         await sink.WriteAsync(record)                            │
│         written.Add((record.ChangedDate, record.Id))            │
│     }                                                             │
│                                                                   │
│  6. HANDLE 20k LIMIT                                             │
│     if (ids.Count >= 20000)                                      │
│     {                                                             │
│         ↳ Checkpoint at last written item                        │
│         ↳ Caller will run again to continue                      │
│     }                                                             │
│                                                                   │
│  7. SAVE CHECKPOINT (only if success)                            │
│     ├─ Flush all writes to sink                                  │
│     ├─ Save: cursor pointing to last written item               │
│     └─ Never checkpoint past any unwritten item                  │
│                                                                   │
│  8. RETRY QUARANTINED (Recycle Bin check)                        │
│     └─ Separately read deleted work items                        │
│                                                                   │
│  Result: SyncReport                                              │
│  ├─ Records: count of successfully written items                 │
│  ├─ Quarantined: count of failed items (retried next run)       │
│  ├─ Status: Synced | Partial | NotSynced                         │
│  └─ Reason: human-readable error (if not fully synced)          │
└───────────────────────────────────────────────────────────────────┘
```

## 3. Ticket-Fix Link Retraction Lifecycle

```
┌─────────────────────────────────────────────────────────────────┐
│ State Machine for ticket_fix_links.retracted_at                │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│        ┌──────────────────┐                                    │
│        │  ACTIVE          │                                    │
│        │ (no retraction)   │                                    │
│        └────────┬─────────┘                                    │
│                 │                                              │
│                 │ UpsertSourceAsync()                          │
│                 │ → clearRetraction=true                       │
│                 │ → Can resurrect from RETRACTED              │
│                 │                                              │
│        ┌────────▼─────────┐                                    │
│        │  ACTIVE or       │                                    │
│        │  RETRACTED       │                                    │
│        │ (same record)     │                                    │
│        └────────┬─────────┘                                    │
│                 │                                              │
│                 │ ReconcileSourceAsync()                       │
│                 │ → Link no longer in source                   │
│                 │ → Set retracted_at = now()                  │
│                 │                                              │
│        ┌────────▼──────────┐                                   │
│        │  RETRACTED        │                                   │
│        │ (retracted_at set) │                                   │
│        └────────┬──────────┘                                   │
│                 │                                              │
│                 │ UpsertSourceAsync()                          │
│                 │ → Fresh claim from source                    │
│                 │ → Clear retracted_at                         │
│                 │ → Back to ACTIVE                             │
│                 │                                              │
│        ┌────────▼─────────┐                                    │
│        │  ACTIVE          │                                    │
│        └──────────────────┘                                    │
│                                                                 │
│  Key Invariant:                                                 │
│  • Explicit claims (direct source) can resurrect retracted links│
│  • Candidate claims (inferred) CANNOT resurrect               │
│    (UpsertCandidateEvidenceAsync → clearRetraction=false)     │
│  • Human review verdict (verified/disputed) is NEVER cleared   │
│                                                                 │
│  Audit Trail: All state changes recorded in                    │
│  evidence.ticket_fix_link_lifecycle_events (append-only)      │
└─────────────────────────────────────────────────────────────────┘
```

## 4. Evidence Storage Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│ Content-Addressed Storage (CloudStorageEvidenceArchive)        │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Input: TicketFixClaim or other raw JSON evidence              │
│  ├─ itemKey = source-derived key (e.g., "ado/proj/wi/123")    │
│  ├─ revisionId = version identifier (e.g., "revision-42")      │
│  ├─ rawJson = serialized bytes                                 │
│  └─ contentSha256 = SHA256(rawJson)                           │
│                                                                 │
│  Process:                                                       │
│  1. Compute revisionHash = SHA256(UTF8(revisionId))           │
│  2. Build object path:                                         │
│     gs://bucket/raw/v1/{itemKey}/{revisionHash}/{contentSha256}.json
│     Example:                                                    │
│     gs://bucket/raw/v1/ado/proj123/work-item/456/               │
│       a1b2c3d4e5f6.../9z8y7x6w5v4u3t2s1r0q.json              │
│                                                                 │
│  3. Upload with IfGenerationMatch=0                            │
│     ├─ If object doesn't exist: upload succeeds               │
│     ├─ If object exists: HTTP 412, silently caught            │
│     └─ Result: idempotent (safe on retries)                   │
│                                                                 │
│  Key Properties:                                                │
│  • Immutable: content hash = identity                          │
│  • Deduped: identical content = same object                    │
│  • Versioned: different revisions = different paths            │
│  • Traceable: paths link to source and revision               │
│  • Append-only: no overwrites, no conflicts                   │
│                                                                 │
│  PostgreSQL Companion:                                          │
│  evidence.evidence_revisions                                    │
│  ├─ item_key, revision_id (source identity)                   │
│  ├─ content_hash (SHA256 of raw bytes)                         │
│  ├─ archive_url (gs://... path)                               │
│  ├─ created_at (when this revision was first seen)            │
│  └─ observed_at (when we indexed it)                          │
└─────────────────────────────────────────────────────────────────┘
```

## 5. At-Least-Once Webhook Processing

```
┌─────────────────────────────────────────────────────────────────┐
│ PostgresSourceChangeInbox: At-Least-Once Semantics             │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Table: evidence.source_change_inbox                            │
│  ├─ source_id, event_id [UNIQUE]                              │
│  ├─ provider_kind, native_id, source_revision (immutable)     │
│  ├─ status (processing | completed | quarantined)             │
│  ├─ claim_token, claim_expires_at (5-min lease)              │
│  ├─ attempt_count (max 5, then dead-lettered)                │
│  └─ last_error_code (sanitized, no secrets)                  │
│                                                                 │
│  Scenario 1: Normal Flow                                       │
│  ┌─ Webhook arrives                                            │
│  ├─ TryClaimAsync():                                           │
│  │  └─ INSERT ... ON CONFLICT DO NOTHING                      │
│  │     ✓ New event: insert succeeds, get token                │
│  │     ✗ Duplicate: return AlreadyCompleted (if done) or       │
│  │                     InProgress (if being processed)         │
│  ├─ Handler re-fetches source (with source auth)              │
│  ├─ WriteAsync() stores result                                 │
│  └─ CompleteAsync(token):                                      │
│     └─ UPDATE status='completed' WHERE claim_token=token      │
│                                                                 │
│  Scenario 2: Worker Crash                                      │
│  ┌─ Handler fails mid-processing                               │
│  ├─ Lease expires (5 min)                                      │
│  ├─ Different worker:                                          │
│  │  └─ TryClaimAsync() sees expired lease                     │
│  │     └─ UPDATE status='processing', claim_token=new_token   │
│  ├─ Re-runs handler (idempotent due to source revision)       │
│  └─ Completes successfully                                     │
│                                                                 │
│  Scenario 3: Poison Event                                      │
│  ┌─ Event fails 5 times                                        │
│  ├─ attempt_count >= maxAttempts                              │
│  ├─ TryClaimAsync() returns DeadLettered                      │
│  └─ Event stays in DB for manual reconciliation               │
│                                                                 │
│  Scenario 4: Conflict (Replay with different source data)      │
│  ┌─ Same event_id but different native_id/source_revision    │
│  ├─ TryClaimAsync() detects mismatch                          │
│  └─ Returns IdentityConflict (operator must investigate)      │
│                                                                 │
│  Guarantee: Every event is re-fetched from source and         │
│  processed exactly once (or retried until success/dead-letter) │
└─────────────────────────────────────────────────────────────────┘
```

---

## Key Concepts Summary

| Concept | Where | Why | How |
|---------|-------|-----|-----|
| **Checkpoints** | LRN-01, LRN-05 | Resume sync without re-reading all history | TimeCursor stores (time, ID) of last written item |
| **Content-Hash Revisions** | LRN-03 | Deduplicate identical evidence | SHA256(rawJSON) = version identity, no overwrites |
| **Live ACL Re-check** | LRN-02, LRN-07 | Enforce permissions at read time, not ingest | Every retrieval verifies Confluence access |
| **Retraction Lifecycle** | LRN-04 | Track when links are withdrawn, allow re-linking | Append-only lifecycle events, separate retracted_at flag |
| **At-Least-Once Processing** | LRN-05 | Guarantee webhook events aren't lost | Dedup inbox with leased claims, retry with expiry |
| **Fail-Closed** | LRN-02, LRN-07 | Never guess permissions | When uncertain, deny access and report error |
| **Append-Only Audit** | LRN-04 | Complete history, no rewrites | All state changes go to lifecycle_events table |
| **Idempotent Source Operations** | LRN-03, LRN-05 | Safe retries without conflicts | Content hash + version identity + IfGenerationMatch=0 |

---

See the individual LRN-XX feature folders for detailed implementation documentation.
