# DEF-03 — Prove the fix works

> Story: DEF-03 on the [Trello board](https://trello.com/b/AaucJawp/defect-intelligence) ·
> Gate G3 · Depends on DEF-02 · Design v1.11 §8.6.2, §8.7 · Decision D10
>
> **Status:** implemented and tested on a synthetic repository with real test and benchmark runs.
> 11 November 2026. Overall flow: [DEFECT-ARCHITECTURE.md](../DEFECT-ARCHITECTURE.md).

*As an engineer, I want the same failing test to pass on the fix, with performance checked, so that
I know the fix works and doesn't slow anything down.*

| Acceptance criterion | How it is met | Status |
| --- | --- | --- |
| The same test passes on the candidate in the same pinned environment. | Same test id, recipe and runner. Each run gets a fresh export of exactly that commit. The result links the red execution (baseline SHA) and the green one (candidate SHA). | Met |
| The touched path is benchmarked with repeated paired runs; a noisy result is INCONCLUSIVE. | `performance.py`: alternating baseline and candidate pairs, warm-up pairs dropped, medians compared, spread as noise. | Met |
| Flaky results are rerun up to twice; the first failure is never erased. | `results.run_with_policy`: one pass among the reruns makes the test `FLAKY`; the first result stays in `first_outcome`. | Met |

## Outcomes

| Test outcome | Meaning |
| --- | --- |
| `PASS` | Every selected case passed |
| `FAIL_TARGET_SIGNATURE` / `FAIL_DIFFERENT_SIGNATURE` | Failed with, or without, the predicted failure |
| `FLAKY` | Runs of the same code disagreed. Never counted as a pass |
| `ENVIRONMENT_ERROR`, `TIMEOUT`, `NOT_RUN`, `UNAVAILABLE`, `INCONCLUSIVE` | No answer about the code |

| Performance status | Meaning |
| --- | --- |
| `PASS` | "No regression demonstrated under these conditions": median change under the threshold |
| `REGRESSION` | Slower than the threshold and by more than the noise. Verdict `FAIL` / `performance_regression` |
| `INCONCLUSIVE` | Noise over the limit. Verdict `INCONCLUSIVE` / `performance_inconclusive` |
| `NOT_MEASURED` | The repository has no benchmark for the changed path |

Note on S11-07: the card names .NET orchestration. The runs are orchestrated in Python, next to the runner.

## Where the code is

| Task | File (under `src/VisionIQ.Intelligence/app/defect/`) |
| --- | --- |
| S11-07 Execute candidate test | `pipeline.py` |
| S11-11 Performance baseline vs candidate | `performance.py` |
| S11-12 Flaky and environment failures | `results.py` (`run_with_policy`) |

## How to test

```powershell
python -m pytest tests/intelligence/test_defect_pipeline.py tests/intelligence/test_defect_results.py tests/intelligence/test_defect_hardening.py
```

End to end, with the demo repository (step 2C.3 of [TESTING-KICKOFF-TODAY.md](../../TESTING-KICKOFF-TODAY.md)):

| Check | Expected |
| --- | --- |
| Gate `candidate_test` | `PASS` |
| Gate `performance` | `PASS` with the median change, the number of pairs, the threshold and the noise |
| `result.json`, `executions` | A `reproduction` run on the baseline SHA and a `candidate` run on the candidate SHA for the same `test_id` |
| On a busy machine | `performance` may be `INCONCLUSIVE` and the exit code 3. Correct behaviour; run again |

**Completion evidence** (red-then-green logs for the same test, and the performance comparison):
`X1.log` (red), the candidate's log, and `performance` in `result.json`.

## Known limits

The review of PR #19 found that runs shared one working tree, that the exit code was ignored,
that a bug in the patch was reported as an environment error, and that a run could outlive its
lease. All are fixed; see
[DEFECT-ARCHITECTURE.md](../DEFECT-ARCHITECTURE.md#review-of-pr-19-fixed-and-still-open).

Still open:

- A run that uses up its time budget ends `INCONCLUSIVE` (or `BLOCKED` during reproduction) with
  the executions it did not start recorded as timed out.
- The performance thresholds are starting values (§8.7).
