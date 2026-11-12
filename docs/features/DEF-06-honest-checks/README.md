# DEF-06 — Native CI runs on the PR, honestly

> Story: DEF-06 on the [Trello board](https://trello.com/b/AaucJawp/defect-intelligence) ·
> Gate G3 · Depends on DEF-05 · Design v1.11 §8.6.1, §11.1.1
>
> **Status:** the gate rules are implemented and tested on both sides. Reading the pipeline's
> result back is not built, and nothing was run against Azure DevOps. 11 November 2026.
> Overall flow: [DEFECT-ARCHITECTURE.md](../DEFECT-ARCHITECTURE.md).

*As a reviewer, I want the normal pipeline and branch policy to run on the PR, with honest results,
so that green means green.*

| Acceptance criterion | How it is met | Status |
| --- | --- | --- |
| CI runs on the PR after it exists; results are published as PR checks. | The repository's own pipeline runs because it is an ordinary pull request. VisionIQ posts its sandbox results as PR statuses (genre `visioniq`). | Partly: the pipeline's result is not read back, so `native_ci` stays `NOT_RUN` |
| A required check that is failing, NOT_RUN or INCONCLUSIVE is never shown as green. | `gates.summarize` (Python) and `FixGates.Summarize` (.NET): `GREEN` only if every required gate is `PASS`. A status is `succeeded` only for `PASS` measured on the current head. A missing gate and a pass that names no commit are `pending`; of two results under one name the worse is shown. | Met |
| A new commit on the PR invalidates earlier results. | Each gate keeps the commit it was measured on. On a "pull request updated" delivery, the head is read from Azure DevOps and, if it is new, every status is posted again as `pending`. | Partly: depends on the service hook being delivered |

## Gates

| Gate | Check name | Posted by VisionIQ |
| --- | --- | --- |
| `reproduction` | `visioniq/reproduction` | Yes |
| `candidate_test` | `visioniq/fix-test` | Yes |
| `historical_regression` | `visioniq/historical-regression` | Yes |
| `touched_suite` | `visioniq/touched-suite` | Yes |
| `performance` | `visioniq/performance` | Yes |
| `patch_policy` | `visioniq/patch-policy` | Yes |
| `native_ci` | none | No: the platform's own checks show it |
| `human_review` | none | No: the engineer's vote shows it |

| Gate status | PR status |
| --- | --- |
| `PASS` | `succeeded` |
| `FAIL` | `failed` |
| `NOT_APPLICABLE` | `notApplicable` |
| Anything else (`NOT_RUN`, `INCONCLUSIVE`, `UNAVAILABLE`, `REVIEW_REQUIRED`, stale) | `pending` |

Overall: `GREEN` (every required gate `PASS`), `RED` (a required gate `FAIL`), otherwise `NOT_GREEN`.
A verified fix is `NOT_GREEN` until native CI has passed and an engineer has approved.

## Where the code is

| Task | File |
| --- | --- |
| S11-10 Execute touched-project suite | Azure Pipelines on the repository: not part of this code |
| S11-17 Invalidate stale checks | `Features/Fixes/AdoPullRequestHook.cs`, `FixPullRequestHeadWatcher` in `FixPullRequestPublisher.cs` |
| S15-12 Test quality gate truthfulness | `FixGates.cs` + `FixGatesTests.cs`; `app/defect/gates.py` + `test_defect_gates.py` |

The service hook "Pull request updated" would go to the same target as the work-item hooks
(`POST /webhooks/ado`, same secret). **Do not create it on the real organization** (read-only rule).

## How to test

```powershell
python -m pytest tests/intelligence/test_defect_gates.py
dotnet test tests/VisionIQ.Api.Tests --filter "FullyQualifiedName~FixGatesTests|FullyQualifiedName~FixPullRequestTests"
```

| Check | Expected |
| --- | --- |
| Demo run, `Gates:` line | `NOT_GREEN`; `native_ci` is `NOT_RUN`, `human_review` is `REVIEW_REQUIRED` |
| `FixGatesTests`: every non-PASS status on every required gate, with CI green and an approval | Never `Green`, never `succeeded` |
| `FixPullRequestTests`: a "pull request updated" body with a new head | Every status posted again as `pending`; back to the verified head, the results apply again |

**Completion evidence** (green required checks on the live defect PR, and a forced NOT_RUN check
shown as not green): the first half is **blocked by the read-only rule**; the second half is the
test above.

## Known limits

The review of PR #19 found that the head commit was taken from the webhook body, that a late
delivery could restore green, and that gates without a commit, missing gates and duplicate gates
gave wrong statuses. All are fixed; see
[DEFECT-ARCHITECTURE.md](../DEFECT-ARCHITECTURE.md#review-of-pr-19-fixed-and-still-open).

Still open:

- Statuses are posted without an iteration, so in Azure DevOps they stay on the pull request
  after a new commit until the hook is processed. A missing subscription or a lost delivery
  leaves a green status on new content.
- Native CI is not read back, so `native_ci` stays `NOT_RUN` in VisionIQ's own summary.
- The hook makes its Azure DevOps calls on the request thread.
- Nothing was run against Azure DevOps.
