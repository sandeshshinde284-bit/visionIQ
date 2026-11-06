# FND-05 — Reliable events between .NET and Python

> Story: [Trello FND-05](https://trello.com/c/yzaI6QcF/5-g0-foundation-fnd-05-reliable-events-between-net-and-python)
>
> **Status:** implemented and unit-tested; cloud dead-letter and push-auth checks still open (see
> [Open items](#open-items)). Updated 3 November 2026.

The .NET API and the Python Intelligence service talk through versioned events on Google Pub/Sub, so
that nothing is lost, duplicated or misread between them.

## Where this came from

The work was first built in the separate `visionIQ` repository (commit `f58ffba` on
`feature/fnd05-reliable-events`), which could not be pushed. It was carried over to this repository on
branch `feature/FND-05-reliable-events`, cut from `dev` (which already contains LRN-01 to LRN-14). The
code is the same; these changes were needed to fit it onto `dev`:

| Change | Why |
| --- | --- |
| Migration renamed `20261102_0002` → `20261102_0018`, `down_revision` `20261029_0001` → `20261101_0017` | `dev` already has the LEARN chain 0002–0017; this keeps a single Alembic head |
| `src/VisionIQ.Api/Program.cs`: kept the LRN evidence, ADO access and graph API wiring, and added the FND-05 `NpgsqlDataSource`, event services and endpoints | Both branches edited the same lines |
| `google-cloud-pubsub` and `jsonschema` added to `requirements-runtime.txt` and `requirements-runtime.lock.txt` (instead of `requirements.txt`) | On `dev` the Docker images install only the hashed runtime lock; `requirements.txt` includes the runtime file |
| `.github/workflows/pr-validation.yml` change left out | That workflow does not exist in this repository |
| Review fixes (after the carry-over) | Error codes may contain digits; publishing happens outside the locking transaction; unpublishable rows are parked; conflicting events are rejected instead of retried; the Pub/Sub provisioning script works in Windows PowerShell 5.1; `/ready` uses `app/db.py`. After PR review: a rejected event keeps its inbox row (`rejected_reason`), and outbox rows are parked after 20 failed publishes |

The lock file was updated by adding only the new packages and their dependencies (`attrs`,
`google-cloud-pubsub`, `jsonschema`, `jsonschema-specifications`, `opentelemetry-sdk`,
`opentelemetry-semantic-conventions`, `referencing`, `rpds-py`). Every existing pin is unchanged.
It was compiled with `pip-compile --generate-hashes --strip-extras` on Windows, and the Linux-only
`uvloop` entry was kept and the Windows-only `tzdata` entry dropped. Some `# via` comments on
existing entries do not list `google-cloud-pubsub` yet; a Linux regeneration will fix that.

## How it works

```
 .NET API (workflow schema)                         Python Intelligence (knowledge schema)
 ─────────────────────────                          ──────────────────────────────────────
 POST /dev/analysis-requests                         POST /events/pubsub
   └─ one transaction:                                 └─ validate envelope + payload (JSON Schema)
      workflow.analysis_runs                           └─ one transaction:
      workflow.event_outbox  ──► OutboxDispatcher         knowledge.event_inbox  (dedup on event_id)
                                 │                        knowledge.analysis_requests
                                 ▼                        knowledge.event_outbox (analysis.completed)
                     topic visioniq-analysis-requested-v1 ──push──►
                                                                     │
 POST /events/pubsub ◄──push── topic visioniq-analysis-completed-v1 ◄┘ (outbox dispatcher)
   └─ workflow.event_inbox + workflow.analysis_results, run marked completed
```

- **Contracts first.** `contracts/events/` holds the shared envelope (`event_id`, `schema_version`,
  `tenant_id`, `run_id`, `source_revision`, `occurred_at`, `correlation_id`, `trace_id`), one JSON
  Schema per event type and version, and golden fixtures. Both test suites run the same fixtures.
  Unknown event types or versions and extra fields are rejected.
- **Transactional outbox.** A service writes its business row and the event in one transaction. A
  dispatcher claims due rows in one short statement that leases them (`FOR UPDATE SKIP LOCKED`, so
  replicas never claim the same row). It then publishes each row with no transaction or lock open
  (10 s timeout per publish) and commits each outcome separately. A crash or shutdown mid-batch only
  re-sends rows whose outcome was not recorded, after their lease ends. Failed publishes back off
  exponentially, up to 10 minutes. After 20 failed attempts (about 2.3 hours), or at once if the
  stored payload no longer matches its contract, the row gets `parked_at` set, leaves the queue, and
  waits for a person. So a deleted topic or a missing permission does not retry forever. Find parked
  rows with `WHERE parked_at IS NOT NULL`; after fixing the cause, requeue one with
  `UPDATE … SET parked_at = NULL, attempt_count = 0, available_at = now() WHERE event_id = …`.
  `last_error_code` holds the exception type name, reduced to letters, digits, `_` and `.`.
- **Idempotent inbox.** The inbox row and the handler's writes commit together. A redelivered
  `event_id` changes nothing and is acknowledged. The handler runs inside a savepoint: if it rejects
  the event, its writes are rolled back but the inbox row is kept with `rejected_reason`. A replay of
  a rejected event (for example from the dead-letter topic) is then acknowledged as a duplicate
  without running the handler again. To re-apply such an event on purpose, delete its inbox row first.
- **Push responses.** `204` processed or duplicate (ack). `400` malformed, contract-invalid, or in
  conflict with stored state (retrying cannot fix it, so it ends in the dead-letter topic). `503`
  database or handler failure (Pub/Sub retries).
- **Conflicts are rejected, not retried.** The API rejects an `analysis.completed` for an unknown run
  (`unknown_run`), for another tenant's run (`tenant_mismatch`), or for a run that already has a
  result (`run_already_completed`). Intelligence rejects a second `analysis.requested` for a run
  it has already answered (`run_already_requested`).
- **Correlation.** `trace_id`, `correlation_id`, `run_id` and `event_id` are on every log line about
  an event: a logging scope in .NET and structlog contextvars in Python.
- **No fake results.** Until the BRIEF pipeline exists, Intelligence answers every
  `analysis.requested` with `analysis.completed` / `BLOCKED` /
  `analysis_pipeline_not_implemented`.

Each service writes only its own schema (`workflow` = API, `knowledge` = Intelligence), so the FND-03
role isolation still holds. The FND-03 default privileges in `db/manage.py` grant the new tables to
the owning role automatically.

## Task map

| Task | Where |
| --- | --- |
| S03-01 shared envelope | `contracts/events/envelope.v1.schema.json` |
| S03-02 / S03-03 event contracts | `contracts/events/analysis.{requested,completed}.v1.schema.json`, `contracts/events/fixtures/{valid,invalid}` |
| S03-06 typed models and parity | C# `src/VisionIQ.Api/Features/Events/EventContracts.cs`, `EventEnvelope.cs`; Pydantic `src/VisionIQ.Intelligence/app/events/envelope.py`, `contracts.py` |
| S03-07 inbox idempotency | `workflow.event_inbox`, `knowledge.event_inbox`; `InboundEvents.cs`, `app/events/processing.py` |
| S03-08 outbox | `workflow.event_outbox`, `knowledge.event_outbox`; `EventOutbox.cs`, `EventPublishing.cs`, `app/events/outbox.py`, `publisher.py` |
| S03-09 retry and DLQ | `POST /events/pubsub` in both services (`PubSubPushEndpoints.cs`, `app/api/events.py`) |
| S03-10 correlation | `EventLog.cs`, structlog contextvars in `processing.py` |
| S02-07 / S02-08 topics and DLQ | `contracts/events/topology.json`; emulator in `docker-compose.yml`; cloud plan in `scripts/pubsub/provision-pubsub.ps1` |
| Schema | `db/migrations/versions/20261102_0018_event_outbox_inbox.py` |

## Run locally

```powershell
docker compose up --build -d   # postgres, migrations, pubsub emulator, api, intelligence, pubsub-init
Invoke-RestMethod -Method Post http://127.0.0.1:5100/dev/analysis-requests -ContentType application/json -Body '{
  "projectId":"VisionIQ-Demo","ticketId":"4711","ticketRevision":12,"repositoryId":"demo-repo",
  "baseCommitSha":"0123456789abcdef0123456789abcdef01234567","analysisMode":"brief","securityScopeId":"project:VisionIQ-Demo"}'
docker compose logs api intelligence | Select-String <traceId from the response>
```

`/dev/analysis-requests` is mapped only in the Development environment.

## Configuration (both services)

| Variable | Default | Meaning |
| --- | --- | --- |
| `TENANT_ID` | `visioniq-dev` | Tenant written into produced events |
| `PUBSUB_PROJECT_ID` | none | Required when dispatch is enabled |
| `EVENTS_DISPATCH_ENABLED` | `false` | Start the outbox dispatcher |
| `EVENTS_DISPATCH_INTERVAL_SECONDS` | `2` | Idle poll interval |
| `PUBSUB_EMULATOR_HOST` | none | Local emulator (`pubsub:8085` in Compose) |
| `EVENTS_PUSH_BASE_URL_API` / `EVENTS_PUSH_BASE_URL_INTELLIGENCE` | `http://api:5100` / `http://intelligence:8000` | Push targets used by the emulator setup only |

## Verification on this branch

| Check | Result |
| --- | --- |
| `dotnet build VisionIQ.sln` | 0 errors |
| `dotnet test tests/VisionIQ.Api.Tests` | 188 passed, 6 skipped |
| `pytest tests/intelligence` | 193 passed, 84 skipped (database tests need `VISIONIQ_DB_INTEGRATION=1`) |
| `ruff check` / `ruff format --check` | clean |
| `scripts/check-env-contract.ps1` | **fails**: the FND-05 variables above are not in `.env.example` yet |

Not run here: the database integration tests (`test_event_reliability_db.py`, which proves the same
event twice gives one stored result), the Docker Compose round trip, and anything in Google Cloud.

```powershell
$env:VISIONIQ_DB_INTEGRATION = '1'; python -m pytest tests/intelligence/test_event_reliability_db.py
```

## Open items

- Add the FND-05 variables to `.env.example` so `scripts/check-env-contract.ps1` passes.
- Run the database integration tests and the Compose round trip on this branch. The lease-based claim,
  `parked_at`, the publish-attempt cap and the conflict rejections (including the kept inbox row) have
  unit tests only; they have not been run against PostgreSQL.
- `workflow.analysis_runs.status` has no failure state: a run Intelligence never answers stays
  `requested`, indistinguishable from one still in flight. A timeout that marks such runs failed
  belongs with the BRIEF pipeline.
- A handler failure that is neither a contract violation nor a rejection (for example an unexpected
  unique violation) returns `503`. Pub/Sub retries it up to `max_delivery_attempts` from
  `contracts/events/topology.json` and then dead-letters it; nothing in the services caps it.
- Grants on the new tables come from the default privileges `db/manage.py` sets up, as for every
  earlier migration; the migration itself does not check them.
- `.env.example` still has `PUBSUB_TOPIC_*` variables from an earlier design. Topics now come from
  `contracts/events/topology.json`, so remove them when adding the FND-05 variables.
- The emulator does not enforce dead-lettering. After `provision-pubsub.ps1 -Apply` in the dev project,
  check that a bad event lands in the dead-letter topic and can be replayed.
- Confirm Pub/Sub push reaches Cloud Run services with internal ingress, and grant `roles/run.invoker`
  to the push identity and `roles/pubsub.publisher` to the runtime identity.
- LRN-13's `knowledge.change_events` outbox can use the same dispatcher once `knowledge.changed.v1`
  has a schema (S03-04).
