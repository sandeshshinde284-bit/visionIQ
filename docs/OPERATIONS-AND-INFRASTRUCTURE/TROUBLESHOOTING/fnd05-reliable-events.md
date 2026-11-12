# FND-05 — Reliable events between .NET and Python

Story: the two services talk through versioned events on Pub/Sub, so nothing is lost, duplicated or misread.

Full write-up, including how this was carried over from the `visionIQ` repository:
[features/FND-05-reliable-events](features/FND-05-reliable-events/README.md).

## What is built

| Task | Where |
|---|---|
| S03-01 shared envelope | `contracts/events/envelope.v1.schema.json` (event_id, schema_version, tenant_id, run_id, source_revision, occurred_at, correlation_id, trace_id) |
| S03-02 / S03-03 contracts | `contracts/events/analysis.{requested,completed}.v1.schema.json` + golden fixtures in `contracts/events/fixtures/{valid,invalid}` |
| S03-06 typed models, parity | C# records `Features/Events/EventContracts.cs`; Pydantic `app/events/envelope.py`. Both test suites run the same fixtures; .NET output is checked against the JSON Schemas |
| S03-07 inbox idempotency | `workflow.event_inbox` / `knowledge.event_inbox`: inbox row and handler writes commit in one transaction; a rejected event keeps its inbox row (`rejected_reason`) so a replay is a duplicate |
| S03-08 outbox | `workflow.event_outbox` / `knowledge.event_outbox`, claimed with a lease (`FOR UPDATE SKIP LOCKED`), published outside any transaction, with exponential backoff; rows that cannot be published, or fail 20 times, are parked (`parked_at`) |
| S03-09 retry and DLQ | Push endpoint `POST /events/pubsub` in both services: 204 ack, 400 contract violation or conflict with stored state (sent to the dead-letter topic), 503 transient |
| S03-10 correlation | `trace_id`, `correlation_id`, `run_id`, `event_id` on every log line of an event (.NET scope, structlog contextvars) |
| S02-07 / S02-08 topics, DLQ | `contracts/events/topology.json`; local emulator in Compose; cloud plan in `scripts/pubsub/provision-pubsub.ps1` |

With the BRIEF pipeline switched off (the default), Intelligence answers every `analysis.requested` with
`analysis.completed` `BLOCKED` / `analysis_pipeline_disabled`, never a fabricated result. With
`BRIEF_PIPELINE_ENABLED=true` a worker answers it with a real brief (`docs/features/BRIEF-ARCHITECTURE.md`).

## Run locally

```powershell
docker compose up --build -d          # postgres, migrations, pubsub emulator, api, intelligence, pubsub-init
Invoke-RestMethod -Method Post http://127.0.0.1:5100/dev/analysis-requests -ContentType application/json -Body '{
  "projectId":"VisionIQ-Demo","ticketId":"4711","ticketRevision":12,"repositoryId":"demo-repo",
  "baseCommitSha":"0123456789abcdef0123456789abcdef01234567","analysisMode":"brief","securityScopeId":"project:VisionIQ-Demo"}'
docker compose logs api intelligence | Select-String <traceId from the response>
```

`/dev/analysis-requests` is mapped only in the Development environment.

Opt-in database tests (completion evidence: the same event twice gives one stored result):

```powershell
$env:VISIONIQ_DB_INTEGRATION = '1'; python -m pytest tests/intelligence/test_event_reliability_db.py
```

## Configuration (both services)

| Variable | Default | Meaning |
|---|---|---|
| `TENANT_ID` | `visioniq-dev` | Tenant scope written into produced events |
| `PUBSUB_PROJECT_ID` | none | Required when dispatch is enabled |
| `EVENTS_DISPATCH_ENABLED` | `false` | Start the outbox dispatcher |
| `EVENTS_DISPATCH_INTERVAL_SECONDS` | `2` | Idle poll interval |
| `PUBSUB_EMULATOR_HOST` | none | Local emulator (`pubsub:8085` in Compose) |

## Open items

- The emulator does not enforce dead-lettering; verify "poison event lands in the DLQ and can be replayed" in the dev project after `provision-pubsub.ps1 -Apply`.
- Confirm Pub/Sub push reaches Cloud Run services with internal ingress in this project, and grant `roles/run.invoker` to the push identity and `roles/pubsub.publisher` to the runtime identity.
- Add the FND-05 variables to `.env.example` (`scripts/check-env-contract.ps1` reports them missing).
- LRN-13's `knowledge.change_events` outbox can be drained by the same dispatcher once `knowledge.changed.v1` has a schema (S03-04).
