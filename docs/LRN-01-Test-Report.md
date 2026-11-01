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

## Security observations

- ADO invalid credential test failed closed; 203 sign-in response was recognized and source reported `NotSynced` rather than zero results.
- 25 selected security/resilience tests passed, including logging, authentication, retry/timeout and source-isolation cases.
- Solution test logs warn that `LOG_PSEUDONYM_KEY` was unset during API testing; require a strong secret for non-test operation. API logs separately contain dictionary-type scope names; treat as distinct follow-up, not an unfixed ingestor regression.
- No claim that all authorization/revocation or database access-control scenarios have been exercised live.

## Open gates before LRN-01 final acceptance

1. **Azure Repos access:** restore authorized Basic/repo read rights; verify repository inventory, commits and pull requests, pagination and independent checkpoint behavior. Do not treat 0 repositories as a completed repo test.
2. **Source fidelity:** sampled latest fields and five earlier historical revisions have now been matched against ADO; **dedicated downstream defect-only classification/search** validation is still open. The sample does not prove exhaustive history correctness.
3. **Canonical persistence where required by the approved story acceptance criteria:** use disposable PostgreSQL/GCS test resources, check tombstone behavior, atomicity/idempotency and applicable skipped integration tests. Coordinate cross-story ownership with LRN-03/05/07.
4. **Final code review and evidence signoff:** evaluate any additional review blockers or CI gates at the final branch head. Do not merge on the basis of this report alone.

## PR policy

**PR #14 remains DRAFT** and must not be merged until LRN-01 through LRN-07 have each passed their story-specific acceptance and the combined pipeline has been reviewed and approved. Live Git verification is pending access restoration. No cloud deployment is authorized by this report.
