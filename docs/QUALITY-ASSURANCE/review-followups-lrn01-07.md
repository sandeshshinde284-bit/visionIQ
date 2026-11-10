# LRN-01..07 review: open items and decisions

Status after the blocker (B-1..B-17) and medium/minor fix rounds. Fixed items are described in
their commit messages ("B-", "M-", "m-" prefixes).

## Needs a decision

| Item | Question | Notes |
|---|---|---|
| M-7 | Should commit-message mentions (`#1234`) stay **explicit** graph edges? | The current design treats a mention as an explicit reference that is *not* proof of a fix (see `TicketFixResolver` and the tests `A_ticket_mentioned_in_a_commit_is_only_an_explicit_reference` / `A_commit_message_mention_is_explicit_but_not_proof_of_a_fix`). The design doc (§6.18) separates mentions from development links. Reclassifying them as inferred would drop mention-only links from the default graph; ADO development links (with repository since B-11) would remain. Left unchanged on purpose. |
| M-16 | Add file-rename handling to the graph? | Needs a new relationship type (database migration + C# enum + Python projection), using `git diff -M` between the previous and current scanned commit. |

## Defect environment (PROD / UAT / TEST) — optional enrichment

Provider-neutral (`DefectEnvironmentClassifier`); Azure DevOps maps work items into it today, Jira issues
map the same way later. Configured per provider and project in `ADO_ENVIRONMENT_RULES_FILE`
(example: `config/environment-rules.example.json`).

- Each project lists its sources in **its own precedence order**: `field`, `labels` (optional prefix),
  `path`, `title` pattern, `description` pattern (patterns need a named group `env`).
- Trust: field/labels/path default to verified; title defaults to candidate (a project may declare its
  title convention verified); **description is always a candidate**.
- All sources are evaluated. Verified findings decide; a candidate never overrides them. Disagreeing
  sources in the deciding tier give **CONFLICT** with every finding preserved. An uninterpretable value
  from a source that claims to be an environment gives UNKNOWN (`unmapped_value`); plain labels and path
  segments that match nothing are ignored. No value gives UNKNOWN (`no_value`).
- Stored per current work item: `environment` (PROD/UAT/TEST/UNKNOWN/CONFLICT), `environmentStatus`
  (verified/candidate/unknown/conflict), `environmentRaw`, `environmentSource`, `environmentReason`,
  `environmentRulesVersion` and `environmentFindings` (every source's finding).
- Never blocks ingestion: invalid rules affect only their project (UNKNOWN `rules_invalid`) and are
  reported as warnings; malformed JSON disables classification with one warning.
- A PROD defect is **not** a confirmed production incident; no incident is inferred.

## CI build links (LRN-07)

`ci_commit`: a finished build -> the commit it built (same repository, exact 40-character SHA).
`ci_pull_request`: a finished build -> the pull request that triggered it (PR builds compile a temporary merge
commit that is not on the branch). In-progress builds are linked once they finish, so every edge cites the exact
stored build revision. A build result is not a test result.

## Test evidence levels (LRN-07)

A test node is **not** proof that a defect is covered. The graph keeps these levels separate, and no API
may present a weaker level as a stronger one:

| Level | Meaning | Source | Status |
|---|---|---|---|
| Structural | File F contains test method T | Code scan (`file_test` edge) | Implemented |
| Changed with fix (candidate) | T is in a file changed by a fix commit | Path: fix commit -> file -> test | Derivable; must be labelled a candidate, never "covers the defect" |
| Declared association | The defect is linked to an ADO Test Case ("Tested By") | ADO relation (`tested_by` edge) | Implemented; shown only while the defect's latest revision states it |
| Mapped automated test | The Test Case's "Automated test name" matches exactly one test method | ADO field + latest code scan (`automated_by` edge); zero or several matches are reported as unresolved | Implemented; shown only while the method exists in the latest scan |
| Test result | T passed or failed in a specific CI run | CI test results | Not ingested |

A declared association proves only that someone linked the test to the defect. It does not prove the
test passed, covers all affected code paths, or prevents recurrence.

## Needs live verification (manual testing)

| Item | What to check |
|---|---|
| M-8 | Revert a PR with Azure DevOps' **Revert** button in the sandbox and record the resulting commit/PR message. Only `git revert` messages ("This reverts commit <sha>") are detected today. |
| B-6 | ADO project membership via the team-members API, with a real member and a non-member. |

## Fixed after the first follow-up list

m-1 (graph ties prefer current evidence), m-6 (runtime/dev requirement split; Docker images install the
hash-verified `requirements-runtime.lock.txt`), m-10 (link evidence uses the source record's time).

## N-16: grammar gaps no longer freeze a whole repository (fixed)

tree-sitter-c-sharp 0.23.5 (the latest release) cannot parse some valid C#, e.g. a collection expression
starting with the identifier `field` (`[field]` is read as an attribute target). A file the grammar cannot
fully parse (or a Python file with a syntax error) is now `UNAVAILABLE` with a diagnostic naming the first
problem line: file-level evidence only, and the rest of the repository updates. Genuine parser crashes are
still `FAILED` and mark the scan PARTIAL (last good scan kept). Safety: grammar gaps in at least 3 files and
more than 20% of parsed files mark the scan PARTIAL. The scanner CLI lists every downgraded file.

## N-2 and N-7 (fixed)

N-2: a work item deleted in ADO is tombstoned (`deleted_at`, `deletion_reason`) and never served again, so it
also disappears from the graph. Found by reading each project's Recycle Bin on every work-item sync, and by
`workitem.deleted` webhooks that are confirmed by re-fetching (if ADO still returns the item, nothing is hidden).
An item ADO no longer returns is hidden even if the cause is lost access. A newer revision (e.g. a restore)
clears the tombstone; a deletion observed after a newer revision is refused. Raw archives are retained for
audit: purging them is a retention-policy decision.

N-7: content that changes A -> B -> back to A under the same source time becomes latest again when A is
re-observed later than the current latest was last confirmed (`latest_observed_at`); a stale observation never
wins, and numeric ADO revisions are unaffected.

## Accepted as is (documented, not changed)

| Item | Reason |
|---|---|
| M-4 | Round trips per row/hop. Measure on real data volumes before refactoring (batching would touch the materializer, traversal and Python graph persistence). |

## Pending CI change

A workflow update that runs the B-8/B-9/B-15 database tests and the FND-03 role-isolation test in the
fresh-PostgreSQL job is ready, but pushing `.github/workflows` needs a token with the **Workflows**
permission.
