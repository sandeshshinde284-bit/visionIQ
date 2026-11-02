# LRN-01 — Acceptance against the original story

**Authoritative story:** [Trello — Connect to the history sources](https://trello.com/c/Zg15086C/7-learn-lrn-01-connect-to-the-history-sources)

## Criteria

| Original criterion | Evidence | Status |
|---|---|---|
| Read work items, PRs, commits, changed files and CI runs for every approved project | Work items: 367 in Oct 1 local full sync. Initial selected `KIOSK_TVM` Git run: 64 commits and 12 PRs. **CI builds: 86, 149 links, 0 failure, exit 0 on 2026-11-02.** Changed-file metadata not yet independently inspected in live output. Scope only approved `Genoa Kiosk` and selected repo; CI build stream is project-scoped. | **PARTIAL — changed-file verification remains** |
| Inaccessible source marked NOT SYNCED, never empty | Invalid-PAT live failure and automated stream-isolation scenarios; explicit nonzero exit | **PASS** |
| Interrupted sync resumes at last checkpoint without duplicates | PostgreSQL checkpoints inspected, no-change replay, 30/30 fault/recovery regressions | **PASS** |

## Separate supporting evidence

- Live ADO comments retrieved and preserved in JSONL: work items 106675 **9/9**, 107328 **2/2**, 115804 **0/0**; no gap/truncation.
- GCS integrity checks: **5/5** sampled archived objects matched recorded SHA-256.
- PostgreSQL canonical evidence and checkpoints inspected; no duplicate item/revision key in tested sample.
- Builds run `00c39f65-5491-4b63-b78b-6ff5176d1f49`: **86 stored; 149 links; 0 not stored/quarantined; 112 build-link graph assertions; exit 0**.
- Recovery test run: **30 passed, 0 failed, 0 skipped, exit 0**.
- Detailed chronology: [LRN-01 test report](../../LRN-01-Test-Report.md).

## Remaining before signoff

1. Inspect changed files from a real selected `KIOSK_TVM` commit/PR against its ADO source and confirm filename/change-type fidelity; don't conflate link graph projection with code change inventory.
2. **Completed:** full solution regression on latest local `feature/learn`: 402 tests passed, 0 failed, 0 skipped; exit 0. Review 66 build warnings for actionable correctness/security issues.
3. Review live source scopes/pipeline filtering, required approvals, and any material security blockers.
4. Optional LRN-03 follow-up: independently validate newly collected comments in canonical archive for a source revision; local JSONL preservation alone doesn't establish archival backfill.

**Acceptance decision: NOT YET FULLY SIGNED OFF; changed-file source comparison remains.** No production-style scheduler validated. PR #14 must remain DRAFT until LRN-01–07 have been fully reviewed.
