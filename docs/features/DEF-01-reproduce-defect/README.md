# DEF-01 — Reproduce the defect before fixing it

> Story: DEF-01 on the [Trello board](https://trello.com/b/AaucJawp/defect-intelligence) ·
> Gate G3 · Depends on BRF-03, FND-04 · Design v1.11 §8.6.2 · Decision D03
>
> **Status:** implemented and tested with the local runner on a synthetic repository. The Docker
> runner was started once by hand against a local image; no real repository has run in it.
> 11 November 2026. Overall flow: [DEFECT-ARCHITECTURE.md](../DEFECT-ARCHITECTURE.md).

*As an engineer, I want VisionIQ to show a test failing on the current code for the reported
reason, so that I know the fix targets the real bug.*

| Acceptance criterion | How it is met | Status |
| --- | --- | --- |
| The baseline commit and runner image are pinned before any test runs. | `workspace.prepare` accepts only a full commit SHA that exists in the repository. A Docker runner needs an image by digest. The execution manifest (`M1.json`) is written before the first run. | Met |
| The runner has no secrets and restricted network access. | Commands get an environment built from an allowlist. The Docker runner uses `--network none` and drops all capabilities. Every command runs in an export of one commit with no Git directory. The local runner has no network isolation, says so in the result and must be allowed with `DEFECT_ALLOW_LOCAL_RUNNER=true`. | Met for Docker; the local runner is for a developer machine only |
| The test fails with the target failure signature; anything else is reported as UNAVAILABLE, FLAKY or ENVIRONMENT_ERROR and escalated. | The model predicts the failure before the test runs. `results.classify` reads the JUnit report. Only the predicted failure, seen twice, is `REPRODUCED`. The match is on the failure's headline, not on the traceback. Everything else ends the run `BLOCKED` with a reason code. | Met |

## Where the code is

| Task | File (under `src/VisionIQ.Intelligence/app/defect/`) |
| --- | --- |
| S11-01 Fix request and response contracts | `schema.py`, `contracts/fixes/*.schema.json` |
| S11-02 Safe workspace preparation | `workspace.py`, `runner.py`, `policy.py` |
| S11-03 Defect reproduction check | `results.py` |
| S11-04 Generate baseline failing test | `generate.py` (`propose_test`), prompt `defect.reproduction@v1` |

## Reason codes

`reproduction_unavailable`, `reproduction_not_red`, `reproduction_different_failure`,
`reproduction_flaky`, `reproduction_environment_error`, `reproduction_test_not_run`,
`reproduction_timeout`, `reproduction_test_invalid`, `baseline_not_pinned`, `baseline_not_found`,
`repository_not_configured`, `repository_unavailable`, `runner_not_allowed`,
`ticket_not_readable`, `scope_not_resolved`. All end the run `BLOCKED`; no fix is attempted.

## How to test

```powershell
python -m pytest tests/intelligence/test_defect_sandbox.py tests/intelligence/test_defect_results.py tests/intelligence/test_defect_worker.py tests/intelligence/test_defect_hardening.py
```

End to end, with the demo repository (see [TESTING-KICKOFF-TODAY.md](../../TESTING-KICKOFF-TODAY.md), step 2C.3):

| Check | Expected |
| --- | --- |
| `Reproduction` line | `REPRODUCED`, failure `AssertionError: captured payment was retried` |
| `M1.json` in the evidence folder | Baseline SHA, runner, tool versions, commands; older than `X1.log` |
| `X1.log`, `X2.log` | The red run and its confirmation, both on the baseline commit |
| `--base-sha main` | `BLOCKED` / `baseline_not_pinned` |
| A wrong `message_contains` in the prepared answer | `BLOCKED` / `reproduction_different_failure` |

**Completion evidence** (the execution manifest and a red test log on the live defect): available
for the demo defect only. A live defect needs decision D03 and a real repository.

## Known limits

The review of PR #19 found a sandbox escape through the mounted `.git` directory, unbounded
output, a timeout that left the container running, a failure match on the whole traceback, a
local runner that was refused only in production, and requests left pending when the pipeline is
switched off. All of these are fixed; see
[DEFECT-ARCHITECTURE.md](../DEFECT-ARCHITECTURE.md#review-of-pr-19-fixed-and-still-open).

Still open:

- A test can write its own report. The exit code must agree with it, but a test that forges the
  report and exits with 0 in its own process is not detected.
- The Docker runner has no automated test with a real container, and no real repository's suite
  has run in it.
- A live defect needs decision D03 and a real repository.
