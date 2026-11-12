# VisionIQ - Google Cloud Setup & Deployment Guide

**Living guide | Cloud infrastructure and deployment | Revision 1.2: 12 November 2026**
**Cloud SQL instance:** `visioniqdev`  |  **Project:** `gbr-mts-wgh-hkt-dev`  |  **Region:** `europe-west3`

> **Status (12 November 2026):** The 29 October Cloud SQL screenshot snapshot is historical evidence. An initial FND-03 Cloud Run migration job and FND-04 API/Intelligence deployments were subsequently reported as successful; these are **historical execution reports, not fresh Google Cloud verification**. Inventory the existing project before creating, migrating or redeploying resources. Never store passwords, tokens, credential values or individual ownership labels here.

This is the **single living cloud operations guide** for VisionIQ. Its canonical repository path is `docs/Functional/VisionIQ_Google_Cloud_Setup_Deployment_Guide.md`. Preserve the 29 October Cloud SQL baseline as historical evidence; append actual verified configurations, revision/image digests, sanitized command results, acceptance findings and rollback evidence here. Feature-specific references remain linked rather than copied into multiple competing cloud documents.

**Delivery boundary:** Cloud-only build, migration, execution and validation. Use approved cloud-hosted build runners, Cloud Shell and Cloud Run services/jobs; do not require local Docker or a laptop PostgreSQL instance. `.env.example` is the shared configuration-key contract, **not** the cloud deployment values: Cloud Run service/job environment variables and Secret Manager supply those values.

## Current state

| Area | Status / evidence |
| --- | --- |
| Console screenshots reviewed | Four provided screenshots show the Cloud SQL Configurations page for `visioniqdev`, including network/security, backups, maintenance and Query Insights. This is visual verification of the displayed settings, not a live API or connectivity test. |
| Historical local development | Local PostgreSQL and API verification were reported previously; local status does **not** establish cloud deployment acceptance. |
| FND-03 migration (historical, now inventoried) | Cloud Run Job `visioniq-db-migrate` exists, last run 30 Oct 2026 at 02:57:27 UTC. Its success, current image, Alembic head and grants remain unverified. |
| FND-04 application deployment (historical, now inventoried) | API/Intelligence services both exist; latest created/ready revisions match `visioniq-api-00001-9gm` and `visioniq-intelligence-00001-zfn`. Images, ingress, authenticated `/ready`, DB access and new code are unverified. |
| Cloud release readiness | **G01.1–G01.11 inventory confirmed from Cloud Shell output (12 Nov 2026). Scheduler checked with no VisionIQ jobs found in `europe-west3`; current image release, migrations, Pub/Sub, webhook auth and feature E2E still pending.** |

## Configuration snapshot - as provided

### 1. Cloud SQL identity and sizing

| Setting | Recorded value |
| --- | --- |
| Cloud project ID | gbr-mts-wgh-hkt-dev |
| Instance ID | visioniqdev |
| Instance connection name | gbr-mts-wgh-hkt-dev:europe-west3:visioniqdev |
| Cloud SQL edition | Enterprise |
| Database engine / version | PostgreSQL 16.15 |
| Region | europe-west3 (Frankfurt) |
| Availability | Single zone |
| Primary zone | europe-west3-c |
| Machine type | db-custom-2-4096 |
| Compute | 2 vCPUs / 4 GB RAM |
| Data cache | Disabled |
| Storage | 10 GB SSD |
| Automatic storage increase | Enabled |
| Disk encryption | Google-managed encryption key |
| Password policy | Enabled |
| Labels | Two labels are visible on the Cloud SQL instance. Their values include individual/team identifiers and are intentionally omitted from this shared guide. |

### 2. Network and connectivity

| Setting | Recorded value |
| --- | --- |
| Private IP connectivity | Enabled |
| Private connection method | Private Service Access (PSA) - enabled |
| Associated network resource | projects/gbr-mts-wgh-hkt-dev/global/networks/vpc-mts-wgh-hkt-mtswghhkt-dev |
| VPC network | vpc-mts-wgh-hkt-mtswghhkt-dev |
| Allocated private IP range | Automatically assigned |
| Internal IP address | 10.102.16.8 |
| Public IP connectivity | Disabled |
| Database TCP port | 5432 |
| Write endpoint | Not provided (-) |

### 3. Security and certificates

| Setting | Recorded value |
| --- | --- |
| Google Cloud services authorization | Disabled |
| Data API access | Disabled |
| App Engine authorization | Enabled |
| SSL/TLS connection setting | Allow only SSL connections - enabled |
| Trusted client certificates | Not required |
| Server certificate authority | Google-managed CAS certificate authority |
| Server certificate expiration | 29 Oct 2027, 5:44:49 PM (as displayed; time zone not specified) |
| Automatic server certificate rotation | Disabled |
| Cloud SQL database flag | cloudsql.iam_authentication = on |

### 4. Backups, recovery and maintenance

| Setting | Recorded value |
| --- | --- |
| Backup tier | Standard backup tier |
| Automated backups | Enabled |
| Backup window | 12:30 PM - 4:30 PM (IST) |
| Automated backups retained | 7 |
| Point-in-time recovery (PITR) | Enabled |
| PITR logs retained | 7 days |
| Backup location | Multi-region: eu |
| Deletion protection | Enabled |
| Retain backups after deletion | Enabled |
| Final backup on deletion | Enabled |
| Final backup retention | 60 days |
| Maintenance timing | Week 1 |
| Maintenance window | Updates may occur any day of the week |
| Next maintenance | None scheduled when reported |
| Maintenance version | POSTGRES_16_15.R20260712.01_44 |

### 5. Observability and other integrations

| Setting | Recorded value |
| --- | --- |
| Query Insights | Enabled |
| Store client IP address | Disabled |
| Store application tags | Disabled |
| Query length | 1024 |
| Sampling rate | 5 (unit not specified in supplied configuration) |
| Vertex AI integration | Enabled |
| Knowledge Catalog integration | Enabled |

## Screenshots and configuration provenance

**Evidence source:** Four team-supplied screenshots from **Google Cloud Console → Cloud SQL → visioniqdev → Configurations**, received 29 October 2026. They confirm the displayed values in Sections 1–5 and the presence of two resource labels (identifiers deliberately not reproduced). These screenshots do **not** demonstrate database connectivity, SQL permissions, successful migration, or Cloud Run readiness. Record those results separately when tested.

**Provisioning method:** Manually created in Google Cloud Console; **no Terraform** is required or used for this instance.

## Working architecture and environment boundaries

```text
Approved Azure DevOps + Confluence
  ├── controlled Cloud Run ingestion/indexing jobs ──> Cloud SQL + private GCS
  └── ADO Service Hooks ──> approved HTTPS webhook ingress ──┐
                                                              ├──> Ingestor webhook Cloud Run (canonical LRN-05/LRN-12)
                                                              └──> API pickup webhook route (BRIEF/feedback)
Private Cloud Run API <──authenticated Pub/Sub push──> Private Cloud Run Intelligence
                  └── authenticated VPC access ──> Cloud SQL: visioniqdev
Cloud Run Jobs: migrations, source sync, indexing, lesson consolidation and acceptance
Vertex AI: Gemini embeddings and BRIEF; Claude integration deferred for this release
```

**Existing environment (to reverify, not recreate):** project `gbr-mts-wgh-hkt-dev`, region `europe-west3`, Artifact Registry `team-visioniq-docker-dev`, VPC connector `vpc-con`, private PostgreSQL instance `visioniqdev` at `10.102.16.8:5432`, migration job `visioniq-db-migrate`, and private services `visioniq-api` / `visioniq-intelligence`. The application database name `visioniq_dev` is in deployment scripts but must be verified live.

The API and Intelligence applications must remain private/IAM-authenticated. **Azure DevOps hooks originate outside GCP**: configure an explicitly approved HTTPS ingress/gateway boundary instead of silently making both internal services public. Pub/Sub push authentication and ADO HTTP Basic webhook authentication are distinct mechanisms.

### Proposed schema and runtime responsibility (decision D07 is not yet signed off)

| PostgreSQL schema | Proposed writer | Proposed other-service access |
| --- | --- | --- |
| `workflow` | .NET API runtime | Read-only |
| `evidence` | .NET ingestion boundary / API runtime | Read-only |
| `knowledge` | Python Intelligence runtime | Read-only |

A distinct migration identity should own schema DDL. The role design is proposed and must be confirmed before cloud grants are applied.

## Deployment execution journal — exact commands and evidence

**Rule for this draft PR:** This file records every **planned and actually executed** cloud deployment or verification step **as we perform it**. Before asking anyone to run a command, write its exact copy/paste form in this guide and commit that update to this same draft PR. When a command is executed, append its UTC timestamp, operator-confirmed result, sanitized output reference, pass/fail/blocked state and follow-up in the same section. **Do not mark a command executed, successful or verified before seeing its result.** Never replace an existing executed entry with a new plan; add a dated correction if needed. When a command modifies cloud infrastructure, record a pre-change state and rollback/restore approach first.

- **Execution location:** Google Cloud Shell **Bash** or another approved GCP-hosted execution environment. No local Docker, workstation PostgreSQL or local image build.
- **Scope:** project `gbr-mts-wgh-hkt-dev`, region `europe-west3`; review any command that targets something else. Prefer explicit `--project` and `--region` instead of changing the user's default `gcloud` configuration.
- **Data protection:** redact all credentials, tokens, secret payloads, auth headers, user names and personal/private identifiers. Do not paste `gcloud run services describe --format=json` or an unfiltered `env` dump, which can expose runtime values. Secret Manager inventory means **names and metadata only**, never `versions access`.
- **Statuses:** `PREPARED` = documented but not run; `EXECUTED — PASS` = actual successful result supplied; `EXECUTED — FAIL` = failed result supplied; `BLOCKED` = prerequisites or authorization missing; `SKIPPED` = deliberately not executed and reason recorded.
- **Evidence:** record date/time, GCP project and resource, reviewed Git SHA/image digest (for deploy commands), sanitized stdout/stderr summary, Cloud Run revision or job execution IDs (when applicable), and checks actually performed. No inferred success.

### G01 — Recheck existing infrastructure (G01.1–G01.12 completed for target region)

**Purpose:** Reconcile the historic FND-03/FND-04 deployment with the **actual** cloud state before creating any resource or running migrations.

**Risk / rollback:** Read-only inspection; no resource changes, therefore no rollback. If authorization is denied, record `BLOCKED` rather than granting IAM without review.

**Execute in Google Cloud Shell (Bash).** Run the entire block; it prints section labels to keep command/output pairs traceable:

```bash
PROJECT="gbr-mts-wgh-hkt-dev"
REGION="europe-west3"

echo "===== G01.1 Active gcloud account/project (account identity may be redacted) ====="
gcloud config get-value project

echo "===== G01.2 Cloud Run services ====="
gcloud run services list --project="$PROJECT" --region="$REGION"

echo "===== G01.3 Existing service revisions (no environment or secrets dump) ====="
for SERVICE in visioniq-api visioniq-intelligence; do
  echo "--- $SERVICE ---"
  gcloud run services describe "$SERVICE" --project="$PROJECT" --region="$REGION" \
    --format="yaml(metadata.name,status.latestCreatedRevisionName,status.latestReadyRevisionName,status.url)"
done

echo "===== G01.4 Cloud Run jobs ====="
gcloud run jobs list --project="$PROJECT" --region="$REGION"

echo "===== G01.5 Artifact Registry repositories ====="
gcloud artifacts repositories list --project="$PROJECT" --location="$REGION"

echo "===== G01.6 Cloud SQL instance ====="
gcloud sql instances describe visioniqdev --project="$PROJECT" \
  --format="yaml(name,state,region,databaseVersion,connectionName)"

echo "===== G01.7 Logical PostgreSQL databases ====="
gcloud sql databases list --instance=visioniqdev --project="$PROJECT"

echo "===== G01.8 Pub/Sub topics ====="
gcloud pubsub topics list --project="$PROJECT"

echo "===== G01.9 Pub/Sub subscriptions ====="
gcloud pubsub subscriptions list --project="$PROJECT"

echo "===== G01.10 Secret Manager SECRET NAMES only ====="
gcloud secrets list --project="$PROJECT" --format="value(name)"

echo "===== G01.11 Runtime service-account emails (redact if needed) ====="
gcloud iam service-accounts list --project="$PROJECT" --format="value(email)"

echo "===== G01.12a Cloud Scheduler jobs in europe-west3 ====="
gcloud scheduler jobs list --project="$PROJECT" --location="$REGION" \
  --format="table(name,schedule,timeZone,state)"
```

**Note about G01.12b:** The original no-location command was executed twice and failed. Keep it in the **historical execution log below**, but do not add that invalid invocation to the reusable commands block.

**G01 execution evidence — supplied Cloud Shell output, 12 November 2026.** Exact command execution time (UTC) was not included in the transcript. These are successful read-only **inventory** checks, not feature acceptance tests.

| Substep | Status | Observed result in Cloud Shell | Outstanding validation |
| --- | --- | --- | --- |
| G01.1 | EXECUTED — PASS | Correct project: `gbr-mts-wgh-hkt-dev` | Current operator permissions are not proven |
| G01.2 | EXECUTED — PASS | `visioniq-api`, `visioniq-intelligence` services exist in `europe-west3`, originally deployed 2026-10-31 | New `main` commit not yet deployed |
| G01.3 | EXECUTED — PASS | API latest created/ready `visioniq-api-00001-9gm`; Intelligence latest created/ready `visioniq-intelligence-00001-zfn` | Ready revision is not DB or endpoint E2E health |
| G01.4 | EXECUTED — PASS | Job `visioniq-db-migrate` exists; last run `2026-10-30 02:57:27 UTC` | Verify last execution result and Alembic head; latest migrations are not proven applied |
| G01.5 | EXECUTED — PASS | Artifact Registry `team-visioniq-docker-dev` exists; last repository update 2026-10-31 | Inspect exact image digests and reviewed-build path |
| G01.6 | EXECUTED — PASS | Cloud SQL `visioniqdev` in `europe-west3`, `POSTGRES_16`, state `RUNNABLE` | Tables, roles, pgvector and SQL read tests remain pending |
| G01.7 | EXECUTED — PASS | Application DB `visioniq_dev` exists, alongside `postgres` | Schema and current migration head pending |
| G01.8 | EXECUTED — PASS (RESOURCE GAP) | Pub/Sub topics exist for unrelated workloads; **no VisionIQ application topic** in output | Provision topology-defined VisionIQ topics only after review |
| G01.9 | EXECUTED — PASS (RESOURCE GAP) | Pub/Sub subscriptions exist for unrelated workloads; **no VisionIQ subscription** in output | Need authenticated VisionIQ push subscriptions and dead-letter handling |
| G01.10 | EXECUTED — PASS (CREDENTIAL GAP) | `visioniq-db-admin-password`, `visioniq-db-api-password`, `visioniq-db-intelligence-password`, `visioniq-build-token-dev` found; other ADO/Confluence token secret names also present. No distinctly named `ADO_WEBHOOK_SECRET` or `PICKUP_WEBHOOK_SECRET` identified | Do not access payloads; verify versions/IAM/ownership and create approved distinct webhook secrets later |
| G01.11 | EXECUTED — PASS | Service accounts `visioniq-runtime` and `visioniq-db-migrate` exist | Actual service bindings and IAM grants pending |
| G01.12 | EXECUTED — PASS (no VisionIQ scheduler in target region) | Cloud Scheduler list in `europe-west3` returned one enabled job, `vision-confluence-sync-scheduler`. The user confirmed this is **another project's job**, not VisionIQ. | Leave that job unchanged; VisionIQ Scheduler provisioning is pending. A project-wide request without `--location` failed because gcloud requires a location; it does not imply missing permissions. |

**Two resolved shell-entry errors:** The first variable assignment misspelled `PROJECT` as `ROJECT`, causing an empty-project error for the first G01.2 attempt. After setting `PROJECT` correctly, G01.2 succeeded. One `echo` heading for G01.6 was entered incorrectly, yielding a shell syntax error; the actual Cloud SQL describe command was rerun successfully. Neither remains a GCP failure.

**Evidence boundaries:** The provided output verifies live resource **metadata** as of the run, not successful database migration, secret accessibility, IAM authorization, application readiness, Pub/Sub message processing or Cloud Scheduler state. Do not include other teams' resource details or personal identities in the shared deployment record.

### G01.12 — Cloud Scheduler inventory (EXECUTED — PASS for VisionIQ target region)

The following is the **exact command and relevant CLI output** provided on **12 November 2026**, with the Cloud Shell username/prompt omitted. Both G01.12a and the unsuccessful G01.12b attempts are retained for traceability. These commands did not modify GCP resources.

**G01.12a — command that was executed successfully:**

```bash
echo "===== G01.12a Scheduler jobs in europe-west3 ====="
gcloud scheduler jobs list --project="$PROJECT" --location="$REGION" \
  --format="table(name,schedule,timeZone,state)"
```

**G01.12a — actual output:**

```text
===== G01.12a Scheduler jobs in europe-west3 =====
ID: vision-confluence-sync-scheduler
SCHEDULE: 0 */3 * * *
TIME_ZONE: Europe/Paris
STATE: ENABLED
```

**Ownership clarification from the user:** `vision-confluence-sync-scheduler` is **another project's** resource, not a VisionIQ job. **Do not change, pause, run, reuse or delete it.** No VisionIQ-specific Scheduler job appeared in the `europe-west3` output. `0 */3 * * *` means every three hours at minute zero in the configured Paris time zone.

**G01.12b — original attempted command (executed twice; retain as failed history, DO NOT reuse):**

```bash
echo "===== G01.12b Scheduler jobs across the project ====="
gcloud scheduler jobs list --project="$PROJECT" \
  --format="table(name,schedule,timeZone,state)"
```

**G01.12b — actual output (same error on two attempts):**

```text
===== G01.12b Scheduler jobs across the project =====
ERROR: (gcloud.scheduler.jobs.list) Please use the location flag to manually specify a location.
ERROR: (gcloud.scheduler.jobs.list) Please use the location flag to manually specify a location.
```

**Interpretation:** The omitted `--location` caused a CLI location-resolution error. It does **not** indicate a missing Cloud Scheduler API, denied IAM permissions or an empty job list across other regions. We have only inventoried the intended VisionIQ deployment region, `europe-west3`.

**Correct reusable read-only command (also shown in the G01 master command list):**

```bash
PROJECT="gbr-mts-wgh-hkt-dev"
REGION="europe-west3"
gcloud scheduler jobs list --project="$PROJECT" --location="$REGION" \
  --format="table(name,schedule,timeZone,state)"
```

**G01.12 result:** `EXECUTED — PASS` for region-scoped inventory. **VisionIQ Scheduler creation and scheduling are PENDING.** The other project's enabled job must remain untouched.

### Earlier cloud access email — permission follow-up decision (not sent)

The previously sent access request asked about Scheduler Admin, permission to continue using the VPC/`vpc-con`, service-account IAM bindings and Cloud IAM management. G01 now proves that the Scheduler API is usable for reading in `europe-west3`, the existing VisionIQ runtime and migration service accounts exist, and the original private Cloud Run services exist. **None of these facts proves permission to create Scheduler jobs, edit IAM bindings, impersonate service accounts, or deploy new revisions.**

**Do not resend the unchanged broad request yet.** In particular, `roles/resourcemanager.projectIamAdmin` and `roles/iam.serviceAccountAdmin` are broader than simply deploying the reviewed VisionIQ services. Confirm the status of the earlier ticket/email and obtain a narrowly scoped approval for the **specific blocked deployment action(s)**, which may include `cloudscheduler.jobs.create`, `iam.serviceAccounts.actAs` on a dedicated service account, and approved IAM bindings on the corresponding resources. Do not make a sacrificial cloud write solely to test authorization, and do not request `cloudscheduler.admin` just because G01.12b failed.

**Permission status:** `cloudscheduler.jobs.list` observed working; create/update, service-account policy editing, VPC deployment rights and secret-access grants remain **UNVERIFIED**. Record any future `PERMISSION_DENIED` error with its attempted command (redacted) before requesting elevated permissions.

**G01 review:** G01.1–G01.12 inventory checks are now **complete for the target region** (including correctly recording that the one listed Scheduler job belongs to a different project). Inventory success is not deployment readiness: SQL tables, migration head, secret versions/IAM, HTTP readiness, secure ingress, Pub/Sub and E2E operation require separate G02–G11 validation.

**Next journal sections (add exact commands immediately before executing each stage):** G02 cloud-hosted build; G03 Cloud SQL backup/migrations/schema+role verification; G04 Secret Manager and service-specific variables; G05 API/Intelligence revisions; G06 Pub/Sub; G07 LEARN jobs; G08 secure ADO webhook ingress; G09 BRIEF; G10 inactive DEFECT staging; G11 monitoring/rollback.

## Deployment readiness and cloud-only acceptance gates

**Work in order and record actual outcomes in this guide.** No project mutation has been performed as part of this documentation revision.

| Gate | Cloud work | Acceptance evidence |
| --- | --- | --- |
| G01 | Inventory existing FND-03/FND-04 resources; do not duplicate | Cloud Run revisions, service accounts, jobs, Artifact Registry, VPC, Cloud SQL and secret names |
| G02 | Approved cloud-hosted build of reviewed `main` commit | Source SHA, green build/tests, pinned Artifact Registry image digests; no local Docker |
| G03 | Backup, Cloud SQL migration and restricted roles | Recoverable backup/PITR, exactly one Alembic head `20261112_0026`, expected tables/constraints, pgvector, role-grant/denial checks |
| G04 | Runtime config and Secret Manager | Per-service/job variable maps, least-privilege IAM, numeric secret version references, private network/TLS |
| G05 | Private API/Intelligence deployment | Authenticated `/health` + `/ready`, direct DB connection evidence, revision digest, rollback procedure |
| G06 | Pub/Sub provisioning | Topology-derived topics/subscriptions/DLQs, authenticated push, publisher/subscriber IAM, deduplication, retries |
| G07 | LEARN cloud execution | Approved ADO/Confluence canonical ingestion to DB/GCS, Vertex embeddings, published snapshot, cited retrieval, lessons |
| G08 | Ingestor webhook deployment and approved ingress | Auth failures return 401, approved event deliveries update canonical evidence once, secure retries and source re-fetch |
| G09 | BRIEF pickup and cloud E2E | Scoped approved pickup policy, pilot ticket allowlist, Gemini brief, Pub/Sub round trip, `record`-only publication |
| G10 | DEFECT infrastructure staged safely | DEFECT schemas/code deployed while `DEFECT_PIPELINE_ENABLED=false`; Claude and sandbox deferred |
| G11 | Scheduling/operations | Approved worker CPU or job/scheduler plan, alerts, dead-letter monitoring, costs, backups, rollback |

### Two independent Azure DevOps webhook authentication contracts

**Do not merge these variables or reuse one secret for both receivers.** These are two different implementations with different HTTP paths and Basic authentication rules.

| Variable | Current code consumer | Meaning / cloud action |
| --- | --- | --- |
| `ADO_WEBHOOK_USERNAME` | `src/VisionIQ.Ingestor.Cli/WebhookHost.cs` / `WebhookCredentials` | Ingestor webhook HTTP Basic username (required; must not contain a colon) |
| `ADO_WEBHOOK_SECRET` | Ingestor webhook service | **Separate Secret Manager secret**, at least **32 characters**, username+password validated |
| `PICKUP_POLICY_FILE` | `src/VisionIQ.Api/Features/Pickup/PickupOptions.cs` | File path in the **running API container**, *not* a Secret Manager credential |
| `PICKUP_WEBHOOK_SECRET` | API `PickupWebhookEndpoints.cs` | **Different Secret Manager secret**, at least **24 characters**; code currently validates the Basic password, **not** its username |
| `PICKUP_ALLOWED_WORK_ITEM_IDS` | API pickup policy | Strict first-pilot item allowlist; unset means **no ID restriction**, not deny-all |
| `ADO_READ_PAT` | Outbound source readers | Read-only PAT for API/Ingester ADO fetch calls; **not** either webhook Basic secret |

**Ingestor LRN-05 / LRN-12:**
- The dedicated Ingestor image/service implements `POST /webhooks/ado/work-items` and `POST /webhooks/ado/pull-requests`. It reads approved source item IDs/PR events, re-fetches authoritative data from ADO and updates canonical PostgreSQL/GCS evidence.
- The existing `scripts/manual-deploy/02-deploy-services.ps1` does **not** deploy this third webhook image. Implement a separately approved service deployment and ingress.
- Required runtime dependencies include `INGESTION_OUTPUT_MODE=canonical`, `CHECKPOINT_STORE=postgres`, scoped ADO organization/projects, `ADO_READ_PAT`, private Cloud SQL and GCS archive.

**API BRIEF pickup / feedback:**
- The API handles `POST /webhooks/ado` when a valid pickup policy is configured. It processes approved ticket transitions, feedback and relevant PR callbacks according to feature controls.
- **The policy template already exists in the repository:** `config/pickup-policy.example.json`. It contains **example placeholders** (`https://dev.azure.com/your-org/`, `VisionIQ-Demo`, `demo-repo`, sample SHA), so its presence in GitHub does not mean a valid operational policy exists.
- The **current `src/VisionIQ.Api/Dockerfile` does not copy `config/`** to `/app/config`. A Cloud Run service cannot read the repository's GitHub file directly via `PICKUP_POLICY_FILE`. Before activation, create/approve a real scoped policy derived from that template, package it in the image or use an approved mounted configuration file, set the actual container path, and verify the file is readable.
- `PICKUP_POLICY_FILE` blank disables pickup. A nonblank path without a readable valid policy or a strong `PICKUP_WEBHOOK_SECRET` can prevent startup. Never enable the template policy against real ADO tickets.
- Set `PICKUP_SWEEP_INTERVAL_MINUTES=0` for controlled initial operation and specify pilot `PICKUP_ALLOWED_WORK_ITEM_IDS`.

**External ingress and ADO permissions:**
1. Keep `visioniq-api` and `visioniq-intelligence` private. ADO Service Hooks cannot directly produce Google IAM invocation tokens. Select a reviewed HTTPS ingress, filtering, abuse-protection and forwarding architecture.
2. Use independently authenticated ADO service hook subscriptions for canonical work-item/PR changes versus pickup/feedback. Check duplicate delivery behavior and role access.
3. Store Ingestor and Pickup webhook passwords under **distinct Secret Manager entries**, grant only the corresponding service account `secretAccessor`, and rotate any previously exposed production-like values.
4. ADO webhook subscription creation is a **write** to external Azure DevOps. Require explicit organizational approval before registration. Until then, test with permitted cloud-hosted synthetic events to the approved ingress.
5. ADO PR updates and BRIEF pickup are separate event sources; avoid treating a Pub/Sub delivery as if it were an externally received ADO Service Hook.
6. Feature details: `docs/OPERATIONS-AND-INFRASTRUCTURE/LRN12-WEBHOOK-CLOUD-DEPLOYMENT.md` and `docs/features/BRF-01-ticket-pickup/README.md`.

### Cloud Run, Pub/Sub, Vertex and feature activation

- **Pub/Sub:** source topics, push subscriptions, dead-letter topics and routing come from `contracts/events/topology.json`. The `PUBSUB_TOPIC_*` placeholders in `.env.example` are *not* runtime overrides today. Run the existing `scripts/pubsub/provision-pubsub.ps1` via an approved **cloud-side** execution route, only after previewing planned commands and service identities. Set `PUBSUB_PROJECT_ID=gbr-mts-wgh-hkt-dev`; leave `PUBSUB_EMULATOR_HOST` unset in GCP.
- **Cloud Run workers:** minimum instances 0 and request-based CPU do **not** imply continuous in-process polling. Use an explicitly reviewed instance-based CPU/min-instances policy or dedicated Jobs/Scheduler for finite work; verify event processing, delays, scaling and cost before enabling dispatch.
- **Secrets/config:** never upload populated `.env` to Cloud Run. Use immutable per-service configuration and pinned Secret Manager references. Audit `--set-env-vars` / `--set-secrets` in existing deployment scripts: set operations can replace existing variable/secret mappings; plan idempotent updates and preserve unrelated configuration.
- **LEARN:** schedule/run canonical ingestion, indexing, published snapshots and lesson consolidation using cloud-executed workloads. Confirm GCS archive IAM and Vertex model/region/embedding dimension, and use only approved evidence scopes.
- **BRIEF:** `LLM_MODE=live` with Gemini on Vertex AI; initial `BRIEF_PUBLISH_MODE=record` avoids real ADO comments. Enable only after cloud event/DB acceptance and approved pickup.
- **DEFECT:** deployment of source, migrations and inactive endpoints is not live AI fixing. Code currently routes `REPRODUCTION_TEST` and `CODE_FIX` exclusively to Claude; `CLAUDE_ROUTE=off` and `DEFECT_PIPELINE_ENABLED=false` for this release. Cloud sandbox and live-Claude integration are separate gates.
- **Graph HTTP:** `GraphApiOptions.FromConfiguration()` currently disables the Graph endpoint. `VISIONIQ_AUTH_TENANT_ID`, `VISIONIQ_AUTH_AUDIENCES`, `VISIONIQ_AUTH_ALLOWED_CALLERS` and `VISIONIQ_AUTH_SCOPE` are not required for that disabled endpoint; do not mistake them for Cloud Run IAM.
- **Cloud SQL:** Existing cloud migration tooling `db/cloud_manage.py` and migration job must be inspected before re-execution. Do not combine all Alembic files or auto-migrate during API startup. Verify actual roles, `workflow`/`evidence`/`knowledge` schemas and pgvector after a guarded cloud migration.

**Proposed safe first cloud phase:** inspect resources → validate cloud builds/database → deploy private services and Pub/Sub → run LEARN and Gemini BRIEF acceptance with **record-only** ADO output. Defer DEFECT live runner, Claude model calls and any unapproved ADO writes. Mark a gate verified only with sanitized, dated live cloud evidence.

## Operational cautions and decisions to verify

- **Private networking:** Public IP is disabled. A developer connected through Zscaler is not automatically able to reach VPC private address 10.102.16.8. Verify an approved private route, or run migrations and smoke tests from an authorized Google Cloud workload.
- **Cloud configuration separation:** Do not change the working local PostgreSQL data volume or use docker compose down -v. Keep Cloud SQL migrations explicit and separate from normal docker compose up.
- **TLS and credentials:** SSL-only access is enabled and IAM database authentication is on, but neither confirms how each app will authenticate. Choose the runtime authentication design and secret delivery before deployment.
- **Certificate rotation:** Automatic server certificate rotation is reported disabled; add an operational review well before 29 Oct 2027 and confirm rotation policy with the cloud administrator.
- **Region and data handling:** Cloud SQL is in Frankfurt (europe-west3), with backups in EU multi-region. Confirm this placement is authorized for any engineering tickets, code and documentation ingested later.
- **Feature toggles:** Vertex AI integration and Knowledge Catalog integration being enabled on Cloud SQL do not prove VisionIQ application integration is implemented or working.

## Change log

| Date | Change | Evidence / validation |
| --- | --- | --- |
| 12 Nov 2026 | Added exact G01.12a Scheduler command to master G01 list; preserved G01.12a real output and G01.12b failed command/error (twice) in historical execution log; captured access-email follow-up decision | User-provided Cloud Shell output and ownership clarification; no GCP changes |
| 12 Nov 2026 | Recorded G01.1–G01.11 Cloud Shell infrastructure evidence; found original services/DB/jobs/registry/roles, identified missing VisionIQ Pub/Sub resources; added G01.12 Cloud Scheduler inventory | User-supplied Cloud Shell transcript, summarized without other team/personal identifiers; no cloud changes |
| 29 Oct 2026 | Initial Cloud SQL configuration captured from supplied settings | Team-provided configuration |
| 29 Oct 2026 | Confirmed configuration against four Console screenshots; documented two resource labels without personal identifiers; confirmed manual provisioning, no Terraform | Screenshot visual review; no API connectivity or migration claimed |

## Document ownership and update rule

Update this single guide **on the documentation branch/draft PR until reviewed and merged** after every material Google Cloud change (Cloud SQL, VPC, IAM, secrets, Cloud Run, Pub/Sub, database migrations, ingress/webhooks, monitoring and deployment). For each change, record the date, resource, actual configuration, reason, command or Console path, evidence, **tested/pending** status and any rollback considerations. Never claim a change is verified based only on configuration screenshots. Keep one-time laptop setup and daily local Docker instructions in their separate guides.
