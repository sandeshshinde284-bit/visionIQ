# BRF-01 — Pick up tickets automatically

> Story: [Trello BRF-01](https://trello.com/c/hYolJudU/21-brief-brf-01-pick-up-tickets-automatically) ·
> Gate G2 · Depends on FND-05, LRN-01 · Design v1.11 §3.5, §8.1, §8.2, §10.11, §11.1.1
>
> **Status:** implemented and unit-tested on branch `feature/BRF-01-ticket-pickup` (not committed). The database
> test and a run against a real ADO organization are still open (see [Open items](#open-items)). 5 November 2026.

*As an engineer, I want VisionIQ to start as soon as I move a ticket to In Progress, so that I never have to open
a separate tool.*

| Acceptance criterion | How it is met |
| --- | --- |
| The webhook is verified, stored and acknowledged within seconds; analysis runs in a worker. | `POST /webhooks/ado` checks the shared secret, applies the rules, stores the pickup and answers `202`. It makes no ADO call and does no analysis. A background worker starts the run. Intelligence does the analysis after receiving the FND-05 event. |
| The same ticket event never starts a second run. | One stored pickup per ADO event id. One run per (project, ticket, pickup revision, policy revision), enforced by a unique index. Older revisions than an existing run are marked stale. Bot updates and later edits are not pickups. |
| A missed webhook is picked up by the reconciliation sweep. | Every 15 minutes the sweep asks ADO which eligible items became actionable recently. It applies the same rules and stores any pickup that has neither a stored pickup nor a run. |

Tasks: **S04-02** ADO service-hook receiver, **S10-01** eligible ticket trigger rules, **S15-14** missed-event
recovery test.

## The whole flow

```
 Azure DevOps                       VisionIQ.Api (.NET, workflow schema)                         Intelligence (Python)
 ────────────                       ────────────────────────────────────                         ─────────────────────
 Engineer moves Bug #4711
 New → In Progress (rev 12)
        │ service hook
        ▼ workitem.updated
 POST /webhooks/ado ──► 1. Basic auth = PICKUP_WEBHOOK_SECRET?          no → 401
                        2. Parse body (only the fields the rules need) bad → 400
                        3. Pickup rules (S10-01)                         not a pickup → 202 "ignored" + reason
                        4. INSERT workflow.ticket_pickups                same ADO event id → 202 "duplicate"
                           (status pending, payload SHA-256, trace id)   DB down → 503 (ADO retries)
                        5. 202 "queued"                                   ← all of this takes milliseconds

 PickupWorker (every 2 s) ─► claim pending pickups (lease, SKIP LOCKED)
                            resolve base commit: pinned in policy, or head of the branch via ADO REST
                            one transaction:
                              workflow.analysis_runs  (unique per project/ticket/revision/policy)
                              workflow.event_outbox   analysis.requested v1
                              ticket_pickups.status = started | duplicate (+ run_id)
                                     │
                     OutboxDispatcher (FND-05) ──► Pub/Sub ──push──► POST /events/pubsub ──► knowledge.analysis_requests
                                                                                     answers analysis.completed
 PickupSweepService (every 15 min) ─► WIQL: eligible types in a pickup state changed in the last 24 h
   (needs ADO_ORG_URL + ADO_READ_PAT)  per item: revision history → the revision that made it actionable
                                       same rules → unknown (no pickup, no run)? → INSERT ticket_pickups (ado_sweep)
                                       → the worker picks it up exactly like a webhook pickup
```

Today Intelligence answers every request with `BLOCKED` / `analysis_pipeline_not_implemented` (FND-05). The brief
itself and the ADO comment are later BRIEF stories. So "a run started" means a `workflow.analysis_runs` row,
followed by its `analysis.requested` event and the `analysis.completed` reply.

### What counts as a pickup (S10-01, decision D05)

A ticket change is a pickup only if **all** of these hold. Otherwise the webhook answers `202 ignored` with the
reason code and logs it.

| Rule | Reason code when it fails |
| --- | --- |
| The body comes from the configured ADO organization (`organization_url`) | `organization_not_allowed` |
| The project is in the policy | `project_not_configured` |
| The work-item type is one of the project's `defect_types` or `enhancement_types` | `type_not_eligible` |
| After the change the item is **actionable**: in one of `pickup_states` and, if `require_assignee`, assigned | `unassigned` (in the state but nobody assigned) or `no_pickup_transition` |
| Before the change it was **not** actionable, so later edits, comments and reassignments do not count | `no_pickup_transition` |
| The change was not made by one of `bot_identities` (no loops from VisionIQ's own updates) | `bot_update` |
| The item has none of the `excluded_tags` | `excluded_tag` |

"Actionable" combines the state and the assignee, as the design asks ("do not treat *assigned* and *In Progress*
as necessarily identical"). Two examples:
- A ticket moved to In Progress with no assignee is ignored (`unassigned`). It is picked up later, at the revision
  where someone is assigned.
- A project with `require_assignee: false` picks up on the state change alone.

A newly created item that is already actionable is also a pickup (`workitem.created`).

### No duplicate runs

| Situation | What happens |
| --- | --- |
| ADO retries the same delivery | Same ADO event `id` → `ticket_pickups` unique (trigger, delivery id) → `202 duplicate`, nothing new |
| Webhook and sweep both see the same pickup | Both resolve to the same pickup revision. The sweep skips it if a pickup or run exists. If both were stored, the second hits the unique run index and is marked `duplicate` / `run_already_started`, linked to the existing run. |
| A late delivery of an older pickup revision, or an older pickup still queued behind a newer one | Marked `duplicate` / `stale_revision` (linked to the newer run when there is one) |
| Ticket leaves In Progress and comes back later | A new pickup revision, so a new run (a real second pickup) |
| `policy_revision` changed | New pickups use the new revision, and a run is stamped with the policy it actually ran under. The sweep does **not** re-brief tickets that already have a run under the old one. |
| Several API instances | Claims use a lease with `SKIP LOCKED`. Starting a run takes a per-ticket advisory lock. Status updates only touch rows that are still `pending`. |

### Failure handling

| Failure | Behaviour |
| --- | --- |
| Wrong or missing secret | `401`, logged as `unauthorized`, nothing stored |
| Body is not a usable work-item event (e.g. resource details not "All") | `400`, so ADO shows the subscription as failing |
| Other event types on the same subscription | `202 ignored` / `unsupported_event` |
| Database unavailable at the webhook | `503`; ADO retries the delivery, and the sweep catches it if those retries also fail |
| Branch head cannot be resolved (ADO down, branch missing, no ADO configured and no pinned commit) | Pickup stays `pending`, retried with backoff (15 s doubling to 15 min). The head is looked up once per project per batch. After 12 attempts (about 1.5 h) it is `failed` / `base_commit_unresolved`. A failed pickup is not lost: an ADO redelivery re-arms it, and the next sweep stores it again because failed pickups do not count as known. |
| Project removed from the policy before the worker ran | `failed` / `project_not_configured` |
| One project's sweep fails | Logged. The other projects are still swept. |

Logs carry project, work-item id, revision and reason codes only. Ticket text, names and e-mails are never logged.
The database stores the triggering actor's ADO identity **id** and a SHA-256 of the body, not the body itself.

## Code map

| Task | Where |
| --- | --- |
| S04-02 receiver | `src/VisionIQ.Api/Features/Pickup/PickupWebhookEndpoints.cs` (`POST /webhooks/ado`, auth, size limit) and `AdoServiceHook.cs` (body parser) |
| S10-01 rules | `PickupPolicy.cs` (policy file, `Evaluate`, `IsActionable`) and `config/pickup-policy.example.json` |
| Run creation | `PickupProcessor.cs` and `PickupHostedServices.cs` (`PickupWorker`), `TicketPickupStore.cs`, `Features/Events/AnalysisRuns.cs` (shared with `/dev/analysis-requests`) |
| S15-14 sweep | `PickupSweep.cs` and `PickupHostedServices.cs` (`PickupSweepService`); ADO REST calls in `AdoPickupSource.cs` (WIQL, revisions, refs) |
| Wiring | `PickupServiceRegistration.cs`, `PickupOptions.cs`, `Program.cs` |
| Schema | `db/migrations/versions/20261105_0019_ticket_pickup.py`: adds `workflow.ticket_pickups`, plus `trigger_source`, `ticket_kind`, `policy_revision` and a unique pickup index on `workflow.analysis_runs` |
| Tests | `tests/VisionIQ.Api.Tests/PickupPolicyTests.cs`, `AdoServiceHookTests.cs`, `PickupProcessingTests.cs`, `PickupWebhookEndpointTests.cs`, `PickupDatabaseIntegrationTests.cs` (opt-in) |

`workflow.ticket_pickups.status`: `pending` → `started` (run created) | `duplicate` (linked to an existing run;
reason `run_already_started` or `stale_revision`) | `failed` (reason says why).

## Configuration

Pickup is **off** unless `PICKUP_POLICY_FILE` is set. Then `/webhooks/ado` is not mapped and no worker runs.

| Variable | Default | Meaning |
| --- | --- | --- |
| `PICKUP_POLICY_FILE` | none | Path to the policy JSON (copy `config/pickup-policy.example.json` to `config/pickup-policy.local.json`, which git ignores) |
| `PICKUP_WEBHOOK_SECRET` | none | Required with the policy; at least 24 characters. ADO sends it as the Basic-auth password |
| `PICKUP_WORKER_INTERVAL_SECONDS` | `2` | Idle poll interval of the worker |
| `PICKUP_SWEEP_INTERVAL_MINUTES` | `15` | `0` turns the sweep off |
| `PICKUP_SWEEP_LOOKBACK_HOURS` | `24` | How far back the sweep looks for pickups; must cover at least two sweep intervals, so consecutive sweeps overlap |
| `ADO_ORG_URL`, `ADO_READ_PAT` | none | Needed for the sweep and to resolve the branch head. PAT scopes: Work Items (Read), Code (Read). Without them only pickups with a pinned `base_commit_sha` can start. |
| `EVENTS_DISPATCH_ENABLED` (FND-05) | `false` | Must be `true` for `analysis.requested` to leave the outbox |

Policy file fields (per project): `project`, optional `project_id` (required when the name has spaces),
`defect_types`, `enhancement_types`, `pickup_states`, `require_assignee` (default `true`), `excluded_tags`,
`repository_id`, `branch` (default `main`), optional `base_commit_sha`, and `security_scope_id`. At the top level:
`policy_revision`, `organization_url` and `bot_identities`. Invalid policies stop start-up with a message listing
every problem.

### ADO service hook setup (per project)

In Project settings → Service hooks → Create subscription → **Web Hooks** → trigger **Work item updated**:
1. Filter by work-item type if you like; ADO filters are optional, because the policy decides.
2. URL: `https://<api host>/webhooks/ado`.
3. Basic authentication: any user name, with the password set to `PICKUP_WEBHOOK_SECRET`.
4. Resource details to send: **All**. Messages and detailed messages: **None**.

Add a second subscription for **Work item created** if items can be created directly in an actionable state.

## How to test it

### 1. Unit tests (no database, no ADO)

```powershell
dotnet test tests/VisionIQ.Api.Tests --filter "FullyQualifiedName~Pickup|FullyQualifiedName~AdoServiceHook"
```

The suite has 78 tests:
- every rule and its reason code
- the body parser on real-shaped payloads
- assignment-after-state pickups
- the webhook's 202, 401, 400, 413 and 503 responses
- one run per pickup, including webhook plus sweep and late older revisions
- commit resolution retries
- sweep recovery and its skip rules
- WIQL escaping
- validation of the policy file

### 2. Local end to end with Docker (no ADO needed)

1. Copy `config/pickup-policy.example.json` to `config/pickup-policy.local.json`. The example pins
   `base_commit_sha`, so no ADO access is needed.
2. Add to `.env`:
   ```
   PICKUP_POLICY_FILE=/app/config/pickup-policy.local.json
   PICKUP_WEBHOOK_SECRET=<at least 24 random characters>
   ```
3. Start everything and post the sample webhook. The sample is "Bug #4711 moved New → In Progress", matching the
   example policy.
   ```powershell
   docker compose up --build -d
   $secret = '<same secret>'
   $auth = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("ado:$secret"))
   $body = Get-Content docs/features/BRF-01-ticket-pickup/sample-workitem-updated.json -Raw
   Invoke-RestMethod -Method Post http://127.0.0.1:5100/webhooks/ado -Headers @{ Authorization = $auth } -ContentType application/json -Body $body
   # → status: queued. Post it again → status: duplicate
   ```
4. See the result:
   ```powershell
   docker compose exec postgres psql -U visioniq -d visioniq -c "SELECT trigger_source, ticket_id, ticket_revision, status, reason, run_id FROM workflow.ticket_pickups"
   docker compose exec postgres psql -U visioniq -d visioniq -c "SELECT run_id, ticket_id, ticket_revision, status, trigger_source, policy_revision FROM workflow.analysis_runs ORDER BY requested_at DESC LIMIT 5"
   docker compose logs api intelligence | Select-String 4711
   ```
   You should see one pickup `started` and one run, which moves to `completed` once Intelligence answers
   `BLOCKED`.
5. Try the negative cases:
   - Change `"oldValue": "New"` to `"In Progress"` → `ignored` / `no_pickup_transition`.
   - Remove `System.AssignedTo` → `unassigned`.
   - Use a wrong secret → `401`.
   - Change `"id"` and `"rev"`/`"newValue"` to 10 (older) after rev 12 has a run → the pickup ends as
     `duplicate` / `stale_revision`.

### 3. Database test (opt-in)

This proves that a redelivery plus the sweep of one pickup give exactly one run and one `analysis.requested`
event. Run it after `python -m db.manage all` against a disposable database:

```powershell
$env:VISIONIQ_PICKUP_DB_INTEGRATION = '1'; $env:DB_API_PASSWORD = '<api password>'
dotnet test tests/VisionIQ.Api.Tests --filter FullyQualifiedName~PickupDatabaseIntegrationTests
```

### 4. Against a real ADO project (completion evidence)

1. Set `ADO_ORG_URL` and `ADO_READ_PAT`, and use your real project, repository and branch in the policy. Remove
   `base_commit_sha` so the branch head is resolved.
2. Expose the API (for example a dev Cloud Run URL or a tunnel) and create the service hook described above.
3. Move a real Bug to In Progress. Expect exactly one `ticket_pickups` row (`ado_webhook`, `started`) and one run.
4. Missed-webhook recovery (S15-14):
   1. Disable the subscription and move another ticket to In Progress.
   2. Wait for the sweep, or restart the API: the first sweep runs 30 s after start-up.
   3. Expect an `ado_sweep` pickup and exactly one run.
   4. Re-enable the subscription and check that nothing is duplicated.

## Verification on this branch

| Check | Result |
| --- | --- |
| `dotnet build VisionIQ.sln` | 0 errors |
| `dotnet test tests/VisionIQ.Api.Tests` | 266 passed, 7 skipped; 78 of these are BRF-01 tests |
| `pytest tests` (Python, unchanged) | 193 passed, 84 skipped |
| Alembic | single head `20261105_0019` |
| `ruff check` on the new migration | clean |
| `scripts/check-env-contract.ps1` | **fails**: `.env.example` lacks the FND-05 keys and now also `PICKUP_POLICY_FILE` and `PICKUP_WEBHOOK_SECRET` |

Not run here (no Docker or PostgreSQL on this machine): the database test, the Docker round trip, and anything
against real ADO.

## Review

The code was reviewed twice before handing over: once by me, and once independently from the code alone. The
fixes from those reviews:
- An item assigned while already In Progress is a pickup.
- A policy-revision bump does not re-brief tickets.
- Older revisions are marked stale, whether a newer run exists or a newer pickup is only queued.
- Failed pickups are re-armed by redelivery or the sweep.
- The sweep reads newest items first.
- Updates are guarded to `pending` rows, and run creation takes a per-ticket advisory lock.
- The branch head is looked up once per project per batch.
- A null `revisedBy` falls back to `System.ChangedBy`.
- The lookback must overlap the sweep interval.
- A run is stamped with the policy it ran under.

## Open items

- Add `PICKUP_POLICY_FILE`, `PICKUP_WEBHOOK_SECRET`, `PICKUP_WORKER_INTERVAL_SECONDS`,
  `PICKUP_SWEEP_INTERVAL_MINUTES` and `PICKUP_SWEEP_LOOKBACK_HOURS` to `.env.example`, together with the FND-05
  keys.
- Decision D05 per real project: which states count as pickup, and whether an assignee is required.
- Run the database test and the two ADO scenarios above. They are the story's completion evidence.
- The sweep only sees items that are **still** in a pickup state. An item moved to In Progress and out again
  between sweeps is not recovered. It looks at the 500 most recently changed items per project per sweep.
- The sweep has no stored checkpoint. If the API is down for longer than `PICKUP_SWEEP_LOOKBACK_HOURS`, pickups
  from that time are not recovered.
- The worker does not re-read the ticket from ADO before starting a run. A late pickup for a ticket that has
  since moved to Done, with no newer pickup stored, still gets a run.
- For `workitem.created`, ADO may send the creator as a `"Name <e-mail>"` string. The bot filter still works,
  but `triggered_by` (identity id) is then empty.
- Not in this story:
  - posting the brief and its status back to the ticket (S04-03 / later BRIEF stories)
  - explicit re-analysis requests
  - Jira
  - a `failed` run status when Intelligence never answers (see FND-05 open items)
