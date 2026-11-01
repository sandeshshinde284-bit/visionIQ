# LRN-01..07 review: open items and decisions

Status after the blocker (B-1..B-17) and medium/minor fix rounds. Fixed items are described in
their commit messages ("B-", "M-", "m-" prefixes).

## Needs a decision

| Item | Question | Notes |
|---|---|---|
| M-7 | Should commit-message mentions (`#1234`) stay **explicit** graph edges? | The current design treats a mention as an explicit reference that is *not* proof of a fix (see `TicketFixResolver` and the tests `A_ticket_mentioned_in_a_commit_is_only_an_explicit_reference` / `A_commit_message_mention_is_explicit_but_not_proof_of_a_fix`). The design doc (§6.18) separates mentions from development links. Reclassifying them as inferred would drop mention-only links from the default graph; ADO development links (with repository since B-11) would remain. Left unchanged on purpose. |
| M-16 | Add file-rename handling to the graph? | Needs a new relationship type (database migration + C# enum + Python projection), using `git diff -M` between the previous and current scanned commit. |

## Test evidence levels (LRN-07)

A test node is **not** proof that a defect is covered. The graph keeps these levels separate, and no API
may present a weaker level as a stronger one:

| Level | Meaning | Source | Status |
|---|---|---|---|
| Structural | File F contains test method T | Code scan (`file_test` edge) | Implemented |
| Changed with fix (candidate) | T is in a file changed by a fix commit | Path: fix commit -> file -> test | Derivable; must be labelled a candidate, never "covers the defect" |
| Declared association | The defect is linked to an ADO Test Case ("Tested By") | ADO relation | Planned |
| Mapped automated test | The Test Case's "Automated test name" matches exactly one test method | ADO field + code scan; otherwise "unresolved" | Planned |
| Test result | T passed or failed in a specific CI run | CI test results | Not ingested |

A declared association proves only that someone linked the test to the defect. It does not prove the
test passed, covers all affected code paths, or prevents recurrence.

## Needs live verification (manual testing)

| Item | What to check |
|---|---|
| M-8 | Revert a PR with Azure DevOps' **Revert** button in the sandbox and record the resulting commit/PR message. Only `git revert` messages ("This reverts commit <sha>") are detected today. |
| B-6 | ADO project membership via the team-members API, with a real member and a non-member. |

## Accepted as is (documented, not changed)

| Item | Reason |
|---|---|
| M-4 | Round trips per row/hop. Measure on real data volumes before refactoring (batching would touch the materializer, traversal and Python graph persistence). |
| m-1 | Final tie-break on `assertion_key` when timestamps are equal is deterministic; no better ordering exists in the data. |
| m-6 | The Python image installs test tools and dependencies are not locked. Splitting runtime and test requirements changes CI and the Dockerfile; do it with a lock file. |
| m-10 | Link records use the ingestion time as source time because ADO relation records carry no timestamp. |

## Pending CI change

A workflow update that runs the B-8/B-9/B-15 database tests and the FND-03 role-isolation test in the
fresh-PostgreSQL job is ready, but pushing `.github/workflows` needs a token with the **Workflows**
permission.
