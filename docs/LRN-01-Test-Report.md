# LRN-01 — Azure DevOps historical ingestion acceptance report

**Project:** VisionIQ · **Branch:** `feature/learn` · **Review PR:** #14 (DRAFT; covers LRN-01 through LRN-07)  
**Evidence date:** 2026-11-01 · **Status:** Work-item ingestion tested; LRN-01 final acceptance **pending** Git/commit/PR live verification, expanded source fidelity and relevant database integration gates.

## Scope and environment

- Source organization: `https://dev.azure.com/WLMTS`, project `Genoa Kiosk`.
- Ingestion output: local JSONL; checkpoint store: local file. No claim of canonical PostgreSQL/GCS ingestion verification.
- Azure DevOps access at test time: **Stakeholder**; project and work-item reads succeeded, repository access not available. Access restoration requested.
- Run output under `artifacts/ingestion/lrn01-manual/`. Evidence files include per-run `report.json` and JSONL streams; checkpoint at `artifacts/ingestion/lrn01-manual/checkpoints.json`.
- No PATs or work-item content included in this report.

## Executed real-source work-item tests

| Check | Observed result | Status |
|---|---|---|
| ADO inventory | 4,436 work items discovered; 22 pipeline definitions; zero visible repositories | **Partial** — Git access blocked; pipeline ingestion not exercised |
| Initial delta sync (2026-10-01) | 363 work items stored, 980 links, zero not-stored/quarantined; exit 0 | **Pass** |
| Export content | 363 work-item lines, 5,141 revision lines, 980 link lines, 415 deletion-marker lines; parseable | **Pass, structural** |
| Current items | No empty envelope keys, timestamps or records; no duplicate keys/IDs; no invalid IDs, revisions, dates or project assignments | **Pass** |
| Historical revisions | 5,141 parsed; no missing ID, revision, changed date; no duplicate (ID, revision) pairs or unmatched current IDs | **Pass, structural** |
| Revision continuity | All 363 items have histories; no internal gaps or max-revision mismatch | **Pass for exported set**, not independently proof against source |
| Links | 980 parsed; no missing endpoints/types/keys or duplicate envelope keys; 671 work-item relations, 286 artifacts, 23 tested-by | **Pass, structural** |
| Link namespaces | All outer keys end with nested key, with consistent 27-character prefix; semantics require code contract confirmation | **Pass for format consistency** |
| Link targets | 694 WorkItem, 155 Commit, 131 PullRequest; commit/PR content not yet read | **References only** |
| Deletions | 415 markers; no duplicate IDs, invalid timestamps/project IDs or overlap with 363 exported active records | **Pass for exported set** |
| No-change replay | Second run stored 0 records, 0 links, exit 0 | **Pass** |
| Source authentication failure | Deliberately invalid `ADO_READ_PAT`; `NotSynced`, exit 2, no records and explicit recycle-bin failure | **Pass** |
| Recovery | Original credential restored; `Synced`, exit 0, 0 replayed; cursor unchanged | **Pass** |

### Checkpoint evidence

Saved cursor `2026-10-30T17:42:47.2770000Z|116507` persisted through the authentication failure and subsequent recovery. Checkpoint `UpdatedAt` changed on successful no-change run while cursor did not; this is not established as a defect.

### Manual source-fidelity sampling

Five exported records selected across the change-date range:

| Work item | Exported revision | Exported state | Exported revision rows |
|---|---:|---|---:|
| 107328 | 16 | New | 16 |
| 106675 | 13 | Ready | 13 |
| 115804 | 5 | Closed | 5 |
| 116460 | 6 | Closed | 6 |
| 116507 | 2 | New | 2 |

The user manually checked **state/status** and **changed dates** in Azure DevOps and reported matches. Subsequent read-only Azure DevOps REST API comparisons verified that all five exported revision numbers exist and equal the current latest revisions, and that **work-item type and full title matched exactly** for each sampled item. Historical revision content (earlier states/titles/reasons) has **not** been independently compared to the source; structural row-count checks do not establish historical-content fidelity.

| Work item | Type | Type match | Title match | Revision match | Revision exists/latest |
|---|---|---|---|---|---|
| 107328 | User Story | Yes | Yes | Yes | 16 / 16 |
| 106675 | Test Case | Yes | Yes | Yes | 13 / 13 |
| 115804 | Task | Yes | Yes | Yes | 5 / 5 |
| 116460 | Bug | Yes | Yes | Yes | 6 / 6 |
| 116507 | Bug | Yes | Yes | Yes | 2 / 2 |

**Work-item types in the 363-record export:** Test Case 152; User Story 96; Bug 54; Task 46; Feature 8; Test Suite 7. The LRN-01 WIQL intentionally ingests all work-item types; preserving a source type does not itself make a non-Bug item defect evidence. Dedicated downstream defect-only classification/search behavior remains a separate validation requirement.

**Additional test evidence:** 41/41 targeted AdoWorkItemReader/DefectEnvironmentClassifier tests passed (0 skipped, exit 0). These tests do not alone prove downstream defect-only search filtering.

## Code fixes verified

- **LRN01-F01 closed:** merged sync report previously serialized `Guid.Empty`. Fixed `SyncReport.Merge` and added regression coverage. Verified live run ID `1796f921-3d4c-4f0e-aa5f-5a9cc3a93954` matches console and `report.json`.
- **LRN01-F02 closed for ingestor:** console logger previously printed `System.Collections.Generic.Dictionary...`. Structured, readable run scope now displays `runId=... source=ado`; validated live and by regression tests. Separate API logging scope behavior is not addressed by this finding.

## Automated acceptance evidence

| Test execution | Total | Passed | Failed | Skipped | Result |
|---|---:|---:|---:|---:|---|
| New mid-stream recovery and multi-project fixtures | 2 | 2 | 0 | 0 | Pass |
| Full ingestor tests | 231 | 231 | 0 | 0 | Pass |
| Targeted security/resilience tests | 25 | 25 | 0 | 0 | Pass |
| `dotnet test VisionIQ.sln --configuration Release --no-build` | 388 | 381 | 0 | 7 | Pass with database-test omissions |

The targeted executions overlap the full suite; **do not sum their counts**. Two additional tests validate that a simulated mid-stream sink failure does not advance the checkpoint and is safely retried in a deduplicating test sink, and that two simulated ADO projects retain independent scopes/keys/checkpoints. These do not prove live multi-project isolation or PostgreSQL transaction semantics.

Seven opt-in PostgreSQL integration tests were skipped in the solution run: graph (2), source-change inbox (1), source-sync audit (1), evidence immutability (1), ticket-fix history (1) and database readiness (1). Their ownership spans LRN-01–07; determine required story gates and run with **disposable** PostgreSQL and explicit test environment flags; do not point tests at production or a shared development database.

## Additional regression cases added — execution pending

Commit `07651d9` adds six parameterized `AdoWorkItemReaderTests` covering source type/title preservation across Bug, User Story, Task, Test Case, Feature and Test Suite, plus one historical-revision parse case covering ID, revision, state, title, reason and changed date. **These seven test cases have not yet been executed in the local environment and must not be counted as passing.** They test parsing and source type preservation, not downstream defect-specific retrieval.

Next run: `dotnet test .\\tests\\VisionIQ.Ingestor.Tests\\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter "FullyQualifiedName~AdoWorkItemReaderTests"`.

## Historical content source-fidelity verification — PASSED (five sampled revisions)

A read-only Azure DevOps REST comparison against the local `work-item-revisions.jsonl` export confirmed exact matches for all four stored historical fields in each of five sampled earlier revisions:

| Work item | Revision | State | Title | Reason | Changed date |
|---|---:|---|---|---|---|
| 107328 | 1 | Match | Match | Match | Match |
| 106675 | 5 | Match | Match | Match | Match |
| 115804 | 2 | Match | Match | Match | Match |
| 116460 | 3 | Match | Match | Match | Match |
| 116507 | 1 | Match | Match | Match | Match |

**Result:** 20/20 field comparisons match across 5/5 sampled historical revisions, with no missing local revision or ADO lookup failure reported. This completes the **sampled historical revision fidelity** acceptance check; it is not a comparison of every one of the 5,141 exported revisions.

The seven newly added type/history regression cases completed with exit code 0 in local tests; the full test-count summary was not supplied for that execution.

## Isolated PostgreSQL integration — PASSED

On 2026-11-01, a separate Docker Compose project `visioniq-lrn01-test` was initialized with PostgreSQL 16 + pgvector, a dedicated volume `visioniq-lrn01-test_pgdata`, and host binding `127.0.0.1:15543`. The isolated migration logs reported: `Database roles and schema permissions configured.` and `pgvector, full-text search and role isolation verified.`

An initial database test run reported **9 succeeded / 1 failed** because the database readiness test hardcoded `5432` rather than the isolated port. Commit `bb365fd` changed the readiness test to honor `DB_PORT` and `DB_NAME`. After correction, the user reran the filtered .NET API database integration tests with the test environment and reported:

- **10 total; 10 passed; 0 failed; 0 skipped.**
- Test duration 5.4 seconds; build succeeded in 9.0 seconds; process exit code **0**.
- Command: `dotnet test .\\tests\\VisionIQ.Api.Tests\\VisionIQ.Api.Tests.csproj --configuration Release --filter "FullyQualifiedName~DatabaseIntegrationTests|FullyQualifiedName~DatabaseReadiness"`.

This verifies the selected database integration tests against the isolated PostgreSQL instance. It does **not** establish that canonical LRN-01 ingestion into PostgreSQL + GCS was exercised end-to-end, nor that every earlier solution-wide skipped test was necessarily selected by this filter. Keep those as separately scoped checks if required.

## Live canonical ADO ingestion — initial run succeeded, verification pending

On 2026-11-02 the user ran `dotnet run --configuration Release --project .\\src\\VisionIQ.Ingestor.Cli -- sync --source work-items --since 2026-10-30` with `INGESTION_OUTPUT_MODE=canonical`, PostgreSQL checkpoints on a disposable database at `127.0.0.1:15543/visioniq`, and GCS bucket `visioniq-source-evidence-dev` in `EUROPE-WEST3`.

- Run ID `d42d2dbf-8b8a-45e0-820b-4a31b14bfb3b`; exit code **0**; all streams synced.
- 33 work items stored; 123 links; zero not-stored and zero quarantined.
- Recycle Bin: 415 deleted work-item markers reported.
- Graph, test-link and build-link projections processed zero assertions.
- Report: `artifacts/ingestion/lrn01-canonical-test/20261102-071749/report.json`.
- Bucket configuration reported `uniform_bucket_level_access=true`, `public_access_prevention=enforced`, and a seven-day soft-delete policy. Effective inherited project IAM and organizational source-data authorization require independent review.

**Pending verification:** independently query canonical PostgreSQL source identities/revisions/checkpoints and inspect GCS archive object metadata and references; replay the unchanged sync to establish canonical idempotency; test canonical failure recovery. These are not yet passed merely because the sync returned exit 0. Live repo/commit/PR ingestion remains blocked pending appropriate Azure Repos access.


## Canonical no-change replay — PASSED (2026-11-02)

Run `08b22273-4089-41b2-b40e-2b48eaa044d6` used canonical PostgreSQL checkpoints and GCS bucket `visioniq-source-evidence-dev`. The ADO work-item sync returned exit code **0** with **0 work items, 0 links, 0 not-stored, 0 quarantined**. Database counts before and after were identical: **208 source items and 524 source revisions**. This demonstrates a no-change incremental replay without duplicate canonical evidence. Report: `artifacts/ingestion/lrn01-canonical-test/20261102-074700/report.json`.

Archive location breakdown at the prior read: 503 rows referenced `visioniq-source-evidence-dev`, 15 `disposable-ci-only`, and 6 `test-evidence`; the latter 21 are test-related URI prefixes and should not be counted as actual GCS objects. PostgreSQL duplicate `(item_key, provider_revision_id)` query returned zero rows. **Still open:** validate selected current-bucket objects exist and their bytes match stored SHA-256; test a canonical persistence failure without advancing checkpoint. Live repository/commit/PR ingestion remains pending access.


## Canonical GCS content integrity — PASSED (sampled)

Five archive URIs were selected from `evidence.source_revisions` in the isolated PostgreSQL database, restricted to `gs://visioniq-source-evidence-dev/`. The user downloaded each object temporarily, computed its local SHA-256 hash and compared it with `content_sha256`. **5/5 matched; no missing objects or mismatches.** This is a five-object sample, not an exhaustive audit of all GCS objects. The check script included temporary-file cleanup.

**Remaining canonical gate:** verify persistence failure does not advance the PostgreSQL checkpoint and successful recovery does not duplicate evidence. Execute this only with the isolated database, controlled fault injection and no destructive changes to shared infrastructure.


## Live ADO access restored and comments unit regression — PASSED (2026-11-02)

User executed the new `WorkItemCommentTests` suite: **4 passed, 0 failed, 0 skipped, exit code 0**. Build reported 63 warnings; evaluate warning types during final regression. Live `inventory` exited 0 and reported **Genoa Kiosk: 4,438 work items, 9 repositories, 22 pipelines**. The former missing Azure Repos permission is resolved at discovery/inventory level; actual live commit and PR ingestion plus real comment cases remain unverified. Confluence remains unconfigured.


## Live KIOSK_TVM Git ingestion — streams passed; graph projection failed (2026-11-02)

Only ADO repository `KIOSK_TVM` (project `Genoa Kiosk`) was selected. Sync run `31242d8b-ba97-44d0-9710-04868966423b` reported pull requests **12 stored, 42 links** and commits **64 stored, 11 links**; both streams `Synced`, zero not stored/quarantined. Post-sync ticket-fix graph materialization processed 43 assertions and reported **18 failed associations** with `InvalidOperationException`; code marks `graphFailed=true` and returns **exit code 2** even when ingestion streams pass. Actual process exit code and remaining output not yet supplied. Treat source sync and graph reconciliation as distinct acceptance conditions. Investigate details without reingesting the same large source unnecessarily; inspect the first safe exception/error detail and PostgreSQL graph endpoint state. PR #14 remains Draft.

## Security observations

- ADO invalid credential test failed closed; 203 sign-in response was recognized and source reported `NotSynced` rather than zero results.
- 25 selected security/resilience tests passed, including logging, authentication, retry/timeout and source-isolation cases.
- Solution test logs warn that `LOG_PSEUDONYM_KEY` was unset during API testing; require a strong secret for non-test operation. API logs separately contain dictionary-type scope names; treat as distinct follow-up, not an unfixed ingestor regression.
- No claim that all authorization/revocation or database access-control scenarios have been exercised live.

## Defect-only search classification scope assessment

Code review of `src/VisionIQ.Ingestor/Ado/AdoWorkItemReader.cs`, `src/VisionIQ.Api/Features/GraphApi/GraphQueryService.cs` and `tests/VisionIQ.Api.Tests/GraphQueryServiceTests.cs` confirms that the source work-item type is preserved during ingestion. Graph query currently traverses authorized work-item relationships and exposes general source evidence metadata, **not** a dedicated defect-only similar-issues search. Therefore the previous "defect-only search regression" gate is **not executable against an implemented defect search feature**. Do not mark it passed, and do not filter out Task/Test Case/User Story data at the ingestion layer. When a defect-specific retrieval feature is implemented, its acceptance must explicitly require Bug-first matching with other types used only as supporting context; test mixed-type data and authorization. Track this as a downstream feature gate, not as an LRN-01 ingestion defect.

## Open gates before LRN-01 final acceptance

1. **Azure Repos access:** restore authorized Basic/repo read rights; verify repository inventory, commits and pull requests, pagination and independent checkpoint behavior. Do not treat 0 repositories as a completed repo test.
2. **Source fidelity:** sampled latest fields and five earlier historical revisions have now been matched against ADO; **dedicated downstream defect-only classification/search** validation remains a downstream feature gate, because no dedicated defect search implementation exists in the reviewed code. The sample does not prove exhaustive history correctness.
3. **Canonical persistence:** first live ADO canonical sync returned exit 0 (33 work items, 123 links); independently verify PostgreSQL records, GCS objects, checkpoint/replay and failure recovery before acceptance. Coordinate cross-story ownership with LRN-03/05/07.
4. **Final code review and evidence signoff:** evaluate any additional review blockers or CI gates at the final branch head. Do not merge on the basis of this report alone.

## PR policy

**PR #14 remains DRAFT** and must not be merged until LRN-01 through LRN-07 have each passed their story-specific acceptance and the combined pipeline has been reviewed and approved. Live Git verification is pending access restoration. No cloud deployment is authorized by this report.

## LRN-07 graph assertion identity fix — implementation awaiting local validation (2026-11-02)

Live `KIOSK_TVM` incremental Git sync (`runId=908c1918-a7c9-43b8-bba6-e023c488d78b`) synced 2 PRs and 19 commits (no source errors), but reconciliation failed 21 associations with `immutable_graph_assertion_conflict` and process exit 2. Database contained multiple ticket-fix associations sharing evidence refs. Root cause in code: `GraphAssertion.AssertionKey` previously omitted `TicketFixAssociationKey`, while `ContentHash` included it, so legitimate distinct associations could collide. Updated association-linked keys to hash `(RelationshipKey, ProvenanceRevision, TicketFixAssociationKey)`, retaining the legacy key formula for assertions without an association. Added unit regression for two distinct associations, deterministic replay, legacy identity, and immutable changed-content detection. Old graph rows are deliberately preserved; no migration or data deletion performed. Verify runtime behavior and inspect legacy/new row coexistence in isolated database before acceptance. PR #14 remains Draft.

## LRN-07 graph identity live retest — no projection conflicts reported (2026-11-02)

Following association-scoped assertion-key fix, user rebuilt and ran canonical `KIOSK_TVM` delta sync (`runId=586d47ce-1769-43fb-abd9-2060eb921ec0`). Both PR and commit streams reported `Synced`, **0 newly stored, 0 quarantined, 0 not stored**. Ticket-fix graph reconciliation processed **85 assertions** and displayed **no projection failure warnings**; test-link and build-link projections processed 0. Full terminal tail and exit code were not included in the shared output, so process exit 0 remains to be confirmed. This reconciled existing data without a database reset, but regression-test output and longer-term compatibility of legacy assertion rows still require validation.

## LRN-07 regression confirmed — PASSED (2026-11-02)

User ran `TicketFixAssertionIdentityTests` in `VisionIQ.Api.Tests`: **3 passed, 0 failed, 0 skipped, exit 0**, build successful. Post-fix KIOSK_TVM reconciliation reported 85 assertions processed and no conflicts; persisted graph assertions were **141**. The original 21 `immutable_graph_assertion_conflict` errors were no longer reported. The specific assertion-key collision defect is verified fixed, while broader LRN-07 story acceptance and LRN-01 comments/failure-recovery checks remain pending.

## Live ADO comments ingestion — PASSED (2026-11-02)

Full local JSONL work-item sync `3ccb5eba-59c1-48e9-b980-da646ccfada3` finished exit 0, 367 work items, 1002 links, zero not stored/quarantined. Verified `work-items.jsonl` via `JsonLinesSink` wrapper property `record`: work item 106675 had `CommentCount=9`, 9 `comments`; 107328 had `CommentCount=2`, 2 comments; 115804 had `CommentCount=0`, zero comments. All three had blank `CommentsGap` and `CommentsTruncated=false`. No source content printed. Earlier `WorkItemCommentTests` passed 4/4 including unavailable handling. This confirms comments in local JSONL but does not independently prove canonical storage of the comment fields. Remaining major LRN-01 gate: controlled canonical persistence failure and checkpoint recovery.
