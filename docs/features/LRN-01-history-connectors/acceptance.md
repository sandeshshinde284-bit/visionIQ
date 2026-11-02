
## Changed-file fidelity — PASSED (2026-11-02)

User compared live Azure DevOps Git commit `6f13cdd9f97eb57ecb0c60f9bc40724cee8bb6a9` in approved `Genoa Kiosk / KIOSK_TVM` against VisionIQ's local JSONL export (`artifacts/ingestion/lrn01-changed-files/20261102-122630/commits.jsonl`). **VisionIQFiles=8, AzureDevOpsFiles=8, Differences=0, Truncated=False, Result=PASS**. Comparison matched both changed-file paths and change types, without publishing filenames or source contents. Export contained 39 commits, exit 0. This is one sampled live commit, not exhaustive verification of every commit. Combined with 402/402 full solution tests, all three written LRN-01 acceptance criteria now have supporting evidence; 66 build warnings still require severity review. PR #14 remains Draft pending LRN-01–07 acceptance.

# LRN-01 — Acceptance against the original story

**Authoritative story:** [Trello — Connect to the history sources](https://trello.com/c/Zg15086C/7-learn-lrn-01-connect-to-the-history-sources)

## Criteria

| Original criterion | Evidence | Status |
|---|---|---|
| Read work items, PRs, commits, changed files and CI runs for every approved project | Work items: 367 in Oct 1 local full sync. Initial selected `KIOSK_TVM` Git run: 64 commits and 12 PRs. **CI builds: 86, 149 links, 0 failure, exit 0 on 2026-11-02.** Sampled commit `6f13cdd9...` verified 8/8 paths/types against ADO, zero differences, not truncated. Scope only approved `Genoa Kiosk` and selected repo; CI build stream is project-scoped. | **PASS — sampled changed-file fidelity** |
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

1. **Completed:** selected `KIOSK_TVM` commit `6f13cdd9...`: 8/8 changed paths/types exactly match live ADO; 0 differences; no truncation.
2. **Completed:** full solution regression on latest local `feature/learn`: 402 tests passed, 0 failed, 0 skipped; exit 0. Review 66 build warnings for actionable correctness/security issues.
3. Review live source scopes/pipeline filtering, required approvals, and any material security blockers.
4. Optional LRN-03 follow-up: independently validate newly collected comments in canonical archive for a source revision; local JSONL preservation alone doesn't establish archival backfill.

**Acceptance decision: all original functional criteria evidenced; pending warning severity review / formal signoff.** No production-style scheduler validated. PR #14 must remain DRAFT until LRN-01–07 have been fully reviewed.
