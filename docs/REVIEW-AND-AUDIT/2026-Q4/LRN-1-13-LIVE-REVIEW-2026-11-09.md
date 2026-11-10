# LRN-01–13 Live Review — 2026-11-09

Running log for today's structured story-by-story review. Each story is reviewed in the same
sequence: **what it is → what it does → pending/untested gaps → how it fits the LEARN phase →
testing status**. Updated live as we work through each story; do not treat an unfilled section as
"done" — only the Testing Status line is the source of truth for acceptance.

**Scope decisions made today** (features flagged as more complex/advanced than needed right now)
are tracked in the **Scope Decisions** table at the bottom, each with its own Trello card, not
removed from the codebase.

---

## LRN-01 — History Connectors

**1. What it is:** The foundational ingestion layer that reads the team's Azure DevOps history
(work items, pull requests, commits, builds) into VisionIQ.

**2. What it does:**
- Queries ADO's REST API for work items/PRs/commits/builds, incrementally since the last checkpoint
- Writes each as immutable, content-addressed evidence (JSONL locally, or canonical PostgreSQL + GCS)
- Tracks a per-stream monotonic checkpoint so repeat runs only fetch what's new
- Detects ADO deletions via the Recycle Bin and tombstones them
- Quarantines individual unreadable items so one bad record doesn't block the whole sync

**3. Pending / not yet tested:**
- **Closed today, with real execution evidence.** `DbCheckpointStore.cs`'s monotonic-guard fix had
  never been exercised against a real PostgreSQL database (only `InMemoryCheckpointStore` had test
  coverage, via `CheckpointStoreTests.cs`). Correction of an earlier wrong call: real Cloud SQL
  connectivity isn't actually required to close this — the gap is "never run against real
  PostgreSQL SQL semantics," not "never run against this specific cloud instance." Reusing this
  session's own disposable-Docker-Postgres pattern (same as every other DB-level fix today),
  `tests/VisionIQ.Ingestor.Tests/DbCheckpointStoreIntegrationTests.cs` now exercises the real
  `DbCheckpointStore.GetAsync`/`SaveAsync` through the real API-role credentials, against a
  disposable `pgvector/pgvector:pg16` container, migrated/bootstrapped with `db/manage.py` exactly
  as production is: initial save, forward advancement, backward-write rejection (and the value
  staying at its prior high-water mark afterward), equal-cursor re-save (confirmed intentional —
  idempotent retry, not a regression, matching the SQL's own `<=` guard), and concurrent overlapping
  saves converging on the highest submitted cursor with at least one real rejection observed.
  **Result: 3/3 passed, 0 failed, 0 skipped, real assertions, no defect found.** Test rows were
  GUID-scoped and self-cleaned (confirmed 0 leftover rows); the disposable container was removed
  afterward; the real dev/cloud databases were never touched. Wired into `scripts/learn_e2e.py` as
  `run_lrn01_checkpoint_database_integration` (gated on canonical mode + DB secrets present, same
  pattern as the LRN-04 DB integration check) so this runs automatically on every future canonical
  LRN-01 pass, not just today.

**4. How it fits the LEARN phase:** Base of the pyramid — Phase 1, "always on" per the Master
Design. Every other story (LRN-02–13) reads evidence that ultimately came through this layer.

**5. Testing status:** **Proven with real evidence** — full ADO sync, all 3 streams (Commits,
PullRequests, WorkItems) `Synced` against the real Genoa Kiosk project, bounded-concurrency
performance fix verified, incremental sync proven via PR #38561's real completion event, and the
DB checkpoint monotonic guard now execution-verified against a real disposable PostgreSQL database
(3/3 passed). **No open gaps for LRN-01.**

---

## LRN-02 — Confluence

**1. What it is:** The second history connector alongside LRN-01 — reads design-decision context
from Confluence (Cloud and Data Center) so VisionIQ's evidence base isn't limited to ADO work
items/commits/PRs.

**2. What it does:**
- Queries Confluence's REST API for pages in an approved space, including version history
- Writes each page as immutable, content-addressed evidence (JSONL locally, or canonical
  PostgreSQL + GCS, same storage model as LRN-01)
- Every page is exported unconditionally (as of today — see below); no per-page access
  classification happens at ingestion time anymore

**3. Pending / not yet tested:**
- **REMOVED FROM CODE, in two passes today (2026-11-09, explicit team decisions — not deferred,
  actually deleted):**
  1. ADR/decision-page classification (`DesignDecisionDetector`, `DesignDecisionRecord`, the
     `design-decisions`/`design_decision` evidence stream, the standalone
     `discover-confluence-decisions` CLI tool) and live per-user Confluence read authorization
     (`ConfluencePageAccessVerifier`, `IConfluenceIdentityResolver`/`MappedConfluenceIdentityResolver`,
     `CONFLUENCE_IDENTITY_MAP_FILE`, the live Confluence `permission/check` call, its DI wiring in
     `GraphServiceRegistration`). Also removed: `ConfluenceCurrentAccessVerifier`, confirmed
     pre-existing dead code (already flagged "R2-10 dead code" in
     `docs/REVIEW-AND-AUDIT/2026-Q4/INTEGRATION-REVIEW.md`), same feature family.
  2. **Restricted-page ACL classification and withholding** (`ConfluencePageAcl`,
     `RestrictionLevel`, `ConfluenceViewer`/`ComputeAcls`/`CanView`/`Visible` in
     `ConfluenceAccess.cs`, the `confluence-acl`/`confluence_acl` evidence stream, and the
     withholding gate in `ConfluenceSyncRunner` that previously skipped writing a page's body when
     its ACL was incomplete or restricted in JSONL mode). Business assumption for now, explicitly
     stated by the team: everyone has Confluence access, so there is no need to distinguish
     restricted vs. unrestricted pages this release. Notably, `ConfluenceViewer`/`CanView`/`Visible`
     were **already dead code in production** before this removal — only ever called from tests,
     never wired into any real request-time authorization path (that path used Confluence's live
     API directly, already removed in pass 1 above).
  - **What stays, untouched:** the live restriction-fetch layer itself
    (`ConfluenceCloudSource`/`ConfluenceDataCenterSource`, `PageRestriction`, `ReadRestriction`) and
    page-hierarchy ancestor tracking (`ConfluenceAccess.AncestorIds`) — lower-level, independently
    tested concerns, just no longer consumed for classification/withholding. Also untouched:
    `ConfluenceAccessPrincipals` (still used for space-level evidence-access tagging) and core page
    ingestion itself.
  - **Verified safe, both passes:** full solution rebuild succeeds (0 errors) after each; the full
    `VisionIQ.Ingestor.Tests` suite (295/295) and `VisionIQ.Api.Tests` suite (260/260) pass for real
    after both removals. Confluence evidence has no registered access verifier at all (pass 1), so
    `ProviderRoutingAccessVerifier` fails closed on it by design — unreadable through the graph API,
    not insecurely exposed, regardless of what ingestion-time classification does or doesn't do.
  - **Tracked separately:** a standalone Trello card
    ([LEARN FOLLOW-UP] LRN-02 — ADR and Confluence restricted-user acceptance testing) now covers
    re-implementing all three removed pieces from scratch later, rather than "finish testing what's
    already there" — because there is no longer anything there. The original LRN-02 story card's
    acceptance-criteria checkbox for "a page the engineer can't open in Confluence is never shown to
    them" points at that follow-up card instead of claiming it's covered.
- **Out of scope for LRN-02's revised MVP closure (explicit team decision, 2026-11-09):** there is
  still no user-facing surface (BRIEF retrieval / search) that returns Confluence evidence to an
  end user — LRN-08/09's job, not LRN-02's. Not a blocker for closing LRN-02 under the revised
  scope below.

**4. How it fits the LEARN phase:** Second connector in the Phase 1 "always on" ingestion layer,
alongside LRN-01 (ADO). LRN-03's versioned-evidence storage and LRN-06/07's graph layer both
consume Confluence evidence once ingested; downstream retrieval (LRN-08/09) is a separate story
that will need a rebuilt authorization check and a delivery surface before Confluence evidence can
ever reach an end user.

**5. Testing status: CLOSED under the revised MVP scope (2026-11-09).** Final closure verification
ran 42/42 real tests, 0 failures, against the 7 revised acceptance requirements (ordinary-page
ingestion without ADR/ACL inputs; content/ID/title/URL/version/timestamp correctness; canonical
PostgreSQL+GCS persistence; incremental sync with no duplicate revisions; retry/failure/checkpoint
handling; configurable URL/space/credentials with no hardcoding; approved-space boundaries with no
unintended exposure) — see `ConfluenceTests`, `ContentRevisionTests`, `CanonicalEvidenceSinkTests`,
`TimeoutHandlingTests`, `StreamIsolationTests`, `ConfluenceConcurrencyTests`,
`ConfluenceReviewRegressionTests`, `ConfluenceEvidenceAccessTests` (Ingestor, 37/37) and
`ConfluencePrincipalTests`/`GraphApiAuthorizationTests` (API, 5/5). Canonical PostgreSQL+GCS
persistence is additionally backed by this session's earlier real 589-page canonical run — the
page-write path was never touched by either removal pass (canonical mode already wrote every page
unconditionally before today). One regression test fixed along the way:
`Reports_from_both_runners_merge_into_one` had come to depend on Confluence being incidentally
`Partial` (a side effect of the now-removed withholding); rewritten to use a genuinely denied
space, 0 production-code behavior change. ADR/decision-page classification, live per-user
authorization, and restricted-page ACL classification/withholding remain **removed from the
codebase** (not deferred) under the explicit business assumption that everyone has Confluence
access for this release; rebuilding them is tracked entirely on the separate follow-up Trello card,
outside LRN-02's revised acceptance.

---

## LRN-03 — Versioned Evidence

**1. What it is:** The storage layer both history connectors (LRN-01 ADO, LRN-02 Confluence) write
into — keeps every change to a ticket, PR, commit or page as its own immutable revision instead of
overwriting the latest text, so "what was known when" is never lost.

**2. What it does:**
- Combines provider/source/project/item-kind/external-ID into a stable item identity so different
  sources never collide
- Archives the raw bytes to Cloud Storage first (SHA-256 content-addressed, create-only — never
  overwritten), then records the PostgreSQL revision only if the archive write succeeded
- `EvidenceNormalizer` copies only an allowlist of short structured fields into searchable
  metadata; descriptions, comments, commit messages and raw environment text never enter it
- Upsert semantics: replaying an unchanged revision is a no-op, a genuinely new revision gets its
  own row and moves the "latest" pointer, and the same revision ID with different content is a
  **Conflict** — never a silent overwrite
- **Built today: each PR reviewer's vote is its own versioned evidence item** (`PullRequestReviewRecord`,
  evidence kind `review`), independent of the PR's own revision cadence — a vote can change without
  the PR gaining a new native revision, so without this a reviewer's history was only ever visible
  as of the PR's last fetched snapshot. ADO already fetched `Reviewers`/`ReviewThreads` on every PR
  (used for lifecycle/outcome logic); what was missing was persisting each vote as its own auditable
  record. Status label (`approved`/`approved_with_suggestions`/`no_vote`/`waiting_for_author`/
  `rejected`/`declined`) and raw vote are the only redacted/indexed fields — reviewer identity stays
  out of searchable metadata, consistent with the rest of this story's redaction policy.

**3. Pending / not yet tested:**
- **Checked against the LRN-03 Trello card's own task list, not assumed elsewhere:** of 7 listed
  implementation tasks (S05-01, -02, -03, -04, -05, -06, -11), 6 are now fully done; **1 remains
  partially done**, and the missing part is real, unbuilt work, not just untested:
  - **S05-05 "Normalize PR, commit and review artifacts"**: ✅ now fully done — PR, commit, and
    review-vote normalization all exist and are proven (see below).
  - **S05-06 "Normalize CI, deployment and incident artifacts"**: CI (`ci_run` from ADO builds) is
    done; **deployment and incident artifacts have no connector and no evidence kind at all** —
    not partially built, not started. **Explicit decision (2026-11-09): deferred for now**, tracked
    in the Pending Work Tracker below — not reintroduced without a separate decision to build it.
  - Counted by actual sub-deliverable (11 total across all 7 tasks, not 7 equal units): **9/11 done,
    ~82% complete.**
- **Already correctly out-of-scope for LRN-03 itself:** no deployed retrieval/search index exists
  yet to validate the redaction boundary end-to-end through an actual query — that's LRN-08/09's
  job, not an LRN-03 regression.

**4. How it fits the LEARN phase:** The shared evidence spine underneath LRN-01/02 — every other
story (LRN-04 ticket-fix links, LRN-06/07 graph, eventually LRN-08/09 retrieval) reads from the
`evidence.source_items`/`evidence.source_revisions` tables this layer writes.

**5. Testing status:** **Everything implemented is proven with real evidence.** Real disposable
PostgreSQL (`Lrn03PostgresIntegrationTests`, fresh container): Created → NewRevision → Unchanged →
Unchanged → Conflict, plus full field-level persistence checks (timestamps, ACL principals,
redacted JSON, GCS ref, content hash) — **1/1 passed**, 0 leftover rows, container torn down. Real
Google Cloud Storage (`Lrn03GcsArchiveIntegrationTests`, against the actual configured bucket
`visioniq-source-evidence-dev`): exact bytes, SHA-256 match, idempotent re-store (same object
generation) — **1/1 passed**, test object deleted after. Redaction boundary (8/8). **New today:**
review-vote normalization — `ReviewVoteStatus.Of` vote-to-status mapping (7/7 cases including a
declined reviewer), a real `SyncRunner` run writing one versioned `pull-request-reviews` item per
reviewer with correct project/repository/vote/status (1/1), a repeat-sync idempotency check (1/1),
and a `CanonicalEvidenceSink` mapping test confirming kind `review`, ADO-project-scoped access, and
that reviewer identity never leaks into redacted/indexed metadata (1/1) — **10/10 new tests pass**.
Full regression suite: **305/305 Ingestor, 260/260 API.** **~82% of LRN-03's own task list is
complete** (9/11 sub-deliverables); deployment-artifact and incident-artifact normalization remain
an explicit, deferred gap — tracked in the Pending Work Tracker below, not reintroduced without a
separate decision.

---

## LRN-04 — Link Each Ticket to the Fix

**1. What it is:** The association layer that connects each ticket (work item) to the PR and commits that fixed it, with provenance tracking and revert detection so VisionIQ knows "what really worked."

**2. What it does:**
- Extracts explicit references from ADO work items (artifact links to PRs/commits) and commits (ticket mentions and git-revert markers)
- Classifies each link as Explicit (directly stated), Inferred (mentioned in commit), or Unknown (incomplete PR not yet merged)
- Stores associations in PostgreSQL with lifecycle event logging (retraction, relink, revert)
- Detects reverts via exact Git SHA (`git revert` message format) and PR-title matching
- Integrates with graph materialization so graph can traverse ticket→fix→commit→file paths
- Feeds Python retrieval code for AI lookups

**3. Pending / not yet tested:**
- **Infrastructure verified in code (2026-11-09), not yet validation-tested:**
  - `TicketFixProjectionSink.cs` (294 lines) exists and is wired into production sink chain in `Program.cs`
  - On PR/work-item write: calls `LinkExtractor`, resolves candidates, calls `ReconcileSourceAsync` with complete snapshots
  - On commit write: extracts mentions (Inferred), calls `RecordRevertAsync` for explicit reverts
  - Title-based revert detection: tracks PR history, resolves "Revert 'title'" PRs when exact title matches (process-scoped, not persistent)
  - Downstream readers exist: `TicketFixGraphMaterializer.cs` materializes ticket-fix edges, `PostgresRelationshipGraphStore.cs` traverses them, `app/knowledge/retrieval.py` queries them
  - Tests exist: `TicketFixProjectionSinkTests.cs` (80+ lines) validates projection logic
- **Validation pending (critical, not building):**
  - Real-data trace: Sync Task 116844 → confirm PR 38561 stored in `evidence.ticket_fix_links` with correct ticket ID, commit SHAs, Explicit classification, evidence references
  - Graph materialization: Confirm `TicketFixGraphMaterializer` builds correct edges from stored associations
  - Retrieval: Confirm `retrieval.py` queries return ticket-fix relationships correctly, including disputed/reverted filtering
  - Cross-run revert detection: Sync original PR (run 1), sync revert commit in separate run (run 2) → confirm revert detected via exact SHA
  - Review verdict workflow: Decide operationally how Verified/Disputed decisions are submitted (UI? API? Manual?)
- **Known limitation (design, not defect):**
  - PR-title-based reverts: in-memory `pullRequestsByTitle` dictionary is process-scoped (one CLI run, one webhook session)
  - A revert PR synced hours/days later in a separate run won't detect the original via title
  - Workaround: exact SHA-based `git revert` messages are always detected (not limited)
  - Fix would require persistent title index (migration needed, deferred)

**4. How it fits the LEARN phase:** Mid-tier intelligence (LRN-04, 05, 06, 07). LRN-04 establishes the fundamental ticket-to-fix relationship; LRN-06/07 build the graph that traverses it; LRN-10+ use it to group repeated failures into lessons.

**5. Testing status: Infrastructure complete and wired; end-to-end validation pending.** All components verified in code:
- ✅ `TicketFixProjectionSink.cs` — 294 lines, all three projection methods (work items, PRs, commits)
- ✅ `Program.cs` — sink wired into production chain (line 226)
- ✅ `TicketFixProjectionSinkTests.cs` — projection logic unit-tested
- ✅ `PostgresTicketFixLinkStore.cs` — 900-line store with reconciliation, revert, review handling
- ✅ `TicketFixGraphMaterializer.cs` — graph edges built from associations
- ✅ `PostgresRelationshipGraphStore.cs` — graph traversal reads ticket-fix status
- ✅ `app/knowledge/retrieval.py` — retrieval code queries associations
- ❓ **Real-data validation needed:** Task 116844 + PR 38561 traced through entire pipeline (ingestion → PostgreSQL → graph → retrieval)
- **Trello status:** "To be Tested" (infrastructure complete, validation pending)

---

---

## LRN-05 — Load Full History and Keep It Current

**1. What it is:** The operational pipeline that backfills years of historical data from approved ADO sources, delivers new/changed items through events, and maintains a quarantine list for items that fail to ingest.

**2. What it does:**
- Initial backfill: reads all historical data from ADO sources (work items, PRs, commits) since checkpoint
- Checkpoint management: tracks cursor per source, advances only through contiguous successful prefix (no gaps)
- Event-driven delta: webhooks deliver new/changed items immediately
- Periodic reconciliation: scheduled runs catch any missed changes
- Failed-item handling: quarantines items with `reason_code` and attempt count instead of stopping the run
- Count validation: confirms expected count matches received (B-17 contract)

**3. Pending / not yet tested:**
- **Core infrastructure verified in code (2026-11-09):**
  - ✅ Python `BackfillReconciler` class (reconcile.py) — orchestrates batches, manages checkpoints, quarantines
  - ✅ .NET `ISyncQuarantine` — interface, `InMemorySyncQuarantine` (JSONL/tests), `PostgresSyncQuarantine` (production)
  - ✅ `.NET backfill infrastructure — `PostgresSourceSyncRunStore`, `SourceChangeProcessor`, change handlers for PR/work-item
  - ✅ Webhook wiring — `AdoWebhookEndpoint` in production sink chain
  - ✅ Database schema — migrations for `backfill_event_inbox`, `sync_quarantine`
  - ✅ Tests exist — `test_backfill_reconciliation.py`, `SourceChangeDatabaseIntegrationTests.cs`, `test_sync_quarantine_db.py`
- **Enhancement staged (not committed):**
  - Targeted reindexing: `--only ITEM_KEY` to reindex specific items, `--limit N` for batch size
  - Tests written for CLI contracts, not yet run
  - This enhancement is separate from MVP, can be committed after real-data validation
- **Real-data validation pending (critical):**
  - Backfill completes for all approved projects with correct counts per source
  - Checkpoint advances correctly through success and failure
  - Failed items correctly quarantined (not stored raw)
  - Repeat sync handles replays (idempotency)
  - Event-driven delta catches new changes within one cycle
  - Checkpoint remains intact through reconciliation

**4. How it fits the LEARN phase:** Operational foundation (LRN-05, mid-tier). LRN-01/02/03 collect evidence; LRN-05 keeps it complete and current; LRN-06/07/08+ consume it.

**5. Testing status: Core implementation complete; end-to-end validation pending.** All four S-tasks addressed:
- ✅ S05-12: Initial backfill — `BackfillReconciler.run_one()` + source adapters
- ✅ S05-13: Delta and reindex — `SourceChangeProcessor` + webhook handlers + event inbox
- ✅ S05-14: Audit and dead-letter queue — `PostgresSyncQuarantine` + attempt tracking + `SourceSyncAuditProjector`
- ✅ S05-15: Historical completeness — contiguous cursor advancement (B-17), count validation tests
- ❓ **Validation needed:** Real-data run against approved ADO scope (not full historical backfill, controlled scope)
- **Trello status:** "To be Tested" (infrastructure complete, validation pending)

---

## LRN-06 — Understand the Code

**1. What it is:** The code intelligence layer that parses repository files to extract symbols, imports, and lexical references so VisionIQ can warn engineers about related code areas affected by a change.

**2. What it does:**
- Scans a repository at a specific Git commit (not working tree)
- Detects file languages by extension (.py, .cs, .js, .ts, .java, .go, .rs, .sql, .vue)
- Recognizes manifest files (go.mod, package.json, pyproject.toml, etc.)
- Parses Python files via standard AST library
- Parses C# files via Tree-sitter (grammar pinned in requirements.txt)
- Extracts symbols (classes, functions, async functions, test functions)
- Extracts imports (including relative import dots)
- Extracts lexical call references (method names, not verified call graph)
- Handles parse failures gracefully: returns PARTIAL status + last_good result instead of crashing
- Never invents symbols or executes code

**3. Pending / not yet tested:**
- **Core infrastructure verified in code (2026-11-09):**
  - ✅ `scanner.py` (300+ lines) — Git file inventory, commit SHA handling, language detection, manifest detection
  - ✅ `parsers.py` (200+ lines) — Python AST parser, C# Tree-sitter adapter, symbol/import/call extraction
  - ✅ Test detection — "test" symbol kind for pytest/unittest functions (test_ prefix or _test.py)
  - ✅ Fail-closed design — IncompleteParseError keeps last_good result, never crashes or invents
  - ✅ `ParserRegistry` abstraction for multiple language adapters
  - ✅ Data structures: `GitFile`, `FileAnalysis`, `RepositoryScan` with blob SHAs
- **Tests exist:**
  - `test_code_scanner.py` — Git inventory, commit handling
  - `test_csharp_parser_structure.py`, `test_csharp_parser_native_stability.py` — C# parser
  - `test_code_intelligence_integration.py` — end-to-end
  - `test_scanner_logging.py` — diagnostic logging
- **Known design decisions:**
  - Lexical call references only (not verified call graph via type inference)
  - No Roslyn for C#; Tree-sitter instead
  - No automatic language guessing; extension-based only
  - Symbols are declared, not inferred

**4. How it fits the LEARN phase:** Intelligence layer (LRN-06, alongside LRN-04/05). LRN-01/02/03 collect evidence; LRN-04 links tickets to fixes; LRN-05 keeps it current; LRN-06 understands code structure; LRN-07 builds the graph.

**5. Testing status: Core implementation complete; real-repository validation pending.** All framework tasks addressed:
- ✅ S06-03: Repository scanner — Git inventory, ref handling, manifest detection
- ✅ S06-04: Parser-adapter interface — `ParserRegistry`, extensible architecture
- ✅ S06-05: File-level fallback — PARTIAL status when parser fails
- ✅ S06-06: Parser for demo repo language — Python AST + C# Tree-sitter
- ❓ S06-07–S06-13: Dependency relationships, diff-to-symbol mapping, cross-project deps — **status unclear from code inspection**
- **Validation needed:** Run parser on actual demo repository, verify symbol extraction accuracy, confirm parser crash isolation
- **Trello status:** "To be Tested" (framework + 2 parsers verified in code, remaining s-tasks unclear)

---

## Pending Work Tracker

| Story | Gap | Action needed | Owner |
|---|---|---|---|
| LRN-03 | Deployment-artifact and incident-artifact normalization (S05-06's two remaining parts) not built at all: no connector, no evidence kind | Explicitly deferred for now (2026-11-09 decision) — build later if/when a deployment or incident-management connector exists | Deferred, Trello-tracked |
| LRN-08/09 | No retrieval/BRIEF delivery surface exists yet that would consume Confluence (or any) evidence | Build the retrieval surface (LRN-08/09 work); then rebuild Confluence read authorization (tracked on the separate LRN-02 follow-up card) and test it end-to-end | Pending — not an LRN-02 blocker, LRN-02 is CLOSED |

**Closed today:** LRN-01's `DbCheckpointStore` monotonic-guard gap — real disposable-Postgres
integration test added and run (3/3 passed), wired permanently into `scripts/learn_e2e.py`. No
Cloud SQL connectivity was needed; see LRN-01's section 3 above for the corrected reasoning.
**Also closed today:** LRN-03's review-artifact normalization (S05-05's third part) — built and
proven with 10/10 new tests; see LRN-03's section above.

---

## Scope Decisions (deferred, not removed — tracked in Trello)

| Story | Feature | Decision | Trello card |
|---|---|---|---|
| LRN-02 | ADR/decision-page classification, live per-user Confluence authorization | **Removed from codebase** (2026-11-09) — not deferred, code deleted; to be rebuilt from scratch later | Created (separate follow-up card, tracks rebuild) |
| LRN-02 | Restricted-page ACL classification and withholding | **Removed from codebase** (2026-11-09) — business assumption: everyone has Confluence access for this release; same follow-up card tracks rebuilding this alongside the authorization check | Created (same follow-up card) |
| LRN-03 | Deployment-artifact and incident-artifact normalization (S05-06) | Deferred for this release (2026-11-09) — no deployment/incident connector exists yet; build later if one is added | Tracked |
