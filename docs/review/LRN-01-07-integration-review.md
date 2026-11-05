# LRN-01 to LRN-07: independent code and integration review

| | |
|---|---|
| Date | 2026-11-04 |
| Reviewed commit | `44ec036` (branch `feature/learn-01-14-TEST`) |
| Fix branch | `review/lrn-01-07-integration` (uncommitted working tree; nothing pushed or merged) |
| Scope | .NET ingestor/API, Python intelligence, PostgreSQL migrations 0001-0018, `db/manage.py` grants/RLS, docs incl. `docs/Functional/` |
| Not touched | Corporate ADO, Confluence, production DB, buckets, service hooks |

## How to read this report

Evidence labels used for every finding:

| Label | Meaning |
|---|---|
| **REPRODUCED** | Executed: a failing test, a script, or a SQL expression run against PostgreSQL 16 showed the wrong behaviour. |
| **CODE-TRACED** | Confirmed by reading the exact code path end to end; not executed. |
| **SUSPECTED** | Plausible from the code, but depends on source-system behaviour that was not observed. |
| **IMPROVEMENT** | Not a defect; a proposed change. |

What was **not** run in this review, and why:

- DB-backed integration tests (82 Python, 9 .NET) were not run: they need the local `.env` database credentials, which were deliberately not read. They are reported as SKIPPED, not PASSED.
- No real ADO, Confluence or GCS calls were made.
- So every statement below about cross-feature behaviour on a real database is CODE-TRACED unless marked otherwise.

Severity: Critical = silent permanent data loss or access leak; High = wrong results in the core flow or a blocker for end-to-end acceptance; Medium = wrong in a realistic edge case; Low = minor or hygiene.

---

## 1. Summary

The seven stories are individually well built: parameterized SQL throughout, fail-closed authorization, immutable content-addressed evidence, careful tie-breaks, and a large unit-test base. They are **not yet a connected, end-to-end-proven LEARN foundation**. The main reasons:

1. **The chain breaks between LRN-04 and LRN-06/07.** A historical fix commit never reaches its changed files or symbols in the graph: there is no `commit -> changed file` relation (R7-01), the scanner only scans whatever HEAD is checked out and nothing triggers it (R6-09), and defect -> PR -> commit -> file -> symbol needs 4 hops while the API allows 3 (R7-02).
2. **The connectors can silently lose history.** The work-item cursor follows live `ChangedDate` instead of the WIQL snapshot (R1-01), and the commit cursor follows the developer's committer clock (R1-02).
3. **Fix associations drift from ADO.** Links are never retracted when unlinked (R4-02), abandoned/active PRs count as fixes (R4-03), and a revert of the merge commit does not hide the PR-level edge (R4-01).
4. **Nothing proves the chain.** There is no CI workflow in the repository, no cross-feature end-to-end test, and the DB integration tests that exist have no recorded run in this repository.
5. **Docs claim more than the evidence shows.** Several docs cite CI that does not exist, and the same story has different statuses in different documents (section 7).

Baseline test state found at `44ec036`: a plain `dotnet test` **failed** (2 tests), one of them a real regression (D-01). Python: 160 passed, 81 skipped.

After the fixes on the review branch: .NET **443 passed, 0 failed, 9 skipped (opt-in)**; Python **167 passed, 82 skipped (DB-gated)**; `ruff check` clean.

---

## 2. What each feature does (plain language)

The running example throughout: **Bug 4521 "Invoice total wrong in PROD"** in ADO project `Payments`, fixed by **PR 812** in repo `billing-svc` (commits `c1`, `c2`, squash-merge commit `m`), which changed `Invoice/TotalCalculator.cs` and `InvoiceTests.cs`. The design rationale lives on Confluence page **ADR-17 "Idempotent retries"** in space `ENG`, restricted to architects.

### LRN-01: Connect to the history sources (ADO)

- **Problem:** to investigate a new invoice bug, we need every past bug, its revisions and comments, and the PRs, commits, changed files and builds around it. All of that is in ADO.
- **Inputs:** `ADO_READ_PAT`, org URL, approved projects, optional repo/pipeline allowlists, `INGESTION_SINCE`.
- **Processing:** `SyncRunner` runs one stream per kind and project.
  - Work items: WIQL (`ChangedDate >= cursor`, 20k cap) -> `workitemsbatch` in groups of 200 -> comments and revisions paged -> Recycle Bin for deletions (`AdoWorkItemSource`, `AdoWorkItemReader`).
  - PRs: list `status=all`, then enrich with work items, commits, iterations, changes, threads, policies (`AdoRepositorySource`).
  - Commits on the default branch with changes paged up to 50k files (`AdoCommitSource`); builds with artifacts (`AdoBuildSource`).
  - `AdoClient`: Basic PAT, retries 429/5xx/timeouts with `Retry-After`, logs path only.
  - Each work item is classified PROD/UAT/TEST/UNKNOWN/CONFLICT (`DefectEnvironmentClassifier`, rules file per project).
- **Outputs:** JSONL files (local mode) or the canonical chain (LRN-03) plus projections; checkpoints in `evidence.connector_checkpoints` (migration 0003) or a file.
- **Depends on:** LRN-03 (storage), feeds LRN-04/07 projections.
- **Status:** implemented for small/medium projects. Cursor correctness gaps R1-01, R1-02, R1-05; moved work items R1-03.
- **Verify:** `dotnet run --project src/VisionIQ.Ingestor.Cli -- sync`, run it twice, the second run must write 0 new revisions; compare `SELECT kind, count(*) FROM evidence.source_items GROUP BY 1` with ADO counts.

### LRN-02: Include design decisions from Confluence

- **Problem:** the fix for Bug 4521 is only understandable with ADR-17, and only people allowed to open ADR-17 in Confluence may see it through VisionIQ.
- **Inputs:** approved space keys, site URL, Cloud email + API token or Data Center PAT.
- **Processing:** `ConfluenceCloudSource` (v2 cursor paging, v1 restrictions per page) or `ConfluenceDataCenterSource` (v1 offset paging); `ConfluenceText.Normalize` turns storage XHTML into text, sections and a properties table; `DesignDecisionDetector` flags ADRs; `ConfluenceAccess.ComputeAcls` builds a fail-closed ACL (unknown, truncated or looping restrictions deny).
- **Read time:** `ConfluencePageAccessVerifier` maps the caller's Entra email to an Atlassian accountId and asks Confluence Cloud live (`permission/check`); any error denies; Data Center always denies.
- **Outputs:** LRN-03 evidence (`design_page`, `design_decision`), `confluence-acl` records.
- **Status:** implemented for listing, normalization, decision detection and the live Cloud check. Missing: historical page bodies, attachments, comments, deleted/moved-page tombstones (R2-02). `ConfluenceCurrentAccessVerifier` is dead code (R2-10).
- **Verify:** sandbox space, page P restricted to user A, child C; sync in canonical mode; query the graph API as user B (hidden) and as A (visible).

### LRN-03: Store history as versioned evidence

- **Problem:** we need Bug 4521 exactly as it was at each revision, the original kept for audit, and only redacted metadata exposed.
- **Inputs:** `EvidenceEnvelope` (provider, instance, project, repo, kind, external id, revision, times, URL, raw JSON <= 10 MiB, ACL principals).
- **Processing (`EvidenceIngestionService.ImportAsync`):** SHA-256 of the raw JSON -> allowlist redaction (`EvidenceNormalizer`) -> GCS object `raw/v1/{itemKey}/{sha(revision)}/{contentSha}.json` with `IfGenerationMatch=0` -> one PostgreSQL transaction in `PostgresEvidenceStore.SaveAsync` (upsert item, detect replay or conflict, insert revision, clear tombstone, choose latest by source time -> numeric revision -> observed time).
- **Read:** `EvidenceQueryService.FindLatestAuthorizedAsync` requires a verified stored ACL, a matching caller principal, and a live provider check; returns only identity, revision, URL and redacted JSON.
- **Tables:** `evidence.source_items`, `evidence.source_revisions` (0001/0002), tombstone columns (0011). Roles and RLS are created by `db/manage.py bootstrap`, not by migrations.
- **Status:** implemented and the best-evidenced story. Gaps: writer and reader share one DB role with UPDATE/DELETE on "immutable" revisions (R2-04); environment classification was being dropped (D-01, fixed).
- **Verify:** import the same item twice -> one `source_items` row, status Unchanged; a changed payload -> two revisions, latest pointer moves.

### LRN-04: Link each ticket to the fix that closed it

- **Problem:** to learn from Bug 4521 we must know that PR 812 (and `c1`, `c2`, `m`) fixed it, how sure we are, and whether it was later reverted.
- **Inputs:** work-item ArtifactLinks (`vstfs:///Git/PullRequestId/...`, `vstfs:///Git/Commit/...`), PR work-item lists, PR commits and `lastMergeCommit`, commit messages (`AB#4521`, `#4521`, `This reverts commit <sha>`).
- **Processing:** `LinkExtractor` -> `TicketFixProjectionSink` upserts `TicketFixClaim`s (key = SHA-256 of instance, project, ticket, repo, PR, commit) into `evidence.ticket_fix_links` via `PostgresTicketFixLinkStore`; reverts recorded on the matching commit rows and in `evidence.ticket_fix_revert_observations` (0018, folded in on later upserts); changed paths per PR/commit into `evidence.fix_change_sets` (0013); human reviews in `evidence.ticket_fix_reviews` (0004).
- **Trust levels:** explicit (ADO states it), inferred (message mention), verified/disputed (human review only; verification refused once reverted).
- **Status:** explicit and inferred links, reverts in either arrival order and idempotent upserts work. Gaps: R4-01 (PR-level revert), R4-02 (no retraction), R4-03 (abandoned PRs), R4-04 (revert of revert), cross-project links dropped silently (R4-10).
- **Verify:** `SELECT ticket_id, pull_request_id, commit_sha, source_classification, revert_commit_shas FROM evidence.ticket_fix_links WHERE ticket_id = '4521'`.

### LRN-05: Load full history and keep it current

- **Problem:** Bug 4790 (a sibling of 4521) was reopened yesterday and deleted today; answers must reflect that without re-reading everything.
- **Pieces:** delta cursors (`TimeCursor`: newest time + IDs at that time); `--full` and `--reconcile-days N` (never moves the checkpoint); `serve-webhooks` with an idempotent inbox `evidence.source_change_inbox` (0005; lease, 5 attempts, dead-letter), handlers re-fetch from ADO and never trust the body; per-item quarantine `evidence.sync_quarantine` (0012); tombstones `source_items.deleted_at` from the Recycle Bin and confirmed `workitem.deleted` events (0011); run audit `evidence.source_sync_runs`; Python `BackfillReconciler` (provider-neutral, contiguous-prefix checkpointing).
- **Status:** partial. Webhooks, inbox, quarantine and tombstones exist and are unit-tested. **No scheduled sync or reconcile sweep exists**, yet dead-lettered webhooks rely on one (R1-08). The Python reconciler has no production adapter. Webhook changes reach the graph only after a CLI batch run (R1-09, R7-12).
- **Verify:** run `serve-webhooks`, post a recorded ADO `workitem.updated` payload, check `evidence.source_change_inbox`; delete and restore a sandbox item and watch `deleted_at`.

### LRN-06: Understand the code

- **Problem:** PR 812 changed `TotalCalculator.cs`; to route the next invoice bug we need the files, symbols (`InvoiceService.Total`), tests and dependencies in that repo.
- **Inputs:** a local git checkout (HEAD only), `--project-id`, `--repository-id`, credential-free `--source-url`, optional `--ado-source-instance`.
- **Processing:** `scanner.scan_repository` reads committed blobs via `git ls-tree`/`cat-file` (symlinks and submodules skipped, 1 MiB per file, binary rejected) -> adapters `PythonAstAdapter`, `CSharpTreeSitterAdapter` -> grammar gaps downgrade one file (N-16) -> `canonical.build_scan_evidence` (metadata-only, 10 MiB) -> GCS + `evidence.source_revisions` (`provider='git', kind='code_scan'`) -> `relationships.resolve_dependencies` -> `graph.persist_code_graph`.
- **Languages:** Python and C# get symbols; JS/TS/Java/Go/Rust/SQL/Vue get file-level evidence only (never invented symbols, which meets AC 2).
- **Status:** implemented for a demo-size repo. Missing: trigger, `--commit`, cloud job, large-repo scaling (R6-04), parser-upgrade path (R6-05). `impact.map_diff_to_symbols` is never called.
- **Verify:** scan a small repo twice (second scan UNCHANGED), then force a parser crash (scan PARTIAL, last good kept).

### LRN-07: Build the engineering relationship graph

- **Problem:** "Show me what fixed bugs like 4521, the code they touched and the tests", hiding anything the asker cannot open.
- **Writers:** `TicketFixGraphMaterializer` (`ticket_pr`, `pr_commit`, `ticket_commit`), `TestLinkGraphMaterializer` (`tested_by`, `automated_by`), `BuildLinkGraphMaterializer` (`ci_commit`, `ci_pull_request`), Python `persist_code_graph` (`commit_file`, `file_symbol`, `file_test`, `file_dependency`, `file_renamed`, ADO-commit bridge). Tables `evidence.graph_nodes`, `evidence.graph_assertions` (0006-0010), each assertion with provenance FK to `source_revisions`.
- **Reader:** `GET /api/v1/graph/work-items/{project}/{id}?hops=1..3` -> `GraphQueryService` -> `AuthorizedGraphTraversal` -> per node `EvidenceQueryService` + per edge revision ACL. Entra JWT (tenant, audience, `scp`, caller app allow-list), 404 for both missing and denied.
- **ID contract:** C# and Python node keys match (reproduced) except for upper-case SHAs (R7-13; ADO returns lower case today).
- **Status:** writers and authorized reader implemented; the code part of the graph is not reachable from a defect (R7-01, R7-02); CI and design edges are never served (R7-03, R7-10).

### The full flow, hop by hop

| # | Hop | Code | Table | Wired today? |
|---|---|---|---|---|
| 1 | ADO bug 4521 / PR 812 / commits -> canonical evidence | `SyncRunner` -> `CanonicalEvidenceSink` -> `EvidenceIngestionService` | `source_items`, `source_revisions`, GCS | Yes (CLI batch and webhooks) |
| 2 | 4521 <-> PR 812 <-> `c1`,`c2`,`m` | `TicketFixProjectionSink` | `ticket_fix_links` | Yes; cross-project links silently dropped |
| 3 | Changed files of PR 812 / `m` | `FixChangeSetProjectionSink` | `fix_change_sets` | Yes (as a table, not as graph edges) |
| 4 | Graph: ticket -> PR -> commit | `TicketFixGraphMaterializer` (`Ingestor.Cli/Program.cs`) | `graph_assertions` | CLI batch only |
| 5 | Graph: commit `m` -> changed files | none | none | **No** (R7-01) |
| 6 | Scan of repo at commit `m` | `ingest_cli` (manual, HEAD only) | `source_revisions` (`code_scan`) | **No trigger** (R6-09) |
| 7 | Graph: file -> symbols/tests | `graph.persist_code_graph` | `graph_assertions` | Yes, for the latest scan only |
| 8 | API traversal 4521 -> ... -> symbol | `AuthorizedGraphTraversal` | | **No**: 4 hops needed, 3 allowed (R7-02) |
| 9 | Retrieval by stack-frame symbol | `retrieval.CODE_PATH_SQL` | | Was dead (R6-02, fixed); nothing in `src/` calls `retrieve()` yet |

---

## 3. Confirmed defects and recommended fixes

### 3.1 Fixed on the review branch (with regression tests)

Every fix below was checked both ways: the new tests **fail on the original code** and pass with the fix.

| ID | Sev | Evidence | Defect | Fix | Regression tests |
|---|---|---|---|---|---|
| D-01 | High | REPRODUCED (baseline test failure) | The PROD/UAT/TEST classification computed by the ingestor was dropped by the redaction allowlist, so canonical metadata could never be filtered by environment. Two existing tests contradicted each other (B-13 expected the fields; a boundary test treated any value as sensitive). | `EvidenceNormalizer` now copies `environment`, `environmentStatus`, `environmentSource`, `environmentReason` **only when the value is in the classifier's closed vocabulary**; `environmentRaw` never. Both existing tests now pass. `src/VisionIQ.Api/Features/Evidence/EvidenceModels.cs` | `EvidenceIngestionTests.Current_work_item_keeps_defect_environment_classification_but_not_raw_source_text`, `Environment_fields_accept_only_the_classifier_vocabulary` |
| D-02 | Medium | REPRODUCED | A plain `dotnet test` was always red: the opt-in GCS test threw; the opt-in PostgreSQL test returned early and was **reported as PASSED without running**. | New `[OptInFact(flag)]` reports both as SKIPPED unless their flag is `1`. | `tests/VisionIQ.Ingestor.Tests/OptInFactAttribute.cs` |
| R4-05 / R4-06 | Medium | REPRODUCED | Commit messages produced false links: `pr #42`, `PR  #42`, `pull request #812`, `AB#0`, `#007`; a 42-hex revert marker was cut to a "full SHA". | Case-insensitive PR / pull-request exclusion with any whitespace, ids must start 1-9, `\b` after the revert SHA. `src/VisionIQ.Ingestor/Links/LinkExtractor.cs` | 7 new cases in `LinkExtractorTests.Noise_never_becomes_a_link` |
| R2-05 | Medium | REPRODUCED | A header-row table before the ADR properties table made the parser read key `Name</th><th>Owner</th>...<th>Status`, losing `Status` and template-based decision detection. | Cell content may not cross row/cell/table boundaries. `ConfluenceText.cs` | `ConfluenceReviewRegressionTests.A_header_row_table_before_the_properties_table_does_not_hide_the_decision_status` |
| R2-06 | Medium (security) | REPRODUCED | A Confluence paging link to another host, to `http://`, another port or scheme-relative `//host` would receive the Basic/Bearer credential. | Resolved links must match the configured site's scheme, host and port, otherwise the run fails. `ConfluenceHttp.cs` | `ConfluenceReviewRegressionTests` (7 cases) |
| R1-04 | Medium | REPRODUCED | JSONL mode saved the checkpoint while records were still in the writer buffer; a crash or Ctrl+C lost records the next run then skipped. | `IRecordSink.FlushAsync` (default no-op; JSONL flushes to disk); both runners flush before every checkpoint save. | `CheckpointDurabilityTests` |
| R6-01 | High | REPRODUCED | One deeply nested or generated file (`RecursionError`) marked the whole repository PARTIAL, after which `build_scan_evidence` refused every later scan until that file changed. | `RecursionError` downgrades only that file (UNAVAILABLE with a diagnostic) and counts toward the existing systematic-gap threshold. `scanner.py` | `test_grammar_gaps.py::test_a_deeply_nested_file_degrades_only_that_file` |
| R6-02 | High | REPRODUCED in PostgreSQL 16 | The retrieval symbol lane extracted the **line number** from node ids (`...OrderService.Submit:40` -> `40`), so stack-frame symbols never matched. | `SYMBOL_NAME_PATTERN` extracts the simple name (`Submit`). `retrieval.py` | `tests/intelligence/test_review_symbol_lane.py` (incl. a DB-gated check) |

### 3.2 Open defects: need a decision or a larger change

Ordered by severity. "Fix size" S = small and local, M = needs a DB test or touches several files, L = design/migration.

#### LRN-01 / LRN-05: connectors and currentness

| ID | Sev | Evidence | Defect | Recommended fix | Size |
|---|---|---|---|---|---|
| R1-01 | Critical | CODE-TRACED (`SyncRunner.cs:163,194,213`; no `asOf` anywhere) | The work-item cursor advances to the item's **live** `ChangedDate` from `workitemsbatch`, not the WIQL snapshot time. An item changed after the WIQL query, but older than another item's later edit, is never read. With the 20k cap the gap can be large. | Read `asOf` from the WIQL response, send it to `workitemsbatch`, and clamp the cursor to it. | M |
| R1-02 | High | CODE-TRACED (`SyncRunner.cs:322,332`) | The commit cursor is the developer's **committer date**. One commit with a future date stops all later commits; merged branches with older dates are skipped. These are the `Fixes #...` commits LRN-04 needs. | Overlap window deduplicated by commit id, clamp future dates, or use push time. | M |
| R1-03 | High | CODE-TRACED; access impact SUSPECTED | `System.TeamProject` is never checked. A work item moved from project A to B (common for security bugs) stays readable by A's members, never tombstoned. | Parse and check the project; tombstone the old key as `missing_in_source`. | M |
| R1-05 | Medium | CODE-TRACED | The work-item stream only checkpoints after the whole window succeeds; one failure late in 20k items saves nothing. A permanently quarantined item holds the cursor forever. | Checkpoint the contiguous written prefix (as `reconcile.py` does). | M |
| R1-06 | Medium | CODE-TRACED | PR delta key is `ClosedDate ?? CreatedDate`: changes to active PRs and links added after completion are missed outside webhooks/`--full`; every run lists all PRs. | Re-enrich active PRs plus PRs closed in the last N days. | M |
| R1-07 | Medium | SUSPECTED | PR/commit/build streams have no per-item quarantine; one permanent 403/404 stops the stream at the same item every run. | Treat as a gap or quarantine and continue. | S |
| R1-08 | Medium | CODE-TRACED | Dead-lettered webhooks return 200 (ADO stops retrying) on the assumption a scheduled reconcile exists. It does not. | Deploy scheduled sync + `--reconcile-days`, or return 5xx until then. | infra |
| R1-09 | Low | CODE-TRACED | Work-item webhooks write no links or revisions and never trigger graph projection. | Write links in the handler; queue graph reconciliation. | M |
| R1-10..14 | Low | CODE-TRACED | Silent tombstone refusal on clock skew; org/project match is a string prefix; quarantine entries of deleted items never close; no retry jitter; audit `result_cursor` always null. | See detailed agent notes; each is small. | S |

#### LRN-02 / LRN-03: Confluence and evidence store

| ID | Sev | Evidence | Defect | Recommended fix | Size |
|---|---|---|---|---|---|
| R2-01 | High (latent) | CODE-TRACED; no production caller yet | Restricted Confluence pages carry only the **space** principal; the LRN-08 indexer indexes full page text; `retrieve()` without `live_check` filters by principal only. Once retrieval is wired to users, a restricted page's text would be returned to anyone with the space. | Make `live_check` mandatory for non-ADO kinds, or do not index restricted/incomplete-ACL pages. Must be closed **before** LRN-08/09 go to users. | S-M |
| R2-02 | Medium | CODE-TRACED | Deleted, archived or moved Confluence pages are never tombstoned; their metadata and index chunks remain. | After a fully successful space listing, tombstone unseen pages (`missing_in_source`). | M |
| R2-03 | Medium | CODE-TRACED | The Confluence identity uses the raw configured URL: `https://x.atlassian.net/wiki` and `https://x.atlassian.net` give different item keys for the same page; changing the setting duplicates everything. | Use scheme+host (`ConfluenceAccessPrincipals.Origin`). **Changes existing keys: needs a migration plan.** | M |
| R2-04 | Medium | CODE-TRACED (`db/manage.py`) | The API role has INSERT/UPDATE/DELETE on all `evidence` tables and an RLS `USING (true)` policy; the "read-only" query path uses the same role; nothing prevents UPDATE/DELETE of immutable revisions. Grants live in `manage.py`, not migrations. | Split ingestor-writer and API-reader roles; revoke UPDATE/DELETE on `source_revisions` (and `ticket_fix_reviews`, `ticket_fix_revert_observations`; see F04-01, R4-09). | L |
| R2-07 | Low | SUSPECTED | Data Center paging stops when a page returns fewer than 50 results; a server-capped limit ends the sync early and advances the checkpoint. | Follow `_links.next`. | S |
| R2-08 | Low | CODE-TRACED | Re-confirming the current latest revision does not refresh `latest_observed_at`, so a late write of an older same-time revision can win. | Update `latest_observed_at` on the Unchanged path. | S (DB test) |
| R2-09..12 | Low | IMPROVEMENT | A GCS upload on every replay; dead `ConfluenceCurrentAccessVerifier`; API cannot force `cloud` deployment; no cache for live permission checks. | See agent notes. | S |

#### LRN-04: ticket-to-fix

| ID | Sev | Evidence | Defect | Recommended fix | Size |
|---|---|---|---|---|---|
| R4-01 | High | CODE-TRACED | A revert of merge commit `m` marks only the `m` row. The `ticket_pr` edge is also produced from rows `c1`, `c2` and (ticket, PR, null), which never get the revert; `DISTINCT ON` picks one arbitrarily. Reverted PR 812 can still show as the fix. | Treat revert at PR level (mark all rows of the same instance/project/repo/PR), or filter `ticket_pr` by "any row of this PR reverted". | M |
| R4-02 | High | CODE-TRACED | Links are never retracted. Unlinking 4521 from PR 812 in ADO leaves the explicit link forever; graph, lessons and retrieval keep using it. | Track the asserting revision per provenance and retract claims missing from a newer revision. | L |
| R4-03 | High | CODE-TRACED | Abandoned and still-active PRs are stored as explicit fixes and get change sets; they feed lessons and retrieval. | Store PR status/completed; filter non-completed PRs in lessons, retrieval and the materializer. | M |
| R4-04 | Medium | CODE-TRACED | Revert of a revert is not handled: the re-applied fix stays hidden and unverifiable. | Record "reinstated by"; compute net state. | M |
| R4-07 | Medium | SUSPECTED (= M-8) | Only `git revert` messages are detected; ADO's Revert button / squash-merged revert PRs are not. | Detect revert PRs (title/branch) as inferred revert evidence; verify the real message in the sandbox. | M |
| R4-08 | Medium | IMPROVEMENT | Reverts dropped before fe8bdb3 are not recovered (checkpoints passed them). | One-time backfill scanning canonical `commit` evidence. | S |
| R4-10 | Low | CODE-TRACED | Cross-project ticket/PR links are silently dropped (no leak, no diagnostic). | Store the ticket's project separately; count unresolved. | M |
| R4-11 | Low | CODE-TRACED | Lessons count observations, not distinct tickets, when re-checking `MIN_TICKETS`: one multi-commit PR can satisfy "at least 2 tickets". Also ticket x commit fan-out round trips. | Count distinct `ticket_id` (`lessons.py:242`); batch upserts. | S |
| R4-12 | Low | CODE-TRACED | `fix_change_sets.observed_at` is wall clock; replaying an older PR snapshot overwrites newer paths. | Order by source time. | S |

#### LRN-06: code intelligence

| ID | Sev | Evidence | Defect | Recommended fix | Size |
|---|---|---|---|---|---|
| R6-03 | High | REPRODUCED (agent script) | Rename detection diffs against the newest scanned commit, not the previous one. Scanning historical fix commits after HEAD (normal backfill) produces **inverted, permanent** `file_renamed` edges. | Only diff when the previous commit is an ancestor (`git merge-base --is-ancestor`). | S-M |
| R6-04 | High | REPRODUCED (measured) | Large repos cannot be ingested: about 6,000 files hit the 10 MiB archive cap; 2 git subprocesses per file (305 ms/file measured on this machine under load); about 66k DB round trips in one transaction for 1,500 files. | `git cat-file --batch`, sharded archive, set-based graph writes, configurable limits. | L |
| R6-05 | Medium | REPRODUCED | Re-scanning a commit after a parser upgrade is a CONFLICT (revision = SHA, content hash includes adapter version). No reprocessing path. | Include the parser-set version in the revision id. | M |
| R6-06 | Medium | REPRODUCED | `from . import util, models` resolves to `pkg/__init__.py` only; imported names are dropped. | Emit per-alias candidates. | S |
| R6-07 | Medium | REPRODUCED | Symbol identity includes the start line; adding one import line creates a new node; `automated_by` edges disappear until re-materialized. | Identity = path + qualified name (+ signature/ordinal); lines as attributes. | L |
| R6-08 | Medium | CODE-TRACED | The CLI never passes approved repositories/targets, so cross-repo dependency edges (S06-10) are never produced. | Wire the configuration or mark S06-10 not delivered. | S |
| R6-09 | Medium | CODE-TRACED | No trigger and no `--commit`: LRN-04 fix commits are never scanned unless someone checks them out by hand. `impact.map_diff_to_symbols` is unused. | `--commit <sha>` via `ls-tree <sha>`; a job fed by new fix change sets. | M |
| R6-10 | Low | REPRODUCED | A non-UTF-8 committed path crashes evidence building. | Escape deterministically with a diagnostic. | S |
| R6-11..12 | Low | IMPROVEMENT | No vendor/generated exclusion; last-good cache keyed by local path; `DB_SSL_MODE` defaults to `prefer`. | Exclude list; key cache by item key; `verify-full` in cloud. | S |

#### LRN-07: graph and authorization

| ID | Sev | Evidence | Defect | Recommended fix | Size |
|---|---|---|---|---|---|
| R7-01 | High | CODE-TRACED + REPRODUCED (edge counts) | `commit_file` means "file exists in the scanned tree" for the scanned HEAD only; a historical fix commit never reaches its **changed** files. The "changed with fix" evidence level in the docs is not derivable. | New relation `commit_changed_file` / `pr_changed_file` from `fix_change_sets.paths`, anchored to the repo's scan file nodes. **Needs a migration.** | L |
| R7-02 | High | CODE-TRACED | Defect -> PR -> commit -> file -> symbol is 4 hops; the API caps at 3. | Typed path query ("defect -> fix files -> symbols") or 4 hops with a fan-out budget. | M |
| R7-03 | Medium | CODE-TRACED | Traversal is outgoing only: CI edges (from `CiRun`), design edges and rename edges are unreachable from a work item. | Reviewed incoming-relation allow-list or reverse those edges. | M |
| R7-05 | Medium | CODE-TRACED (also R6-13) | `automated_by` mapping matches test names across **all** projects and repos. | Filter by the test case's project/repo. | S |
| R7-06 | Medium | CODE-TRACED (design) | Authorization is project-team membership only; ADO area-path, repo and item restrictions are not enforced. | Per-item check via the user's OBO token, or document as an accepted risk with sign-off. | L |
| R7-07 | Medium | SUSPECTED | ADO identity is taken from mutable `preferred_username`/`upn`. | Key on `oid`/`tid`. | M |
| R7-08 | Medium | CODE-TRACED | Up to ~40k sequential queries per request at worst; no rate limit. | Batch ACL checks per hop, per-request cache, rate limit. | M |
| R7-10 | Medium | CODE-TRACED | Nothing writes `design_related`; `repository_dependency` never wired. The Confluence part of the graph does not exist. | Implement or mark as not delivered. | M |
| R7-04, R7-09, R7-11..17 | Low | CODE-TRACED | Cross-project PR links dropped; LIMIT before authorization; no edge provenance in API output and duplicate edges; batch-only projection; silent bridge miss and SHA case divergence; API on without ADO verifier silently 404s; docs say no endpoint exists. | See agent notes; mostly S. | S |

### 3.3 Checked and found sound

- All SQL is parameterized (Npgsql and psycopg); WIQL project names are regex-guarded.
- Webhook auth: SHA-256 + `FixedTimeEquals`, secret >= 32 chars, 1 MiB body cap; replay is harmless because handlers re-fetch from ADO; deletions are confirmed before hiding.
- PATs and tokens never appear in logs, URLs, `ToString` or exception text (path-only logging).
- Evidence query and graph API fail closed: unverified ACL, principal mismatch, verifier errors, missing and denied all return 404 with `no-store`.
- Entra JWT validation: tenant issuer, audience, lifetime, signature, `tid`, required `scp`, caller app allow-list. No header trust, no dev bypass.
- RLS limits the intelligence role to `git/code_scan` evidence and its graph rows.
- C# and Python graph node keys agree for the same commit (reproduced), except upper-case SHAs.
- The fe8bdb3 revert-ordering fix itself is correct (same advisory lock on both paths, SHA case normalized, NULL commit handled), apart from R4-01 and R4-08.

---

## 4. Test and production-readiness assessment

"Recorded" means a dated result exists in this repository's docs. No result in this table was invented; "none" means none was found.

| Story | Unit tests | DB integration tests | Real-source acceptance | Cross-feature E2E | Cloud readiness | Security / ops readiness | Verdict |
|---|---|---|---|---|---|---|---|
| LRN-01 | Strong (fake HTTP: sync, cursors, WIQL cap, retries, paging) | `DbCheckpointStore` SQL never tested on PostgreSQL | Recorded user-run real-ADO syncs (`docs/LRN-01-Test-Report.md`); doc itself says not fully accepted | None | No ingestion Cloud Run Job or scheduler | PAT handling good; R1-01/R1-02 silent loss | **Not ready**: fix R1-01, R1-02, R1-03 |
| LRN-02 | Good (normalization, ACL computation, live check) | n/a | Recorded inventory/sync; acceptance OPEN in its own docs | None ("restricted page hidden from a real user" never demonstrated) | Identity map is a local file; DC readers always denied | R2-01 must close before retrieval | **Not ready** |
| LRN-03 | Good | Exist (opt-in); recorded user-run PostgreSQL 1/1, GCS 1/1 | Recorded GCS SHA-256 matches | Partial (LRN-01 canonical runs) | GCS/IAM immutability not enforced by code; roles via `manage.py` only | R2-04 role split | **Closest to ready**; needs R2-04 |
| LRN-04 | Good (extractor, resolver, sink) | Opt-in tests exist; fe8bdb3 commit says they were not run on PostgreSQL | Local real-ADO JSONL runs exist (gitignored); the required "5 team-reviewed links" not recorded | None through graph or lessons | n/a beyond LRN-01 | R4-01/02/03 corrupt fix truth | **Not ready** |
| LRN-05 | Good (webhooks, inbox, quarantine, tombstones) | Opt-in tests exist, no recorded run | None ("changed ticket within one cycle" not shown) | None | **No scheduler / reconcile job** | Webhook auth sound; R1-08 | **Not ready** |
| LRN-06 | Good for happy paths | Opt-in tests exist | No approved demo repo (decision D03 open) | None (no trigger from LRN-04) | No job definition; HEAD-only; local cache lost on Cloud Run | R6-04 scaling | **Not ready** |
| LRN-07 | Good (traversal, auth, tokens) | Opt-in tests exist; `TicketFix`/`BuildLink` materializers never executed by any test | None | None ("defect -> fix -> test, restricted node hidden" not shown) | Entra app not registered; no rate limit | R7-06 granularity needs sign-off | **Not ready** |

Precise gaps blocking end-to-end acceptance:

1. **Local:** a disposable PostgreSQL run of all DB-gated tests (`VISIONIQ_*_DB_INTEGRATION=1`, `LRN03_TEST_DB=1`) with recorded output; a seeded end-to-end test (ADO fixture -> canonical -> ticket-fix -> scan at the fix commit -> graph -> API); R7-01/R7-02 so that test can pass.
2. **CI:** `.github/workflows/` is empty. Every "runs in CI" statement in the docs is unverified. The prepared workflow needs a token with Workflows permission.
3. **Cloud:** ingestion + scanner Cloud Run Jobs and Cloud Scheduler (FND-06); Entra app registration and OBO grant; Secret Manager for PAT/Confluence/DB/pseudonym keys; split DB roles; GCS retention/no-delete IAM for runtime identities; `sslmode=verify-full`.

---

## 5. Proposals that need your decision before implementation

These change schema, scope or architecture, so they were **not** implemented:

1. **Changed-file graph relation (R7-01):** add `commit_changed_file` (and optionally `pr_changed_file`) via a new migration, materialized from `fix_change_sets`. This is what makes the LEARN chain connect.
2. **Path query instead of raising hops (R7-02):** a typed `defect -> fix -> changed files -> symbols/tests` endpoint, keeping generic traversal at 3 hops.
3. **Work-item `asOf` snapshot (R1-01)** and **commit overlap window (R1-02):** change checkpoint semantics; need a DB-backed test before merging.
4. **PR-level revert and link retraction (R4-01, R4-02)** plus **PR status (R4-03):** touches `ticket_fix_links` schema and three consumers.
5. **DB role split and immutability (R2-04, R4-09, F04-01):** separate ingestor-writer and API-reader roles; REVOKE UPDATE/DELETE on immutable tables; move grants into migrations.
6. **Confluence identity normalization (R2-03):** changes existing item keys; needs a re-keying migration.
7. **Scanner `--commit` and a scan job fed by new fix change sets (R6-09)**, with ancestor-ordered rename detection (R6-03).
8. **Retrieval fail-closed for Confluence text (R2-01):** make `live_check` mandatory. Small, but changes the `retrieve()` contract that LRN-08/09 consume.

---

## 6. Local and cloud end-to-end test plan

Principle: a step passes only when its output is recorded (date, commit, command, counts). No step may use corporate production data without approval; use a sandbox ADO project, a sandbox Confluence space, an isolated bucket and a disposable database.

### 6.1 Local, deterministic (no external services)

| Step | What | How | Pass criterion |
|---|---|---|---|
| L1 | Unit suites | `dotnet test VisionIQ.sln`; `.venv/Scripts/python -m pytest` | 0 failed; skips only opt-in |
| L2 | Fresh schema | disposable `pgvector/pgvector:pg16`; `python -m db.manage migrate` then `bootstrap`; `verify` | head = `20261103_0018`; roles and RLS present |
| L3 | All DB-gated tests | set `VISIONIQ_DB_INTEGRATION`, `VISIONIQ_SCAN_DB_INTEGRATION`, `VISIONIQ_GRAPH_DB_INTEGRATION`, `VISIONIQ_TICKET_FIX_DB_INTEGRATION`, `VISIONIQ_EVIDENCE_DB_INTEGRATION`, `LRN03_TEST_DB`=1 against L2 | 0 failed, 0 skipped among gated tests |
| L4 | Replay idempotency | run the canonical sync twice from recorded ADO fixtures (`tools/Lrn04Replay`) | second run: 0 new revisions, checkpoints unchanged |
| L5 | **E2E chain (new test to write)** | fixture: Bug 4521, PR 812 (`c1`,`c2`,`m`), changed files; git repo at `m` with `TotalCalculator.cs` + test; run sink -> materializers -> scanner at `m` -> API with a locally signed token | API returns 4521 -> PR 812 -> `m` -> `TotalCalculator.cs` -> `Total` and the test; *requires R7-01/R7-02* |
| L6 | Access E2E | same fixture, caller not in project team / restricted Confluence page | 404 for root; restricted nodes absent; no counts leak |
| L7 | Revert E2E | add commit "This reverts commit `m`" (both arrival orders) | PR 812 hidden from 4521 in graph and lessons (*requires R4-01*) |
| L8 | Deletion E2E | Recycle Bin fixture for 4521 | `deleted_at` set; graph root 404; restore clears it |
| L9 | Large repo budget | scan a 10k-file synthetic repo | completes within agreed time and memory (*requires R6-04*) |

### 6.2 Sandbox real sources (local machine)

| Step | What | Pass criterion |
|---|---|---|
| S1 | ADO sandbox sync, interrupted and resumed | counts per source match ADO; no duplicates; unreachable source shows NOT SYNCED |
| S2 | Five real ticket -> fix links reviewed by the team (LRN-04 completion evidence) | all correct or errors logged in `ticket_fix_reviews` |
| S3 | ADO Revert button on a sandbox PR (M-8) | resulting message recorded; revert detected or gap logged |
| S4 | Confluence sandbox page restricted to user A | B denied, A allowed, through the graph API |
| S5 | Webhook: edit a sandbox work item | appears within one cycle; inbox row `processed` |
| S6 | Move a work item between sandbox projects | old project no longer serves it (*requires R1-03*) |

### 6.3 Cloud (staging project, never production)

| Step | What | Pass criterion |
|---|---|---|
| C1 | Cloud SQL migrate via `db/cloud_manage.py` + role bootstrap | `verify` passes; API role cannot UPDATE/DELETE revisions (*after R2-04*) |
| C2 | Ingestion Cloud Run Job + Scheduler against the sandbox org | scheduled runs recorded in `source_sync_runs`; checkpoints advance |
| C3 | `serve-webhooks` on Cloud Run behind TLS with Secret Manager secret | ADO service hook delivers; dead-letter path visible |
| C4 | Scanner job at fix commits | `code_scan` evidence per fix commit; graph bridged |
| C5 | Graph API with real Entra app + OBO from the Impact Agent | allowed/denied users behave as L6; rate limit in place |
| C6 | GCS immutability | runtime identity cannot delete/overwrite; retention policy set |
| C7 | Observability | logs contain no tokens; pseudonymized identities; alert on dead letters and ignored webhooks |

---

## 7. Documentation consolidation plan

To be applied **after** LRN-01-07 integration and testing; nothing was rewritten in this review.

### 7.1 Problems found

- **Status contradictions for the same story:** LRN-03 is "COMPLETE" in its feature README, "In progress / all criteria Open" in `lrn03-canonical-evidence-contract.md`, "nothing Accepted" in the quality matrix, and "Not started" in `VisionIQ_Master_Backlog.xlsx` (all 236 tasks are "Not started", including merged FND-03/04). LRN-01's `acceptance.md` says PASS and "warning review completed" while its README says warnings remain and `LRN-01-Test-Report.md` says "NOT YET FULLY ACCEPTED" (and contradicts itself on changed-file fidelity). LRN-04's README says focused tests were not executed while `01-Local-Testing.md` records 25/25.
- **Claims of CI that does not exist:** quality matrix, `blocker-fixes-b01-b17.md`, `graph-api.md`, `kb-readiness.md`, `lessons.md`, `lrn07-relationship-graph.md`, `learn-ingestion-integration.md`.
- **Docs contradicting code:** `lrn07-relationship-graph.md` says there is no graph endpoint (there is); `review-followups-lrn01-07.md` and `lrn04-ticket-to-fix.md` say commit mentions stay explicit (code makes them inferred); M-16 called "needs a migration" (migration 0007 exists); `learn-01-06-integration-closure.md` uses the wrong scanner DB login; `lrn05-backfill-currentness.md` says no webhook endpoint exists; migration heads quoted as 0003/0005/0006/0012 (actual 0018); README and setup PDF say .NET 8 (code is .NET 10).
- **Unresolvable references:** branches `feature/learn`, `feature/learn-08-14`; PRs #3/#5/#6/#7/#9/#12/#14 as LEARN PRs (in this repo #5 is FND-03, #8 is FND-04); missing commits. These docs came from another repository and history was squashed into `e9933be`.
- **"LRN-15"** is used in several docs but is not an official story.
- **`CLAUDE.md` / `AGENTS.md`** say only `README.md` is committed and that it is UTF-16; both false. `AGENTS.md` points at `.Codex/settings.json` (actual: `.codex/config.toml`).
- **`docs/Functional/`:** `VisionIQ_Cloud_Structure.pdf` is embedded in the Consolidated PDF; `VisionIQ_Google_Cloud_Setup_Deployment_Guide.md` and the setup PDF are superseded; `VisionIQ_Task_List_deprictiated.xlsx` is superseded (misspelled); two diagrams share number 08; `.docx` and `.pdf` user stories duplicate each other. The v1.11 design PDF, user stories, backlog, consolidated cloud PDF and diagrams are the reference set.
- **Now outdated because of this review:** the LRN-03 README follow-up and `blocker-fixes` M-10 note about opt-in tests reporting PASSED/FAILED (fixed by D-02).

### 7.2 Evidence that must be preserved (verbatim, archived)

- `docs/LRN-01-Test-Report.md` and `features/LRN-01-history-connectors/acceptance.md` (run IDs, 20/20 field matches, 5/5 GCS hashes, invalid-PAT NOT SYNCED exit 2).
- `features/LRN-02-confluence/03-Test-Report-and-Evidence.md` (589-page inventory, ACL split, run IDs).
- `features/LRN-03-versioned-evidence/04-Test-Evidence-and-Acceptance.md`, `05-GCS-Archive-Verification.md`, troubleshooting incident table.
- `features/LRN-04-ticket-fix-links/01-Local-Testing.md`, `03-Acceptance-and-Findings.md` (F04-01 still open).
- `learn-quality-acceptance-matrix.md` (criterion -> test mapping).
- Local, gitignored: `artifacts/ingestion/**/report.json` (incl. `lrn04-real-ado` runs on 2026-11-04) and `test-backups/`. Copy sanitized summaries (counts and run IDs only) into the archive.

### 7.3 Target structure

```
README.md                         short; .NET 10; links docs/README.md
CLAUDE.md / AGENTS.md             regenerated from one source
docs/
  README.md                       index + source-of-truth order: v1.11 design > user stories > status matrix
  status/learn-feature-status.md  THE status matrix (per AC: implemented / test exists / run recorded / live evidence / accepted by whom)
  architecture/learn-architecture.md   how LRN-01..07 connect; tables 0001-0018; ACL model; decisions
  testing/learn-e2e-testing-and-acceptance.md   section 6 of this report, kept current
  features/LRN-0x-*/README.md     one page each: story + AC, code map, how to run/test, known gaps, link to status
  ops/                            local-setup, env-sync, deployment (FND-04), ingestion-operations, graph-api-setup, vertex-ai-setup, logging, troubleshooting
  reference/                      (was Functional/) v1.11 PDF, user stories, backlog, consolidated cloud PDF, diagrams
  review/                         this report and later reviews
  archive/YYYY-MM-DD_<topic>/     historical reports, unchanged, each with a "superseded by" line
```

### 7.4 Mapping of existing documents

| Existing | Destination |
|---|---|
| `learn-quality-acceptance-matrix.md` | basis of `status/learn-feature-status.md` (remove CI claims, update migrations) |
| `lrn03-canonical-evidence-contract.md`, `blocker-fixes` decisions, still-valid parts of `review-followups` (evidence levels, environment classifier, build links) | `architecture/learn-architecture.md`; rest archived |
| `phase1-learn-integration-plan.md` (Checkpoint A), `learn-01-06-integration-closure.md`, `learn-ingestion-integration.md`, `features/*/local-testing.md` | `testing/...`; originals archived |
| `lrn04-ticket-to-fix.md`, `lrn05-sync.md`, `lrn06-code-intelligence.md`, `lrn07-relationship-graph.md` + `graph-api.md` | feature READMEs for LRN-04..07 (create LRN-05/06/07 folders); fix stated contradictions |
| `LEARN-01-02-Command-Register.md`, LRN-01 `operations.md`/`cloud-deployment.md`, LRN-02 `02`, LRN-03 `03`, `env-sync.md`, `fnd04-manual-deployment.md`, `logging.md`, `vertex-ai-setup.md`, `db/README.md` | `ops/` |
| Test reports and audits listed in 7.2, `lrn05-backfill-currentness.md`, LRN-03 `01`/`06`, `blocker-fixes`, `review-followups` | `archive/` |
| `visual-guide.md`, LRN-02 `05` | delete or keep one as the only explainer |
| `lessons.md`, `search-index.md`, `retrieval.md`, `snapshots.md`, `kb-readiness.md` | LRN-08..13 feature folders; replace "LRN-15" with official IDs |
| `docs/Functional/` superseded files (30 Oct cloud PDF, GCP setup `.md`, setup PDF, deprecated task list) | `archive/superseded-reference/` |

Decisions needed before writing the status matrix: is the backlog `.xlsx` or the new matrix the system of record; will a CI workflow be committed (otherwise remove every CI claim).

---

## 7a. Second round: fixes against real ADO data, and a second reviewer's feedback

After this report, the user supplied local ingestion runs against their real Azure DevOps org
(`artifacts/ingestion/`, gitignored). Checking those confirmed three findings were live, not
hypothetical, with real IDs:

- **R4-07**, PRs 38155 and 38394 in the real data are titled `Revert "..."`/`Revert '...'` with no
  `This reverts commit <sha>` anywhere — ADO's own Revert button, not `git revert`.
- **R4-03**, PR 38497 (abandoned) and six real active PRs carry real work-item links.

A second AI reviewed this plan and raised three points, all acted on:

1. **Don't treat a title match as a verified revert.** Implemented: a title match only records a
   revert when both the original and the new PR also agree on a real Git SHA, and an ambiguous title
   (shared by more than one earlier PR) resolves nothing.
2. **Don't discard active/abandoned PR relationships.** Implemented, and better than the first draft:
   the schema already had an unused `Unknown` classification value; these are now stored as `Unknown`
   (kept, not discarded) and upgrade to `Explicit` automatically once the PR completes. Zero schema
   change, because every consumer already required `explicit`/`inferred`.
3. **Fix R1-01/R1-02 before graph work.** Done in this order.

Fixed and regression-tested this round (same standard: every new test fails on the pre-fix code):

| ID | Fix | Verified against |
|---|---|---|
| R4-07 | Detect ADO's own `Revert "<title>"` pull requests; resolve only an unambiguous, same-process, same-SHA match | Real PR 38386/38394 titles and commits |
| R4-03 | Abandoned/active PR links kept as `Unknown`, not `Explicit`; self-upgrades on completion | Real PR 38497 and 6 active PRs |
| R1-01 | `workitemsbatch` now reads field values "as of" the WIQL query's own snapshot time; checkpoint additionally clamped to that snapshot as defense in depth | Fake-server race-condition test; matches the real WIQL response's own `asOf` field already present in the test fixture |
| R1-02 | A future-dated commit (bad local clock) no longer advances the checkpoint past the real current time | Fake-server future-date test |
| R1-03 | A work item observed under a new ADO project now tombstones its old project's copy (`missing_in_source`), the same mechanism as a real deletion | New store query `FindOtherProjectItemKeysAsync` against existing, unchanged schema columns |
| R4-01 | The `ticket_pr` graph edge is now hidden when **any** sibling row of the same pull request is reverted or disputed, not just the one row the edge happens to cite | SQL verified directly against PostgreSQL 16 with representative rows; a DB-integration test (`VISIONIQ_GRAPH_DB_INTEGRATION`) was added but not executed here |

None of R1-01/R1-02/R1-03/R4-01 required a schema change. Still open and still requiring a decision
before implementation, per this project's own rule on architectural/DB changes: R4-02 (link
retraction), R4-04 (revert-of-revert), R7-01/R7-02 (commit-to-changed-files graph edge, 4-hop
traversal), R6-09 (scan trigger), R2-03 (Confluence identity re-keying), R2-04 (DB role split).

## 7b. Third round: the code-intelligence graph chain (R7-01, R7-02, R6-09)

**A simpler design than originally scoped, found during investigation.** File nodes in the Python
graph (`GraphCodeNode(scan_item_key, "file", path)`) are anchored to the *repository's* scan
identity, which is stable across every commit ever scanned for that repo — not to the specific
commit scanned. This means a historical fix commit's changed files can be wired into the graph
**without re-scanning that exact historical commit**, as long as the repository has been scanned at
all, at any commit. This also meant **R7-02 needed no change at all**: the new edge goes directly
from a ticket's commit/PR to the changed file (skipping the "every file in the tree" `commit_file`
relation), so the full chain — ticket → commit/PR → file → symbol/test — is 3 hops, exactly the
existing limit (confirmed by reading `AuthorizedGraphTraversal.cs`, which already accepts
`hops=3` with no relation-type allowlist).

| ID | What was built | Verified |
|---|---|---|
| R7-01 | New migration (`graph_assertions_relation_type_check` extended with `commit_changed_file`/`pr_changed_file`); new Python function `project_fix_change_edges` reading `evidence.fix_change_sets` and writing a direct edge from the fix's own (still-current) revision to the file node, reusing the exact node/assertion-insert pattern `persist_code_graph` already uses; wired into `ingest_cli.py` right after `persist_code_graph`, so every scan re-reconciles the full historical fix backlog for that repository (self-healing) | Migration validated against PostgreSQL 16 directly. New DB-gated unit test (`test_fix_change_edges_reach_the_same_file_node_a_scan_would_use`) proves the edge lands on the *same* file-node key `project_scan_edges` would use for that path — not run against a live DB in this session, compiles and collects. |
| R7-02 | No code change — confirmed unnecessary by the R7-01 design above | Read `AuthorizedGraphTraversal.cs`: `hops` already accepts 1-3 with no relation-type filter |
| R6-09 | `scan_repository`/`ingest_cli.py` gained `commit`/`--commit`, to scan an exact historical SHA instead of only the checkout's current HEAD; routed around the HEAD-only last-good cache entirely (a historical scan is not "the previous good state" of HEAD) | `test_an_exact_commit_can_be_scanned_without_checking_it_out`: passes with the fix, reproduced failing without it (asserted the wrong commit's file set) |

**Still not solved by this round, and explicitly out of scope for code alone:** nothing yet
*automatically* invokes `ingest_cli.py --commit <sha>` when LRN-04 discovers a new fix commit —
that is a job/scheduling decision (a queue fed by new `ticket_fix_links`/`fix_change_sets` rows, run
as a Cloud Run Job or similar), not something that belongs in this code change without you choosing
the infrastructure. Also not verified: this round's DB-gated tests were written and compile-checked
only, not executed against a live migrated database, consistent with every other round in this
review.

## 7c. Fourth round: revert-of-revert (R4-04)

**The gap:** `evidence.ticket_fix_links.revert_commit_shas` was a pure append-only log of every
revert ever observed. Once a fix's commit was reverted, it stayed hidden forever — even if someone
later reverted that revert (restoring the original fix) and even if a further revert hid it again.
The field answered "was this ever reverted", not "is this currently reverted".

**The fix:** `revert_commit_shas` is now a recomputed *current-state* cache, the same architectural
move already made for retraction (R4-02) — the underlying observation log
(`evidence.ticket_fix_revert_observations`) stays fully append-only and immutable; nothing is
deleted from it. On every new revert observation, `PostgresTicketFixLinkStore` now:

1. Finds every commit whose status can depend on the new observation — the original commit plus
   everything it, in turn, reverts, walked transitively (bounded, cycle-guarded).
2. Recursively recomputes each one's status: a commit is reverted only if at least one of its
   reverters has *not itself* been reverted. A revert of a revert cancels the earlier revert; a
   further revert re-applies it. This is a general recursive definition, correct for chains of any
   depth, not a hardcoded two-level special case.
3. Overwrites `revert_commit_shas` with exactly that recomputed set (shrinking it back to empty
   when a revert is cancelled, which the old append-only logic could never do).

The same recompute now also runs whenever a *new* ticket-fix claim is created for a commit that
already has revert history, replacing an inline fold-in subquery that — like the old update path —
blindly unioned every historical revert without accounting for cancellation.

**Verified:** the exact SQL used by each step was run directly against PostgreSQL 16 through a
3-level chain (`b` reverts `a`; `c` reverts `b`, restoring `a`; `d` reverts `c`, re-hiding `a`) and
matched a hand-derived trace of the algorithm at every step. The existing fe8bdb3 regression test's
one assertion that depended on the old always-append semantics was updated to the new, more
accurate meaning (replaying an unchanged revert now correctly reports 0 rows changed, not 1); every
other existing assertion in that test, and all other revert/dispute/retraction tests, pass
unchanged. A new opt-in PostgreSQL test (`A_revert_of_a_revert_restores_the_original_fix_and_a_further_revert_hides_it_again`)
was added and compiles, but — consistent with every round in this review — was not executed against
a live database in this session.

**Known, accepted scope limitation, stated explicitly rather than silently assumed away:**
concurrent `RecordRevertAsync` calls are serialized against the revert's own original commit, not
every commit the recompute may touch further up a chain. A real collision would need two revert
observations for the same multi-level chain to land at almost the same instant — the ingestion
sink already processes one PR or commit write at a time, making this a low-probability residual
risk rather than a solved one.

## 7d. Fifth round: R1-06 (active PR updates missed) and R2-01 (optional security check)

**R1-06.** The PR sync stream's delta cursor is `LastActivity` (`ClosedDate ?? CreatedDate`). While a
PR stays active/open, `ClosedDate` is null, so `LastActivity` never changes — meaning a new push, a
vote, a comment, or a newly linked work item on an open PR was silently never picked up again after
the PR's creation, until it eventually closed or a webhook happened to fire for it. Fixed by always
re-enriching and re-writing every currently-active PR on every run, regardless of the cursor;
closed/completed PRs are unaffected (still cursor-gated, since their `LastActivity` is stable once
set). This is safe to do cheaply because `ContentRevision` already hashes each PR's full fetched
content (commits, threads, work items, policies) — re-checking an unchanged open PR produces the
same revision and is a no-op at the evidence layer; only a genuine change registers as new. Verified:
a new test (`An_open_pull_request_is_re_checked_on_every_run_but_a_closed_one_is_not`) fails on the
pre-fix code (the still-open PR in the demo fixture is never seen again on a second/third run) and
passes with the fix; the pre-existing `A_second_run_resumes_from_checkpoints_without_duplicates` test
was updated to its new, correct expectation (the open PR, and only it, now shows up on a repeat run).

**R2-01.** `retrieve()`'s live Confluence/ADO access re-check (`live_check`) was an optional
parameter defaulting to `None`, silently skipped if a future caller forgot to pass it — meaning
content whose access was restricted *after* it was indexed could be served from the stale stored
ACL alone. Confirmed no caller anywhere in `src/` invokes `retrieve()` today, so this was tightened
with zero risk to production code: `live_check` is now a required parameter with no default. Every
test call site was updated to pass one explicitly (most pass `lambda key: True`, a visible,
deliberate opt-out; the one test that exercises the real behavior is unaffected). Verified: a new
test asserts the parameter has no default and that calling `retrieve()` without it raises
`TypeError`; confirmed to fail against the pre-fix signature and pass with the fix.

## 7e. Sixth round: second-pass correction from an independent review of R4-02/R4-04/R1-06, cross-checked against LRN-05

A second, independent review of the R4-02 (link retraction), R4-04 (revert-of-revert) and R1-06
(active-PR re-check) work flagged that these three features interact and must be tested together
with LRN-05, not in isolation. Four findings were investigated; all four were confirmed and fixed.
No unrelated refactoring was done, and nothing was merged or deployed — everything below stays
uncommitted on `review/lrn-01-07-integration`.

**Finding #1 (High) — active-PR revision freshness, confirmed.** `AcceptRevisionAsync`'s guard used
a strict `source_revision_time < EXCLUDED.source_revision_time` comparison to reject out-of-order
replays. R1-06 (fifth round, above) deliberately re-checks every open PR on every sync run regardless
of its cursor, because an open PR's `LastActivity` (`ClosedDate ?? CreatedDate`) never changes while
it stays open — but that is exactly the same timestamp `AcceptRevisionAsync` uses to decide whether a
new revision is "newer". The combination meant the *first* re-check of an open PR after R4-02's
retraction/reconciliation logic shipped was accepted, and every re-check after that was silently
rejected forever, even when the PR's real content (a changed linked ticket, a new commit) had
changed — exactly the cross-feature regression the second reviewer predicted. Reproduced directly
against the disposable PostgreSQL container: a second reconciliation of the same source item, same
`source_revision_time`, different (content-hash-derived) `source_revision_id`, was rejected
(0 rows accepted). Fixed in `PostgresTicketFixLinkStore.AcceptRevisionAsync`
([PostgresTicketFixLinkStore.cs](../../src/VisionIQ.Api/Features/TicketFixes/PostgresTicketFixLinkStore.cs))
by also accepting when the time is equal but the revision id differs; a time that is strictly older,
or a revision with an identical id and time (a true replay), is still rejected exactly as before.
Verified against real PostgreSQL (content-changed-same-time → accepted; identical replay → rejected;
strictly-older out-of-order revision → rejected), and with a new xUnit DB-integration test,
`An_active_prs_recheck_is_accepted_when_content_changed_even_at_the_same_timestamp`
([TicketFixDatabaseIntegrationTests.cs](../../tests/VisionIQ.Api.Tests/TicketFixDatabaseIntegrationTests.cs)),
covering the accept case plus both the negative (identical replay) and out-of-order (older revision)
cases the reviewer asked for; confirmed to fail against the pre-fix comparison and pass with the fix.

**Finding #2 (High) — graph sibling suppression, confirmed.** `PostgresRelationshipGraphStore
.OutgoingAsync`'s sibling-suppression `EXISTS` subquery (added in the fourth round for R4-01, to
propagate a revert or a dispute from one commit's association row to its sibling rows for the same
ticket/PR pair) also included `sibling.retracted_at IS NOT NULL` in its `OR` clause. A link is
retracted whenever its source simply stops asserting it (R4-02 — e.g. a PR's commit list changes
from an old commit to a replacement one after a force-push or amend), which is unrelated to a revert.
Including it in the sibling check meant retracting the *old, superseded* commit's association wrongly
hid the *new, currently valid* replacement association for the same ticket/PR pair too. Reproduced
against real PostgreSQL: a valid replacement commit association was incorrectly suppressed once its
superseded sibling was retracted. Fixed by removing `sibling.retracted_at IS NOT NULL` from the
sibling-propagation clause in
[PostgresRelationshipGraphStore.cs](../../src/VisionIQ.Api/Features/Graph/PostgresRelationshipGraphStore.cs)
(the row's own `fix.retracted_at IS NULL` check, and the revert/dispute sibling-propagation R4-01
relies on, are both untouched). Verified against real PostgreSQL (the valid replacement is now
visible; a genuinely-reverted PR's sibling rows both still correctly stay hidden, confirming R4-01 is
intact) and with a new xUnit DB-integration test,
`Retracting_a_supersede_commit_does_not_hide_its_valid_replacement_sibling`
([GraphDatabaseIntegrationTests.cs](../../tests/VisionIQ.Api.Tests/GraphDatabaseIntegrationTests.cs));
confirmed to fail against the pre-fix `OR` clause and pass with the fix.

**Finding #3 (High) — PR-title revert over-confidence, confirmed.** `RecordTitleRevertAsync`
(`TicketFixProjectionSink`, from R4-07/second-pass-correction) called `RecordRevertAsync` — the same
full-confidence call used for a literal `git revert`-style "This reverts commit &lt;sha&gt;" message —
whenever a pull request's title matched ADO's own `Revert "..."` convention against an earlier,
uniquely-titled PR, with SHA-shaped commit ids on both sides. A title match is circumstantial (titles
are free text, can be edited, and the "revert" relationship it implies is a claim about the title, not
a verified claim about the diff), so treating it as equally authoritative as an explicit revert
message was over-confident and could wrongly suppress a verified fix. Fixed by rewriting
`RecordTitleRevertAsync` in
[TicketFixProjectionSink.cs](../../src/VisionIQ.Ingestor.Cli/TicketFixProjectionSink.cs) to stop
calling `RecordRevertAsync` and instead append a `candidate-revert:pr-title-match:pr<id>:<sha>`
marker as an additional evidence reference on the original ticket's claim, via the existing
`UpsertSourceAsync` path — recorded and queryable, but never suppressing the original fix outright.
Verified with the existing regression test, renamed to
`A_pr_titled_as_ados_own_revert_of_a_pr_seen_earlier_records_a_candidate_not_a_confirmed_revert`
([TicketFixProjectionSinkTests.cs](../../tests/VisionIQ.Ingestor.Tests/TicketFixProjectionSinkTests.cs)),
updated to assert `links.Reverts` stays empty and the candidate marker is present; confirmed to fail
against the pre-fix code (which asserted a confirmed revert pair) and pass with the fix.

**Finding #4 (Medium) — historical retraction lifecycle, confirmed.** `ticket_fix_links.retracted_at`
and `ticket_fix_link_source_associations.ended_at` each only record the *current* state of a
transition. A link that is retracted, later relinked (clearing the retraction), then retracted again
a second time left no trace that the first retraction ever happened — an auditor could see only
where the association stands today, not the cycle it went through. Fixed by adding a purely additive,
append-only `evidence.ticket_fix_link_lifecycle_events` table
([20261105_0021_ticket_fix_link_lifecycle_events.py](../../db/migrations/versions/20261105_0021_ticket_fix_link_lifecycle_events.py))
and a `RecordLifecycleEventAsync` helper in
[PostgresTicketFixLinkStore.cs](../../src/VisionIQ.Api/Features/TicketFixes/PostgresTicketFixLinkStore.cs)
that inserts one immutable `retracted` or `cleared` row per real transition (never on a no-op
re-write) from `UpsertCoreAsync` (clearing) and `ReplaceSourceAssociationsAsync` (retracting); the
existing current-state columns are unchanged and remain the fast path for "is this retracted right
now". Verified against real PostgreSQL and with a new xUnit DB-integration test,
`Repeated_retract_and_relink_cycles_leave_a_full_lifecycle_history`
([TicketFixDatabaseIntegrationTests.cs](../../tests/VisionIQ.Api.Tests/TicketFixDatabaseIntegrationTests.cs)),
which drives two full retract/relink cycles and asserts all three transitions
(`retracted` → `cleared` → `retracted`) are present in order; confirmed to fail against the pre-fix
code (zero event rows) and pass with the fix. The existing
`Reconcile_retracts_a_dropped_link_and_rejects_a_stale_replay` test's cleanup was also updated to
delete from the new table first, since it now has an `ON DELETE RESTRICT` foreign key to
`ticket_fix_links`.

**Verification summary for this round.** All four findings were reproduced against the disposable
PostgreSQL test container before fixing, and every regression test listed above was confirmed to
fail on the pre-fix code and pass with the fix in place (the same "red, then green" discipline used
throughout this review). The full suites were re-run afterward with no regressions:
`dotnet test VisionIQ.sln` — 456 passed, 15 skipped (opt-in DB-integration tests not relevant to this
run), 0 failed; `python -m pytest` — 169 passed, 83 skipped (opt-in DB-integration tests), 0 failed.

Nothing in this round is committed, pushed, merged, or deployed, per standing instruction — all
changes remain as uncommitted edits on `review/lrn-01-07-integration`. There is accordingly no commit
SHA to report for this round; one will exist once these changes are committed (by the project owner,
as agreed).

## 7f. Seventh round: third-pass correction, two more risks flagged against the sixth round's fixes

After the sixth round (§7e) shipped, a further review of that round's own fixes raised two more risks,
both confirmed and fixed. No unrelated refactoring; still nothing committed, pushed, merged, or
deployed.

**Risk #1 — candidate revert could reactivate a retracted link, confirmed.** `RecordTitleRevertAsync`'s
fix in the sixth round stopped it from calling `RecordRevertAsync`, but it still called the public
`UpsertSourceAsync`, which unconditionally clears `retracted_at` (correct for a direct, explicit,
source-stated claim, but this candidate marker is neither). If the *original* ticket/PR/commit
association had already been retracted by a newer, authoritative `ReconcileSourceAsync` snapshot (e.g.
the ticket was genuinely unlinked from the PR), a later title-matched "revert" candidate would
incorrectly resurrect it — reactivating a link current evidence says does not exist. Fixed by adding a
dedicated `ITicketFixLinkStore.UpsertCandidateEvidenceAsync` method
([PostgresTicketFixLinkStore.cs](../../src/VisionIQ.Api/Features/TicketFixes/PostgresTicketFixLinkStore.cs))
— identical to `UpsertSourceAsync` except it always passes `clearRetraction: false` to the shared
`UpsertCoreAsync` core — and switching `RecordTitleRevertAsync`
([TicketFixProjectionSink.cs](../../src/VisionIQ.Ingestor.Cli/TicketFixProjectionSink.cs)) to call it
instead. Verified with two new tests, both confirmed to fail against the pre-fix
(`UpsertSourceAsync`-calling) code and pass with the fix: a sink-level test,
`A_title_matched_candidate_revert_never_reactivates_a_link_a_newer_revision_already_retracted`
([TicketFixProjectionSinkTests.cs](../../tests/VisionIQ.Ingestor.Tests/TicketFixProjectionSinkTests.cs)),
and a real-PostgreSQL DB-integration test,
`Candidate_evidence_never_reactivates_an_already_retracted_association`
([TicketFixDatabaseIntegrationTests.cs](../../tests/VisionIQ.Api.Tests/TicketFixDatabaseIntegrationTests.cs)),
which also contrasts the fix against the normal `UpsertSourceAsync` path (a direct, explicit claim
still legitimately re-links).

**Risk #2 — equal-timestamp acceptance could be confused with an older, delayed snapshot, not
confirmed as a code defect, but a test was missing as asked.** The sixth round's `AcceptRevisionAsync`
fix accepts an incoming revision when its timestamp exactly matches the one on file but its revision id
differs (needed so an active PR's re-checks, whose `LastActivity` never advances, aren't rejected
forever). Re-reading the guard's `WHERE` clause confirms it only takes that branch when the timestamps
are *equal*; a revision whose timestamp is *strictly older* than the one on file is unconditionally
rejected by the first `OR` branch regardless of its id or content, so a genuinely older, delayed
snapshot cannot override the current state. No code change was needed, but — per the request — a
dedicated regression test was added to lock this in and catch a future regression that widens the
guard too far:
`An_older_delayed_revision_with_different_content_still_cannot_override_the_current_state`
([TicketFixDatabaseIntegrationTests.cs](../../tests/VisionIQ.Api.Tests/TicketFixDatabaseIntegrationTests.cs)).
To prove the test is not vacuous, the guard was temporarily widened to a naive "any different revision
id is accepted, regardless of time" rule and the test was confirmed to fail against it, then the
correct guard was restored and the test passes again.

**Verification summary for this round.** Both risks were investigated by reading the exact SQL/code
path end to end and, for Risk #1, reproduced against the disposable PostgreSQL test container before
fixing. All new/changed tests were confirmed red-then-green against real PostgreSQL. Full suites were
re-run afterward with no regressions: `dotnet test VisionIQ.sln` — 457 passed, 17 skipped (opt-in
DB-integration tests not relevant to this run), 0 failed; `python -m pytest` — 169 passed, 83 skipped,
0 failed (no Python files changed this round). Nothing is committed, pushed, merged, or deployed.

## 8. Changes on the review branch

Uncommitted, on `review/lrn-01-07-integration` (branched from `feature/learn-01-14-TEST` at `44ec036`).

| File | Change |
|---|---|
| `src/VisionIQ.Api/Features/Evidence/EvidenceModels.cs` | D-01 closed-vocabulary environment metadata |
| `src/VisionIQ.Ingestor/Links/LinkExtractor.cs` | R4-05/R4-06 regexes |
| `src/VisionIQ.Ingestor/Confluence/ConfluenceText.cs` | R2-05 properties regex |
| `src/VisionIQ.Ingestor/Confluence/ConfluenceHttp.cs` | R2-06 same-site paging links |
| `src/VisionIQ.Ingestor/Sync/JsonLinesSink.cs`, `SyncRunner.cs`, `Confluence/ConfluenceSyncRunner.cs` | R1-04 flush before checkpoint |
| `src/VisionIQ.Intelligence/app/code_intelligence/scanner.py` | R6-01 per-file downgrade on `RecursionError` |
| `src/VisionIQ.Intelligence/app/knowledge/retrieval.py` | R6-02 symbol-name pattern |
| `tests/VisionIQ.Ingestor.Tests/OptInFactAttribute.cs` (new), `Lrn03GcsArchiveIntegrationTests.cs`, `Lrn03PostgresIntegrationTests.cs` | D-02 |
| `tests/VisionIQ.Api.Tests/EvidenceIngestionTests.cs`, `tests/VisionIQ.Ingestor.Tests/LinkExtractorTests.cs`, `ConfluenceReviewRegressionTests.cs` (new), `CheckpointDurabilityTests.cs` (new), `tests/intelligence/test_grammar_gaps.py`, `tests/intelligence/test_review_symbol_lane.py` (new) | regression tests |

Note on D-01 for existing data: rows already stored keep their old redacted metadata (the normalizer version string is unchanged, so replays of unchanged content are still recognized). A one-time re-normalization of `work_item_current` revisions is needed if environment filtering must cover history.
