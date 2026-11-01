# LEARN Quality & Acceptance Matrix — LRN-01 to LRN-07

The quality contract between the implementing AI, the reviewing AI and the product owner. It maps every
acceptance criterion to its implementation, the automated evidence that runs in CI, and the manual check that
remains. **Nothing here is "Accepted"**: acceptance happens only after hands-on testing on a fixed commit.

- Criteria source: Trello export `AaucJawp_-_defect-intelligence_latest.json` (all seven cards in **Doing**;
  criteria unchanged from the earlier export).
- Code: `feature/learn`; draft PR #14. No merge to `main`, no Google Cloud deployment.
- CI (`.github/workflows/pr-validation.yml`): .NET build and tests, Python tests and lint, Docker image builds,
  deployment-script safety, and a fresh-PostgreSQL job running migrations twice plus the database tests.

**Status legend.** *Automated*: implemented and covered by tests that run in CI. *Manual*: needs a real
ADO/Confluence/GCP check (listed in the last column). *Gap*: not met by the code yet.

## LRN-01 — Connect to the history sources

| Criterion | Implementation | Automated evidence | Manual check | Status |
|---|---|---|---|---|
| Work items, PRs, commits, changed files and CI runs are read for every approved project | `SyncRunner`, ADO sources; all changed files paged (M-6); repository kept on links (B-11) | `SyncRunnerTests`, `AdoSourceTests`, `CommitChangesPagingTests`, `WiqlCapTests` | Sync report with counts per source on the sandbox | Automated + Manual |
| An unreachable source is NOT SYNCED, never empty | Timeouts become source errors (B-2/B-3); each stream isolated (B-4); audit records received vs stored (M-12) | `TimeoutHandlingTests`, `StreamIsolationTests`, `SyncAuditCountTests` | Revoke the PAT for one project and confirm NOT SYNCED | Automated + Manual |
| An interrupted sync resumes from its checkpoint without duplicates | Checkpoints only after complete batches; capped batches save progress (B-10); idempotent evidence | `WiqlCapTests`, `CheckpointKeyTests`, replay tests in `ContentRevisionTests` | Kill a sync mid-run, rerun, check no duplicate rows | Automated + Manual |

Completion evidence required: *sync report with counts per source, and a resumed run with no duplicate rows.*

## LRN-02 — Include design decisions from Confluence

| Criterion | Implementation | Automated evidence | Manual check | Status |
|---|---|---|---|---|
| Pages are stored with version and page permissions | Page versions, labels and versions paged (M-15); ACL snapshot per page; ancestor loop guard (M-2) | `ConfluenceTests`, `ConfluenceEnrichPagingTests`, `ConfluenceAncestorLoopTests` | Import a real page, check version and ACL | Automated + Manual |
| A page the engineer can't open is never shown to them | Live Confluence Cloud permission check per user and page (space, page, ancestors); only a space-level candidate is stored; deny on any error | `ConfluencePageAccessTests`, `ConfluenceEvidenceAccessTests`, `ConfluencePrincipalTests` | Restricted page hidden from an unauthorized user, visible to an authorized one | Automated + Manual |

Prerequisites before this can pass: `CONFLUENCE_IDENTITY_MAP_FILE` (e-mail to Atlassian account ID) — a decision
for your Atlassian administrators. **Data Center pages stay hidden** (no permission-check endpoint).

## LRN-03 — Store history as versioned evidence

| Criterion | Implementation | Automated evidence | Manual check | Status |
|---|---|---|---|---|
| Source ID, revision, timestamps and access list per record | `CanonicalEvidenceSink`, `PostgresEvidenceStore`; link times from the source (m-10) | `CanonicalEvidenceSinkTests`, `EvidenceDatabaseIntegrationTests` (CI) | Inspect stored rows after a real sync | Automated + Manual |
| Raw copy in Cloud Storage; sensitive fields redacted before indexing | Immutable GCS archive; redaction allowlist (B-13); reused client (M-5) | `CanonicalEvidenceSinkTests` (redaction) | Confirm objects in the real bucket | Automated + Manual |
| Re-importing the same item updates it, no duplicate | Content-addressed revisions (B-9); latest ordering (B-8); reverted content (N-7); deleted items hidden (N-2) | `ContentRevisionTests`, `test_latest_revision_tiebreak_db.py`, `test_tombstones_db.py` (exact .NET SQL) | Import the same item twice: one record, two revisions | Automated + Manual |

## LRN-04 — Link each ticket to the fix that closed it

| Criterion | Implementation | Automated evidence | Manual check | Status |
|---|---|---|---|---|
| Links only from explicit references; time never creates a link | `LinkExtractor`, `TicketFixResolver`; commit mentions are *inferred*, not proof (M-7) | `LinkExtractorTests`, `TicketFixResolverTests`, `TicketFixProjectionSinkTests` | — | Automated |
| Each link is explicit / inferred / verified / disputed / unknown | Source classification plus review status | `TicketFixDatabaseIntegrationTests` (CI) | — | Automated |
| One ticket links to several fixes and a later revert | Multiple associations per ticket; `git revert` detection | `TicketFixResolverTests` | **M-8:** ADO's Revert button message format | Automated + Manual |

Completion evidence required: *five known ticket → fix links reviewed by the team.*

## LRN-05 — Load full history and keep it current

| Criterion | Implementation | Automated evidence | Manual check | Status |
|---|---|---|---|---|
| Initial backfill for all approved projects, counts per source | `sync` with audit rows per stream | `SyncRunnerTests`, `SourceSyncRunDatabaseIntegrationTests` (CI) | Backfill report on the sandbox | Automated + Manual |
| New/changed items arrive through events plus periodic reconciliation | `serve-webhooks` (ADO Service Hooks, re-fetch, idempotent inbox, status codes verified over real HTTP); `sync --reconcile-days N` never moves checkpoints | `WebhookAndReconcileTests`, `WebhookHttpTests`, `DeletionTests` | A changed ticket appears within one cycle; real Service Hook subscription | Automated + Manual |
| Failed items go to a quarantine list instead of stopping the run | Events: inbox quarantine and dead-lettering after 5 attempts (M-11). Sync: failed items are counted, reported and retried (checkpoint held) | `SourceChangeProcessorTests`, `SyncAuditCountTests` | — | **Gap G-1** (sync path) |

**G-1:** the scheduled sync does not keep a per-item quarantine *list* of which items failed; it counts them,
reports them and retries them. Scheduling infrastructure (Cloud Run Jobs, Scheduler, Pub/Sub) is FND-06.

## LRN-06 — Understand the code

| Criterion | Implementation | Automated evidence | Manual check | Status |
|---|---|---|---|---|
| Demo repo scanned into files, symbols and dependencies | Python and C# parsers with namespaces, usings, properties (M-9), test methods; import resolution (B-12) | `test_code_scanner.py`, `test_csharp_parser_structure.py`, `test_import_resolution_paths.py` | Symbol list for the demo repo | Automated + Manual |
| A language without a parser falls back to file level; symbols never invented | `UNAVAILABLE` file-level evidence; grammar gaps degrade one file (N-16) | `test_code_scanner.py`, `test_grammar_gaps.py` | — | Automated |
| A parser crash marks the repo PARTIAL and keeps the last good result | Crashes are `FAILED`; tree-sitter pinned to 0.25.2 (B-1); systematic grammar gaps also PARTIAL | `test_csharp_parser_native_stability.py`, `test_grammar_gaps.py`, `test_code_canonical_db_integration.py` | Forced crash on the demo repo | Automated + Manual |

## LRN-07 — Build the engineering relationship graph

| Criterion | Implementation | Automated evidence | Manual check | Status |
|---|---|---|---|---|
| Nodes and edges follow one taxonomy | Typed enums, database CHECK constraints, endpoint rules per relation (M-3) | `GraphRelationEndpointTests`, migration tests in CI | — | Automated |
| Renames and reverts update edges without losing history | `file_renamed` edges stay visible after later scans (M-16); revert detection; deleted items hidden (N-2) | `test_file_rename_edges.py` (exact .NET SQL), `test_tombstones_db.py` | Rename a file in the demo repo | Automated + Manual |
| Queries return only items the requester may see | Graph API: Entra ID delegated tokens, approved caller, live ADO membership per item (B-5/B-6), code evidence by project (N-17), Confluence live check; identical 404 for not found / not allowed; no hidden counts | `GraphApiTokenTests` (real Microsoft library, CI), `GraphApiAuthorizationTests`, `GraphQueryServiceTests`, `AdoEvidenceAccessTests`, `CodeScanAccessTests` | Same query hides a restricted node; real Entra app registration | Automated + Manual |

Completion evidence required: *a query from a defect to its fix and test, and the same query hiding a restricted
node.* Test evidence levels are kept separate: structural (file contains test), changed-with-fix (candidate),
declared ADO "Tested By", mapped automated test; test results are not ingested and never implied.

## Review findings

| Set | Fixed | Open |
|---|---|---|
| Blocking (B-1..B-17) | 17 / 17 | — |
| Medium (M-1..M-16) | 14 / 16 | **M-4** performance (benchmark on representative data), **M-8** ADO Revert button (needs a real revert) |
| Minor (m-1..m-12) | 12 / 12 | — |
| Follow-ups (N-1..N-18) | N-2, N-3, N-7, N-8, N-16, N-17, N-18 | see below |

## Open items and risks

| ID | Item | Owner |
|---|---|---|
| N-1 | Independent review of every commit since `283dde9` by the reviewing AI | Reviewing AI |
| N-4 | Protect `main` (PR, approvals, required CI checks) | Product owner |
| G-1 | Per-item quarantine list for the scheduled sync (LRN-05) | Implementation |
| N-5 | ADO membership cache: revocation can lag 5 minutes | Decision |
| N-6 | Project members not in any ADO team are denied | Manual test |
| N-9 | More than 20,000 work items with one timestamp can stall | Implementation |
| N-10 | FND-04 deployment guide still expects database version 0006 (now 0011) | FND-04 owner |
| N-11..N-14 | CI on locked dependencies, lock-sync check, NuGet lock files, vulnerability and secret scanning | Implementation |
| N-15 / SP-1 | Observability: metrics, correlation IDs, alerts; fail-closed access checks deny silently | Implementation |
| M-4, M-8 | See above | Benchmark / manual test |
| — | Entra app registration and On-Behalf-Of setup (`docs/graph-api.md`) | Entra administrators |
| — | Confluence identity map (`CONFLUENCE_IDENTITY_MAP_FILE`) | Atlassian administrators |
| — | Retention policy for raw archives of deleted items | Product owner |

## Quality gates (proposed by the reviewing AI) — current position

| Gate | Position |
|---|---|
| Critical/high security defects unresolved | None known after the second-pass review; independent review (N-1) pending |
| Schema and migrations: fresh and upgrade paths | Verified fresh and as upgrades for every new migration (0007-0011); CI runs migrations twice |
| Database tests never silently pass | Enforced (M-10): skipped without a database, run in CI |
| Unauthorized access denied | Covered by token, authorization, membership, code and Confluence tests |
| Replay and idempotency | Covered (content revisions, replay guards, inbox) |
| Provenance for surfaced relationships | Every edge cites a stored evidence revision |
| Performance benchmark | Not done (M-4) |
| Hands-on acceptance | Not started: follows this matrix on a fixed commit |
