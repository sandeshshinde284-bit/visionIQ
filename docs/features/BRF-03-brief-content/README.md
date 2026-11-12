# BRF-03 — Brief with risk areas, do / don't and best approach

> Story: [Trello BRF-03](https://trello.com/c/LDFiVOf9/23-brief-brf-03-brief-with-risk-areas-do-dont-and-best-approach) ·
> Gate G2 · Depends on LRN-09, BRF-02 · Design v1.11 §7.4, §7.5
>
> **Status:** implemented, unit-tested and run end to end locally with the offline model stand-in,
> on branch `feature/BRF-02-08-brief-pipeline` (not committed). 10 November 2026.
> Overall flow: [BRIEF-ARCHITECTURE.md](../BRIEF-ARCHITECTURE.md).

*As an engineer picking up a defect, I want a brief showing past fixes, risky areas and what to
avoid, so that I don't repeat mistakes others already made.*

| Acceptance criterion | How it is met |
| --- | --- |
| The brief lists risk areas, do / don't from past fixes and backfires, and a recommended approach. | Advice items have a kind: `RISK`, `DO`, `DONT` or `APPROACH`. A fix that was later reverted reaches the model as counterevidence, which is what a `DONT` is built from. The brief also lists the files likely affected. |
| Risk is shown as LOW, MODERATE, HIGH or INSUFFICIENT_DATA, with the factors behind it. | `risk.assess` returns the band, a one-sentence explanation and five factors, each with its count, time window and the ticket and PR ids behind it. |
| Missing data is never shown as low risk. | `LOW` needs recorded change history for the files in scope. No scope, or no recorded history, gives `INSUFFICIENT_DATA`. A factor with no data is marked `missing` with no value, never zero, and the ticket shows "no data". |

## The pipeline

`AnalysisWorker` claims an accepted request and runs `build_brief` (`app/brief/pipeline.py`):

| Step | What happens | If it falls short |
| --- | --- | --- |
| 1. Ticket | Read the ticket's indexed, redacted text under the run's `security_scope_id` | `BLOCKED` / `ticket_not_readable` |
| 2. Injection scan | Record instruction-like text in the ticket (BRF-02) | flagged, never obeyed |
| 3. Retrieval | LRN-09 hybrid search over the published snapshot; the ticket itself is excluded | too little detail: `PARTIAL` / `ticket_too_vague`; nothing found: `PARTIAL` / `no_history_found` |
| 4. Scope (S10-02) | Files named in the ticket, plus files changed by the fixes of the closest matches | no scope → band `INSUFFICIENT_DATA` |
| 5. Risk (S10-03, S10-13) | Count the factors and derive the band, with no model | missing history → `INSUFFICIENT_DATA` |
| 6. Draft (S10-05) | One model call through the gateway, with the evidence and the computed facts | `PARTIAL` with the gateway's reason |
| 7. Evidence check | Keep only advice its source supports ([BRF-04](../BRF-04-advice-evidence/README.md)) | none left: `PARTIAL` / `no_supported_advice` |
| 8. Second opinion | Only on conflicting evidence or a `HIGH` band (BRF-02) | skipped silently if not allowed |
| 9. State | `PUBLISHED` if advice rests on well-supported history; thin history: `PARTIAL` / `weak_evidence` | |

The worker then stores the brief, its advice, the model-call log and the `analysis.completed`
event in one transaction. A run whose pipeline keeps failing is retried with backoff (15 s
doubling to 4 minutes) and answered `BLOCKED` / `analysis_failed` on the sixth attempt, about
8 minutes later, so a short outage is ridden out and a run never stays unanswered.

### Scope prediction (S10-02)

| Basis | Confidence | Source |
| --- | --- | --- |
| `named_in_ticket` | exact | File names in the ticket text or stack trace |
| `changed_by_past_fix` | inferred | Files changed by the fixes of the closest past tickets, most often changed first |

Only close matches widen the scope: tickets whose fix touched the code the ticket names (a
code-path match), or, when there is none, the three results on which keyword and vector search
agree. A loosely related result would otherwise pull unrelated files, and their fixes, into the
risk count. The scope is capped at 12 files.

### Risk factors (S10-03)

All factors are plain counts from recorded history, limited to what the run's scope may read.

| Factor | What it counts | Window |
| --- | --- | --- |
| `recent_fixes` | Defect fixes that changed a file in scope | `BRIEF_RISK_WINDOW_DAYS` (90) |
| `reverted_fixes` | Of those, the ones later reverted | 90 days |
| `change_activity` | All recorded changes (fix or not) to those files | 90 days |
| `known_lessons` | Current or candidate lessons for those files (LRN-10) | none |
| `hotspot_areas` | Areas of the latest hotspot sweep, in the ticket's project, that contain a file in scope (BRF-08) | the sweep's |

A fix counts only if its link is explicit or inferred, not disputed and not retracted, and the
ticket it fixed is readable now. The window uses the fixed ticket's last source update.

### Risk band (S10-13)

| Band | Rule |
| --- | --- |
| `INSUFFICIENT_DATA` | No file in scope; or no change history recorded for the files in scope; or the counts would say `LOW` but part of the history could not be read (fixes whose ticket is outside the run's access scope, or a failed lesson search) |
| `HIGH` | 2 or more reverted fixes; or 5 or more fixes; or a revert with 3 or more fixes; or a `HOT` area with 2 or more fixes |
| `MODERATE` | 2 or more fixes; or 1 reverted fix; or a live lesson; or a flagged hotspot area |
| `LOW` | History is recorded, at most 1 fix, none reverted |

The thresholds are constants at the top of `app/brief/risk.py`. The band is an ordinal label, not a
probability: no percentage or likelihood appears anywhere in the brief.

### The brief document (S10-04)

`app/brief/schema.py` is the model; [contracts/briefs/brief.v1.schema.json](../../../contracts/briefs/brief.v1.schema.json)
is generated from it. Main parts: `ticket`, `state`, `reason_codes`, `predicted_scope`, `risk`
(`band`, `explanation`, `factors`), `advice`, `evidence`, `disagreements`, `limitations`,
`injection_flags`, `generation` (provider, model, prompt version, routing revision, tokens) and
`timings`.

## Code map

| Task | Where (`src/VisionIQ.Intelligence/app/`) |
| --- | --- |
| S10-02 scope prediction | `brief/risk.py` (`predict_scope`) |
| S10-03 deterministic risk factors | `brief/history.py` (SQL), `brief/risk.py` (`assess`) |
| S10-04 structured brief schema | `brief/schema.py`, `contracts/briefs/brief.v1.schema.json` |
| S10-05 generate defect brief | `brief/pipeline.py`, `llm/prompts.py` (`brief.defect@v1`) |
| S10-13 explainable risk band | `brief/risk.py` |
| Request intake, worker, storage | `events/processing.py`, `brief/worker.py`, `main.py` |
| Schema | `knowledge.analysis_requests` (now a queue), `knowledge.briefs`, `knowledge.brief_advice` |

## How to test it

```powershell
python -m pytest tests/intelligence/test_brief_pipeline.py tests/intelligence/test_brief_risk.py tests/intelligence/test_brief_worker.py
```

50 tests: a full brief from in-memory history; each `BLOCKED` and `PARTIAL` path; every band
threshold; missing history never `LOW`; factor references; scope rules; the request handler
(queued, or answered `BLOCKED` at once); one transaction per answered run; retries; and the
committed JSON schema matching the model.

Database test (opt-in, disposable PostgreSQL after `python -m db.manage all`):

```powershell
$env:VISIONIQ_DB_INTEGRATION = '1'
python -m pytest tests/intelligence/test_brief_pipeline_db.py
```

It accepts a request, runs the worker and checks the stored brief: `PUBLISHED`, band `MODERATE`
from two fixes with one reverted, advice citing real ticket links, a brief that cannot be updated
or deleted, and a ticket outside the run's scope answered `BLOCKED` without any model call.

For the completion evidence (the brief for a live defect with its band and factors), follow the
[local walkthrough](../BRIEF-ARCHITECTURE.md#how-to-test-the-whole-flow-locally), steps 1 to 4.

## Open items

- The ticket must already be in the published knowledge snapshot. A ticket picked up before the
  sync and index have seen it is answered `BLOCKED` / `ticket_not_readable`, and nothing re-runs it
  later.
- Before the first hotspot sweep the hotspot factor shows "no data", and the band is decided by
  the other factors.
- The risk thresholds are an initial rule (§7.5 asks for an ADR and a labelled hold-out set).
- Files are matched by base name, as in LRN-09, so two files with the same name in different
  folders are counted together.
- Meaningful test-coverage gaps and ownership concentration (§7.5) are not factors yet.
- Enhancement tickets get the same defect-style brief.
- Design §7.4 also lists hypotheses with a next check. The brief has advice, evidence and risk;
  root-cause hypotheses belong to the DEFECT phase.
