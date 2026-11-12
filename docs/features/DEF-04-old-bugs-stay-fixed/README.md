# DEF-04 — Old bugs don't come back

> Story: DEF-04 on the [Trello board](https://trello.com/b/AaucJawp/defect-intelligence) ·
> Gate G4 · Depends on DEF-03, LRN-04 · Design v1.11 §1.2, §4.5, §4.5.1
>
> **Status:** implemented and tested on a synthetic repository. The history query runs on
> PostgreSQL but was tried only against a scope with no history. 11 November 2026.
> Overall flow: [DEFECT-ARCHITECTURE.md](../DEFECT-ARCHITECTURE.md).

*As an engineer, I want past regression tests from this area re-run on the fix, so that I don't
reopen a bug someone already fixed.*

| Acceptance criterion | How it is met | Status |
| --- | --- | --- |
| Historical regression tests are found through the ticket → PR → test links. | `sources.HISTORICAL_FIXES_SQL` finds past fixes of readable tickets that changed the same files (matched by full path) and were not reverted. The test files those fixes changed are run. | Met; tier 1 links are not used |
| An unsafe fix that passes the new test but fails an old one is caught before the PR. | The old tests run on every candidate. One that fails is run on the baseline too: passing there and failing on the candidate is a regression, and the model gets another iteration. | Met |
| If no runnable old test exists, the brief and PR say so. | The regression result is `NOT_FOUND`, the gate is `UNAVAILABLE`, and the PR description shows it. | Partly: the brief is not changed |

## How old tests are chosen

| Tier | Source | Label in the result |
| --- | --- | --- |
| 1 | Declared Test Case links (`evidence.tested_by_links`) | Not used yet: an ADO automated test name does not map to a file a recipe can run |
| 2 | Test files changed by past fixes of the same files (LRN-04 ticket-fix links) | `historical` |
| 3 | Test files found by name next to the changed files | `inferred` |

Note on S11-08 and S11-09: the cards put these in .NET. They are in Python, next to the runner.

## Where the code is

| Task | File (under `src/VisionIQ.Intelligence/app/defect/`) |
| --- | --- |
| S11-08 Identify historical regression tests | `sources.py`, `regression.py` |
| S11-09 Execute historical regression replay | `pipeline.py` |

## How to test

```powershell
python -m pytest tests/intelligence/test_defect_pipeline.py
```

End to end, with the demo repository (step 2C.3 of [TESTING-KICKOFF-TODAY.md](../../TESTING-KICKOFF-TODAY.md)).
The demo's first prepared fix is the unsafe one: it fixes the reported bug and brings back old bug #101.

| Check | Expected |
| --- | --- |
| `Attempts`, patch 1 | `regression_failed`: `tests/test_retry_policy.py` passes on the baseline and fails on the candidate |
| `Attempts`, patch 2 | `candidate_accepted` |
| Gate `historical_regression` | `PASS`: "1 test(s) passed on the candidate: 1 linked to past fixes, 0 found by name only." |

**Completion evidence** (an unsafe candidate failing a recovered old test, or a stated absence):
the `patch 1` attempt above.

## Known limits

The review of PR #19 found that past fixes were matched by file name across repositories and that
tests left out by the limit were not mentioned. Both are fixed; see
[DEFECT-ARCHITECTURE.md](../DEFECT-ARCHITECTURE.md#review-of-pr-19-fixed-and-still-open).

Still open:

- Tests found by name only (`inferred`) still give a `PASS` on the gate. The detail and the
  limitations say that none is linked to a past fix.
- Declared Test Case links (tier 1) are not used.
- The history query was not run against real history. Two repositories with the same full path
  for a file would still be mixed: the query has no repository filter, because the policy's
  repository id and the id in the evidence tables are not known to be the same value.
- The brief is not changed when no old test exists; only the fix result and the PR say so.
