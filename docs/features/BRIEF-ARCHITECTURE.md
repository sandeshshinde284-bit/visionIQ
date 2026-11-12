# BRIEF phase — architecture (BRF-01 to BRF-08)

> Design v1.11 §3.4, §3.5, §7.4, §7.4.1, §7.5, §8.3, §8.10, §8.11, §10.9, §11.1.1 ·
> Branch `feature/BRF-02-08-brief-pipeline` (not committed) · 10 November 2026
>
> **Status:** BRF-01 is merged. BRF-02 to BRF-08 are implemented and tested locally, end to end,
> with an offline stand-in for the models. Not done yet: a run against real Gemini or Claude, and
> posting to a real Azure DevOps ticket (see [Open items](#open-items)).

When an engineer moves a ticket to In Progress, VisionIQ writes a brief on that ticket within
seconds: which files are probably involved, how risky the area is and why, and what past fixes say
to do and not to do. Every piece of advice links to the ticket, PR or commit it came from, and the
engineer can rate each piece by replying on the ticket.

| Story | What it adds | Feature doc |
| --- | --- | --- |
| BRF-01 | Ticket pickup: webhook, rules, missed-webhook sweep | [BRF-01](BRF-01-ticket-pickup/README.md) |
| BRF-02 | One controlled way to call Gemini or Claude | [BRF-02](BRF-02-llm-gateway/README.md) |
| BRF-03 | The brief: scope, risk factors and band, advice | [BRF-03](BRF-03-brief-content/README.md) |
| BRF-04 | Every advice item is backed by its source and has its own id | [BRF-04](BRF-04-advice-evidence/README.md) |
| BRF-05 | The brief is posted as one comment on the ticket | [BRF-05](BRF-05-brief-on-ticket/README.md) |
| BRF-06 | Engineers rate advice inside Azure DevOps | [BRF-06](BRF-06-advice-ratings/README.md) |
| BRF-07 | Pickup-to-brief time is recorded for every run | [BRF-07](BRF-07-time-to-brief/README.md) |
| BRF-08 | Activity-adjusted hotspot sweep | [BRF-08](BRF-08-hotspot-sweep/README.md) |

## The whole flow

```
 Azure DevOps                  VisionIQ.Api (.NET, workflow schema)                Intelligence (Python, knowledge schema)
 ────────────                  ────────────────────────────────────                ───────────────────────────────────────
 Bug #4711: New → In Progress
   │ service hook "work item updated"
   ▼
 POST /webhooks/ado ─► BRF-01: secret, rules, store pickup, 202
                       PickupWorker ─► analysis_runs + outbox
                                         │ analysis.requested (Pub/Sub, FND-05)
                                         └────────────────────────────────────► POST /events/pubsub
                                                                                 inbox + analysis_requests (status pending), 204
                                                                                        │
                                                                                 AnalysisWorker (BRF-03) claims the request
                                                                                   1. load the ticket text (access scope of the run)
                                                                                   2. flag instructions hidden in it          (BRF-02)
                                                                                   3. retrieve history: LRN-09 hybrid search
                                                                                   4. predict scope, count risk factors, band (BRF-03)
                                                                                   5. model drafts advice through the gateway (BRF-02)
                                                                                   6. keep only advice its source supports    (BRF-04)
                                                                                   7. optional second opinion                 (BRF-02)
                                                                                 one transaction:
                                                                                   briefs + brief_advice + llm_calls
                                                                                   analysis_requests = completed
                                                                                   outbox: analysis.completed (ids only)
                                         ┌───────────────────────────────────────────────┘
 POST /events/pubsub ◄───────────────────┘
   one transaction: event_inbox + analysis_results + run completed
                    + brief_publications (pending, pickup time)        (BRF-05)
 BriefPublishWorker ─► reads knowledge.briefs by result id
                       renders the HTML comment
   ◄── adds ONE comment ── mode ado: POST …/workItems/4711/comments (ADO_COMMENT_PAT)
                       mode record: keeps the comment in brief_publications, calls nothing
                       published_at, time_to_brief_ms                  (BRF-07)

 Engineer replies on the ticket: "ADV-001 accepted"
   │ service hook "work item commented"
   ▼
 POST /webhooks/ado ─► BRF-06: parse the lines, find the brief published on the ticket,
                       append to advice_feedback (advice id, who, when). Never starts a run.

 On a schedule or on demand: python -m app.hotspots.cli                 (BRF-08)
   reads recorded changes and defect fixes → knowledge.hotspot_sweeps / hotspot_areas
   the next brief shows a flagged area as a risk factor
```

Three design choices run through all of it.

- **Code decides, the model words.** Which evidence exists, who may read it, what the risk band is
  and whether an advice item is supported are all computed by code. A model only writes the advice
  sentences, and only from the evidence it was given.
- **Nothing is invented to fill a gap.** Every way of falling short has a state and a reason code.
  A brief with no supported advice says so and still shows the evidence and risk it found.
- **Each service writes only its own schema** (FND-03). Intelligence stores the brief; the API reads
  it by id and owns delivery and ratings. They talk only through the FND-05 events.

## States and reason codes

`analysis.completed` carries the state and the reason codes; the ticket comment explains them.

| State | Meaning | Reason codes |
| --- | --- | --- |
| `PUBLISHED` | Grounded advice on well-supported history | none |
| `PARTIAL` | Evidence and risk are shown; advice is missing or rests on thin history | `ticket_too_vague`, `no_history_found`, `weak_evidence`, `no_supported_advice`, `llm_budget_exhausted`, `llm_no_allowed_provider`, `llm_refused`, `llm_unavailable` |
| `BLOCKED` | Nothing useful can be said | `ticket_not_readable`, `analysis_failed`, `analysis_pipeline_disabled`, `deep_analysis_not_implemented` |

`ticket_not_readable` means the ticket is not in the published knowledge snapshot under the run's
access scope. That happens when the ticket has not been synced and indexed yet.

## Data model (migration `20261110_0023`)

| Table | Owner | Holds |
| --- | --- | --- |
| `knowledge.analysis_requests` (changed) | Intelligence | Now a queue: `status` pending or completed, the accepted event, lease and attempt count |
| `knowledge.briefs` | Intelligence | One immutable brief per run: the full document (JSON), state, risk band, stage timings |
| `knowledge.brief_advice` | Intelligence | One row per advice item: id, kind, statement, cited evidence ids |
| `knowledge.llm_calls` | Intelligence | The model calls behind each stored brief: provider, model, prompt version, routing revision, tokens, status. No prompt or response text. Calls of an abandoned attempt are in the service log only |
| `knowledge.hotspot_sweeps`, `hotspot_areas` | Intelligence | Each sweep and its scored areas with the fixes behind them |
| `workflow.brief_publications` | API | One row per run: status, mode, comment id, pickup time, published time, time to brief |
| `workflow.advice_feedback` | API | Append-only ratings: advice id, disposition, note, reviewer, when |

Briefs, advice and ratings cannot be updated or deleted (database triggers). A correction is a new
run; a changed mind is a new rating row.

The brief document's schema is [contracts/briefs/brief.v1.schema.json](../../contracts/briefs/brief.v1.schema.json).
It is generated from the Python model (`python -m app.brief.schema`) and a test fails if the two
differ. The .NET tests validate their sample brief against the same file.

## Code map

| Area | Where |
| --- | --- |
| Model gateway (BRF-02) | `src/VisionIQ.Intelligence/app/llm/`: `types.py`, `routing.py`, `gateway.py`, `prompts.py`, `injection.py`, `tools.py`, `consistency.py`, `evaluation.py`, `factory.py`, `providers/` |
| Brief pipeline (BRF-03, BRF-04) | `app/brief/`: `pipeline.py`, `history.py`, `risk.py`, `validate.py`, `schema.py`, `worker.py` |
| Request intake | `app/events/processing.py` (`handle_analysis_requested`), `app/main.py` |
| Time to brief (BRF-07) | `app/brief/latency.py`; recorded in `BriefPublicationStore.cs` |
| Hotspots (BRF-08) | `app/hotspots/`: `scoring.py`, `sweep.py`, `cli.py` |
| Delivery (BRF-05) | `src/VisionIQ.Api/Features/Briefs/`: `BriefPublisher.cs`, `BriefCommentRenderer.cs`, `BriefCommentPublishers.cs`, `BriefPublicationStore.cs`, `BriefPublishOptions.cs`, `BriefDocument.cs` |
| Ratings (BRF-06) | `src/VisionIQ.Api/Features/Feedback/`: `AdviceFeedback.cs`, `AdviceFeedbackEndpoint.cs`, `AdviceFeedbackStore.cs`; routed from `Features/Pickup/PickupWebhookEndpoints.cs` |
| Schema | `db/migrations/versions/20261110_0023_brief_pipeline.py` |
| Local demo data | `scripts/brief-demo/seed_demo_history.py` |

## Configuration

Everything is off by default. With nothing set, the services behave as before this branch: a
request is answered `BLOCKED` / `analysis_pipeline_disabled` and nothing is posted.

| Variable | Service | Default | Meaning |
| --- | --- | --- | --- |
| `BRIEF_PIPELINE_ENABLED` | Intelligence | `false` | Run the analysis worker |
| `LLM_MODE` | Intelligence | `live` | `offline` replaces every model with a deterministic stand-in. Refused when `APP_ENV=production` |
| `EMBEDDING_PROVIDER` | Intelligence | `vertex` | `test` for a local run without Google Cloud (must match how the knowledge base was indexed) |
| `GCP_PROJECT_ID`, `VERTEX_LOCATION`, `GEMINI_MODEL` | Intelligence | none, `europe-west1`, `gemini-2.5-flash` | Gemini on Vertex AI |
| `CLAUDE_ROUTE` | Intelligence | `off` | `vertex` or `anthropic` (decision D04) |
| `LLM_CLAUDE_MAX_CLASSIFICATION` | Intelligence | none | Highest data class Claude may receive: `PUBLIC`, `INTERNAL` or `RESTRICTED`. Unset means Claude gets nothing |
| `LLM_DATA_CLASSIFICATION` | Intelligence | `INTERNAL` | The class of ticket and evidence text |
| `LLM_MAX_CALLS_PER_RUN`, `LLM_MAX_TOKENS_PER_RUN`, `LLM_MAX_SECONDS_PER_RUN` | Intelligence | `3`, `60000`, `90` | Budget of one run |
| `BRIEF_RISK_WINDOW_DAYS`, `BRIEF_EVIDENCE_LIMIT` | Intelligence | `90`, `8` | Window of the risk factors; evidence items per brief |
| `HOTSPOT_SWEEP_INTERVAL_HOURS`, `HOTSPOT_WINDOW_DAYS`, `HOTSPOT_AREA_DEPTH` | Intelligence | `0`, `180`, `2` | In-process sweep schedule (0 = off), window, directory depth of an area |
| `PICKUP_ALLOWED_WORK_ITEM_IDS` | API | none | Pilot switch: comma-separated work-item ids. When set, no other work item gets a run, a brief or a comment |
| `BRIEF_PUBLISH_MODE` | API | `off` | `record` or `ado` |
| `ADO_COMMENT_PAT` | API | none | Required for `ado`: the bot's token, Work Items (Read & write) only. Must differ from `ADO_READ_PAT` |

More settings and their limits are in `app/config.py` and `BriefPublishOptions.cs`.

## How to test the whole flow locally

This needs Docker only: no Azure DevOps, no Confluence, no Google Cloud. The models are replaced by
the offline stand-in and the comment is kept in the database instead of being posted.

1. Add to `.env`:
   ```
   PICKUP_POLICY_FILE=/app/config/pickup-policy.example.json
   PICKUP_WEBHOOK_SECRET=<at least 24 random characters>
   PICKUP_SWEEP_INTERVAL_MINUTES=0
   BRIEF_PIPELINE_ENABLED=true
   LLM_MODE=offline
   EMBEDDING_PROVIDER=test
   BRIEF_PUBLISH_MODE=record
   ```
2. Start the stack and seed a small synthetic history (eight past bugs with their fixes, one of
   them reverted, plus the live ticket #4711). The seed script only accepts a local database.
   ```powershell
   docker compose up --build -d
   python scripts/brief-demo/seed_demo_history.py
   ```
3. Pick up the ticket. The sample is "Bug #4711 moved New → In Progress".
   ```powershell
   $secret = '<same secret>'
   $auth = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("ado:$secret"))
   $body = Get-Content docs/features/BRF-01-ticket-pickup/sample-workitem-updated.json -Raw
   Invoke-RestMethod -Method Post http://127.0.0.1:5100/webhooks/ado -Headers @{ Authorization = $auth } -ContentType application/json -Body $body
   # → status: queued. Post it again → status: duplicate
   ```
4. A few seconds later, read the result and the comment that would be on the ticket.
   ```powershell
   docker compose exec postgres psql -U visioniq -d visioniq -c "SELECT r.ticket_id, res.result_state, res.reason_codes, res.advice_ids, p.status, p.mode, p.time_to_brief_ms FROM workflow.analysis_runs r JOIN workflow.analysis_results res USING (run_id) JOIN workflow.brief_publications p USING (run_id)"
   docker compose exec postgres psql -U visioniq -d visioniq -At -c "SELECT comment_body FROM workflow.brief_publications ORDER BY created_at DESC LIMIT 1" > brief.html
   start brief.html
   ```
   Expect `PUBLISHED`, risk **Moderate** (two fixes touched `RetryPolicy.cs`, one was reverted),
   advice `ADV-001`… each with a link, and a "Don't" for the reverted fix.
5. See the call log (BRF-02) and the stored brief.
   ```powershell
   docker compose exec postgres psql -U visioniq -d visioniq -c "SELECT task, role, provider, model_id, prompt_template_version, status, input_tokens, output_tokens FROM knowledge.llm_calls"
   docker compose logs intelligence | Select-String llm_call
   ```
6. Rate the advice (BRF-06). The sample comment says `ADV-001 accepted`, `ADV-002 rejected: …`,
   `ADV-003 n/a`.
   ```powershell
   $comment = Get-Content docs/features/BRF-06-advice-ratings/sample-workitem-commented.json -Raw
   Invoke-RestMethod -Method Post http://127.0.0.1:5100/webhooks/ado -Headers @{ Authorization = $auth } -ContentType application/json -Body $comment
   # → status: recorded. Post it again → status: duplicate
   docker compose exec postgres psql -U visioniq -d visioniq -c "SELECT advice_id, disposition, note, reviewer_id, rated_at FROM workflow.advice_feedback ORDER BY advice_id"
   ```
7. Run a hotspot sweep (BRF-08) and the latency report (BRF-07).
   ```powershell
   docker compose exec intelligence python -m app.hotspots.cli
   docker compose exec intelligence python -m app.brief.latency
   ```
   The sweep flags `src/Payments` as `HOT`. A ticket picked up after the sweep shows that area as a
   risk factor and its band rises to **High**. To see it, post the sample again with a new `"id"`
   and `"rev"` / `"newValue"` 13.
8. Try the failure paths:
   - Skip step 2 → `BLOCKED` / `ticket_not_readable`, and the comment says why.
   - Set `LLM_MODE=live` with no `GCP_PROJECT_ID` → `PARTIAL` / `llm_no_allowed_provider`: the
     evidence and risk are shown, with no advice.
   - Set `BRIEF_PUBLISH_MODE=off` → the result is stored and nothing is queued for the ticket.

This walkthrough was run on 10 November 2026 in an isolated Compose project. Results: `queued` then
`duplicate`; one `PUBLISHED` brief; three ratings recorded, then `duplicate`; `src/Payments` flagged
`HOT` (6 fixes in 6 changes, lift 1.72); the second pickup's band `HIGH`; time to brief 6.8 s and
4.2 s.

### Automated tests

```powershell
python -m pytest tests/intelligence                       # 364 passed, 90 skipped
dotnet test VisionIQ.sln --configuration Release          # 659 passed, 20 skipped
python -m ruff check . ; python -m ruff format --check .  # clean
```

The skips are opt-in database tests. With a disposable PostgreSQL (after `python -m db.manage all`):

```powershell
$env:VISIONIQ_DB_INTEGRATION = '1'
python -m pytest tests/intelligence/test_brief_pipeline_db.py        # 4 passed
$env:VISIONIQ_BRIEF_DB_INTEGRATION = '1'
dotnet test tests/VisionIQ.Api.Tests --filter FullyQualifiedName~BriefDeliveryDatabaseIntegrationTests   # 2 passed
```

The .NET database test also needs `DB_INTELLIGENCE_USER` and `DB_INTELLIGENCE_PASSWORD`, because it
stores a brief the way Intelligence does.

## Running a pilot on a few work items of a live organization

Set `PICKUP_ALLOWED_WORK_ITEM_IDS` to the ids of the chosen work items (for example a few Task
items created for the pilot). Pickup, the sweep, briefs, ticket comments and ratings are then
limited to those items; every other ticket of the organization is ignored with
`work_item_not_allowlisted`. Details, the Task type setup and what the list does not cover are in
[BRF-01, Piloting on chosen work items](BRF-01-ticket-pickup/README.md#piloting-on-chosen-work-items).

## Rules for testing against real systems

The read-only rule in [TESTING-KICKOFF-TODAY.md](../TESTING-KICKOFF-TODAY.md) applies in full.
`BRIEF_PUBLISH_MODE=ado` is the only code path in VisionIQ that writes to a source system: it adds
one comment to a work item. Do not use it against the real Azure DevOps organization while that
rule is in force. `record` mode and the fakes in the tests cover the same code up to the HTTP call.

## Open items

- **Real models.** The Gemini and Claude providers are tested against fakes only. Run a real
  brief and the S08-12 evaluation on labelled cases before relying on them. The default `GEMINI_MODEL` (`gemini-2.5-flash`) needs confirming for the
  project and region.
- **Decision D04** (what data may go to Claude) is unresolved, so Claude is off by default. With it
  off, a brief has no second opinion and no fallback when Gemini fails.
- **Posting to real ADO** (`ado` mode) and the service hook for "work item commented" have not been
  tried against a real organization. They need a bot identity with Work Items (Read & write).
- **`.env.example`** does not list the new variables, so `scripts/check-env-contract.ps1` fails
  until they are added (together with the FND-05 and BRF-01 keys already listed in those docs).
- **Live access check.** Intelligence cannot ask ADO or Confluence whether access changed since
  indexing. A brief is limited to evidence whose stored access list names the run's security scope
  (`stored_access_only` in `app/main.py`).
- **More than one API or Intelligence instance.** Each worker claims one item at a time under a
  lease, so two instances do not work on the same run. A narrow window remains in `ado` mode: if
  a post takes longer than its 2-minute lease, a second instance can post too.
- **Risk and hotspot thresholds** are an initial rule. Design §7.5 asks for an ADR and a labelled
  hold-out set before they are called predictive.
- **Enhancement tickets** get the same defect-style brief. A separate enhancement prompt (§8.5) is
  a later story.
- **`graph.neighbors`** (S08-08) is not offered as a tool: the authorized graph traversal lives in
  the .NET API. The tool allowlist has `retrieval.search` and `evidence.fetch_authorized_source`.
- **Reporting decision D11** is unresolved: the hotspot report is a CLI and JSON output, with no
  delivery channel.
