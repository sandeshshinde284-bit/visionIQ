# DEF-02 — Propose a bounded fix

> Story: DEF-02 on the [Trello board](https://trello.com/b/AaucJawp/defect-intelligence) ·
> Gate G3 · Depends on DEF-01, BRF-02 · Design v1.11 §8.6.4 · Decision D04
>
> **Status:** implemented and tested with scripted model answers. No real model was used: the
> code tasks are routed to Claude only, and Claude is off until decision D04.
> 11 November 2026. Overall flow: [DEFECT-ARCHITECTURE.md](../DEFECT-ARCHITECTURE.md).

*As an engineer, I want a small fix in the right layer, so that I can review it quickly and trust
its scope.*

| Acceptance criterion | How it is met | Status |
| --- | --- | --- |
| Claude proposes the patch inside an isolated workspace. | `CODE_FIX` and `REPRODUCTION_TEST` are routed to Claude with no fallback. The patch is applied in a throwaway clone; the source repository is never written to. | Met (scripted model only) |
| Changes to security, payment, identity, schema or hardware areas, to secrets, or to dependency versions stop for engineer review. | `guardrails.check` returns `review` findings by path and for text that looks like a credential. The run stops as `NEEDS_REVIEW` with the diff; the change is not applied. | Met |
| Iterations are capped; when the budget runs out the result is FAIL or INCONCLUSIVE, never green. | `max_fix_iterations` per repository and the model budget (`DEFECT_MAX_*`). | Met |

## Rules a patch must pass

| Finding | Effect | Examples |
| --- | --- | --- |
| `block` | The proposal is rejected. The model is told the rule code and may try again. | A test file, a generated file, a path outside `writable_paths`, an existing file the model was not shown in full, an existing file that is not text, a path that is a directory or differs from an existing file only by case, unchanged content, a file over the size limit |
| `review` | The run stops for an engineer (`fix_needs_engineer_review`). | A restricted area (payment, security, identity, schema, hardware), a dependency file, secret-like text, a change broader than the repository's budget |

The areas, patterns and budgets are per repository in the fix policy
([config/defect-policy.example.json](../../../config/defect-policy.example.json)).

Note on S11-13: the card puts the iteration budget in .NET. It is in the Python pipeline, where the loop runs.

## Where the code is

| Task | File (under `src/VisionIQ.Intelligence/app/defect/`) |
| --- | --- |
| S11-05 Bounded patch proposal | `generate.py` (`propose_fix`), prompt `defect.fix@v1` |
| S11-06 Layer and restriction checks | `guardrails.py`, `policy.py` |
| S11-13 Fix iteration budget | `pipeline.py` |

## How to test

```powershell
python -m pytest tests/intelligence/test_defect_guardrails.py tests/intelligence/test_defect_hardening.py
python -m pytest tests/intelligence/test_defect_pipeline.py     # iteration cap, budget, stop for review
```

End to end, with the demo repository (step 2C.3 of [TESTING-KICKOFF-TODAY.md](../../TESTING-KICKOFF-TODAY.md)):

| Check | Expected |
| --- | --- |
| `Patch (iteration 2)` | Only `src/kiosk/retry_policy.py` |
| Gate `patch_policy` | `PASS`: "The change is inside the repository's policy." |
| `candidate.patch` in the evidence folder | The diff that was tested |
| Remove the `code_fix` answers from `offline-script.json` | `PARTIAL` / `INCONCLUSIVE` / `llm_unavailable`, never `VERIFIED` |

**Completion evidence** (the proposed diff with its policy check result): `candidate.patch` and the
`patch_policy` gate in `result.json`.

## Known limits

The review of PR #19 found gaps in the dependency and secret rules, a non-text file that could be
replaced unseen, and a directory path that crashed the run. All are fixed; see
[DEFECT-ARCHITECTURE.md](../DEFECT-ARCHITECTURE.md#review-of-pr-19-fixed-and-still-open).

Still open:

- The dependency patterns are defaults. A repository with other manifest files must add them to
  `dependency_files` in its policy.
- Thresholds and budgets are starting values; the owning team has not reviewed them.
- No real model was used: the code tasks need Claude and decision D04.
