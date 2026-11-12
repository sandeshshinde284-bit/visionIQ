# VisionIQ — Google Cloud Setup and Deployment Runbook

**Living team runbook · Revision 1.4 · 12 November 2026**  
**Repository path:** `docs/Functional/VisionIQ_Google_Cloud_Setup_Deployment_Guide.md`

This is a **reusable, end-to-end deployment procedure** for any authorized VisionIQ engineer. It covers both **automated CI/CD** and **manual cloud-side deployment**, using the **same code, image build definition, infrastructure, configuration and acceptance gates**. It is not an account-access discussion or a transcript of troubleshooting conversations.

## 1. Deployment environment and prerequisites

| Resource | Development deployment |
| --- | --- |
| GCP project | `gbr-mts-wgh-hkt-dev` |
| Region | `europe-west3` |
| Artifact Registry | `team-visioniq-docker-dev` |
| Cloud SQL | `visioniqdev` / PostgreSQL 16 / private IP `10.102.16.8:5432` |
| Logical database | `visioniq_dev` |
| VPC access connector | `vpc-con` |
| API Cloud Run service | `visioniq-api`, container port `5100` |
| Intelligence Cloud Run service | `visioniq-intelligence`, container port `8000` |
| Migration Cloud Run Job | `visioniq-db-migrate` |
| Existing runtime identities | `visioniq-runtime`, `visioniq-db-migrate` |
| Source branch | Reviewed `main` commit, pinned by SHA |
| Cloud execution | Cloud Build / approved hosted runner + Google Cloud Shell; **no laptop Docker or local PostgreSQL required** |

These are the currently known development resource names, not evidence that every service or feature is working. Never recreate an existing shared-project resource without checking its identity and ownership. Verify corporate approvals for network, source-data scope, deployment IAM and outbound API access.

**Required before a write:** source tests passing; resource permissions; selected deployment path; Secret Manager references; migration/rollback plan; approved target project; and a documented operator/release approval. Never paste credential values or full environment dumps into a shared runbook.

## 2. Choose the deployment path

Both paths are permanent, supported procedures. **Automated is preferred once approved repository integration and CI/CD permissions are available; otherwise use the manual cloud-side path.** Do not block a permitted manual release merely because GitHub-to-GCP automation is unavailable.

| Step | Path A — automated | Path B — manual cloud-side |
| --- | --- | --- |
| Repository source | GitHub connection via approved Cloud Build/Developer Connect integration | Approved source checkout in Google Cloud Shell or hosted runner, pinned to reviewed commit |
| Tests | CI on PR and approved main commit | Explicitly execute the **same test gates** on a cloud-hosted runner |
| Docker image build | Cloud Build repository trigger | `gcloud builds submit` using the **same reviewed build config** |
| Artifact publishing | Cloud Build pushes images on successful build | Cloud Build pushes identical images on successful operator-triggered build |
| Cloud SQL migration | Separate approved pipeline stage/job (not API startup) | Explicitly approved Cloud Run migration job |
| Cloud Run deployment | Gated CD job updates existing services | Explicit `gcloud run deploy` updates existing services |
| Verification and rollback | CI/CD smoke tests, observability and rollback gate | Same smoke checks and rollback commands manually invoked |
| Prerequisite | Repository integration, trigger and deploy permissions | Cloud Build submit + registry permissions, source acquisition, Cloud Run deploy permissions |

**Fallback decision rule:** If Path A's repository connection or trigger is missing, choose Path B **only if manual cloud build and deploy permissions are actually available**. An enabled Cloud Build API does not establish build-submit rights. If both paths are blocked, stop and record the specific blocked permission or source access; do not bypass IAM or deploy from an unreviewed machine.

### One shared release contract for both paths

1. Pin the approved full Git commit SHA (not `latest`).
2. Run the same Python lint/tests, .NET tests, four Docker image builds and isolated PostgreSQL integration/migration tests in approved hosted CI. Never run destructive test fixtures in the shared `visioniq_dev` database.
3. Build from the **repository root**, using the four existing Dockerfiles and `linux/amd64` target architecture.
4. Publish images to `europe-west3-docker.pkg.dev/gbr-mts-wgh-hkt-dev/team-visioniq-docker-dev/` using SHA tags; record immutable `@sha256:` digests.
5. Make schema changes through the separately approved migration job, not at API startup.
6. Deploy/update only the intended Cloud Run services and jobs; keep API and Intelligence ingress private and IAM-authenticated.
7. Validate authenticated readiness, real SQL role access, Pub/Sub delivery and feature E2E; preserve the previous Cloud Run revision/image for rollback.
8. Add the command and expected outcome to this guide **before** execution; record verified results afterwards. Never turn an unexecuted proposal into PASS.

## 3. G01 — Verify existing cloud resources

**Execution location:** Google Cloud Shell **Bash**. **Effect:** read-only inventory.

```bash
PROJECT="gbr-mts-wgh-hkt-dev"
REGION="europe-west3"

echo "===== G01.1 GCP project ====="
gcloud config get-value project

echo "===== G01.2 Existing Cloud Run services ====="
gcloud run services list --project="$PROJECT" --region="$REGION"

echo "===== G01.3 Current service revisions ====="
for SERVICE in visioniq-api visioniq-intelligence; do
  echo "--- $SERVICE ---"
  gcloud run services describe "$SERVICE" --project="$PROJECT" --region="$REGION" \
    --format="yaml(metadata.name,status.latestCreatedRevisionName,status.latestReadyRevisionName)"
done

echo "===== G01.4 Cloud Run jobs ====="
gcloud run jobs list --project="$PROJECT" --region="$REGION"

echo "===== G01.5 Artifact Registry ====="
gcloud artifacts repositories list --project="$PROJECT" --location="$REGION"

echo "===== G01.6 Cloud SQL instance ====="
gcloud sql instances describe visioniqdev --project="$PROJECT" \
  --format="yaml(name,state,region,databaseVersion,connectionName)"

echo "===== G01.7 Logical databases ====="
gcloud sql databases list --instance=visioniqdev --project="$PROJECT"

echo "===== G01.8 VisionIQ Pub/Sub topic names ====="
gcloud pubsub topics list --project="$PROJECT" --format="value(name)" \
  | grep '/visioniq-' || true

echo "===== G01.9 VisionIQ Pub/Sub subscription names ====="
gcloud pubsub subscriptions list --project="$PROJECT" --format="value(name)" \
  | grep '/visioniq-' || true

echo "===== G01.10 Secret Manager names (not secret versions/payloads) ====="
gcloud secrets list --project="$PROJECT" --format="value(name)" \
  | grep '^visioniq-' || true

echo "===== G01.11 VisionIQ service accounts ====="
gcloud iam service-accounts list --project="$PROJECT" --format="value(email)" \
  | grep 'visioniq-' || true

echo "===== G01.12 Cloud Scheduler jobs in intended region ====="
gcloud scheduler jobs list --project="$PROJECT" --location="$REGION" \
  --format="table(name,schedule,timeZone,state)"
```

**PASS criteria:** required existing resources identified, Cloud SQL `RUNNABLE`, project and region correct; missing VisionIQ topics/subscriptions/Scheduler jobs recorded as provisioning tasks. Listing **does not** prove IAM rights to mutate resources or application health. Cloud Scheduler requires `--location`. Record only VisionIQ results even when the listing contains other shared-project resources.

**Development inventory baseline (observed 12 Nov 2026):**

| Item | Verified metadata | Follow-up |
| --- | --- | --- |
| API / Intelligence | `visioniq-api-00001-9gm`, `visioniq-intelligence-00001-zfn` latest ready | New source and SQL readiness not verified |
| Cloud SQL | `visioniqdev` RUNNABLE; `visioniq_dev` exists | Check migrations, tables, role grants and connections |
| Migration job | `visioniq-db-migrate` exists, last listed run 30 Oct 2026 | Newer migrations pending verification |
| Artifact Registry | `team-visioniq-docker-dev` exists | Check image digests |
| Pub/Sub | No VisionIQ application topics or subscriptions listed | Provision at G06 |
| Service accounts / secrets | VisionIQ runtime/migration accounts and DB secret names exist | Check versions, policy and least-privilege IAM |
| Scheduler | No VisionIQ job identified in target region | Configure dedicated VisionIQ schedules at G11 |

## 4. G02 — Source validation, Cloud Build and Artifact Registry

**Build scope — four repository-root Dockerfiles:**

| Image | Dockerfile |
| --- | --- |
| `visioniq-api` | `src/VisionIQ.Api/Dockerfile` |
| `visioniq-intelligence` | `src/VisionIQ.Intelligence/Dockerfile` |
| `visioniq-ingestor-webhooks` | `src/VisionIQ.Ingestor.Cli/Dockerfile` |
| `visioniq-db-migrate` | `db/Dockerfile` |

### G02.1–G02.6 — Read-only checks for both paths

Run in **Google Cloud Shell Bash**. These commands do not build or deploy.

```bash
PROJECT="gbr-mts-wgh-hkt-dev"
REGION="europe-west3"
REPOSITORY="team-visioniq-docker-dev"
REGISTRY="$REGION-docker.pkg.dev/$PROJECT/$REPOSITORY"

echo "===== G02.1 Cloud Build and registry APIs ====="
gcloud services list --enabled --project="$PROJECT" \
  --format="value(config.name)" \
  | grep -E '^(cloudbuild.googleapis.com|artifactregistry.googleapis.com)$' || true

echo "===== G02.2 Regional Cloud Build runs ====="
gcloud builds list --project="$PROJECT" --region="$REGION" --limit=15 \
  --format="table(id,status,createTime)"

echo "===== G02.3 Regional Cloud Build triggers ====="
gcloud builds triggers list --project="$PROJECT" --region="$REGION" \
  --format="table(name,disabled)"

echo "===== G02.4 Regional repository connections ====="
gcloud builds connections list --project="$PROJECT" --region="$REGION" \
  --format="value(name)"

echo "===== G02.5 VisionIQ registry images ====="
for IMAGE in visioniq-api visioniq-intelligence visioniq-ingestor-webhooks visioniq-db-migrate; do
  echo "--- $IMAGE ---"
  gcloud artifacts docker images list "$REGISTRY/$IMAGE" --project="$PROJECT" \
    --include-tags --limit=15 --format="table(package,version,tags)"
done

echo "===== G02.6 GitHub main revision ====="
git ls-remote --heads https://github.com/sandeshshinde284-bit/visionIQ.git main
```

**Current evidence (12 Nov 2026):** G02.1 **PASS** — `cloudbuild.googleapis.com` and `artifactregistry.googleapis.com` enabled. **G02.2–G02.6 are pending actual output.** An enabled API does not confirm `cloudbuild.builds.create` or registry publish privileges.

### G02-A — Automated build and gated deployment

Use this branch when repository integration and CI/CD approvals are available.

1. Enable or verify the approved **GitHub → Cloud Build** repository connection (Cloud Build repository integration or Developer Connect) in the required region. Verify repository identity and source permissions; do not assume Developer Connect is the only supported integration.
2. Maintain **one reviewed build configuration** in the code repository (proposed path: `cloudbuild.yaml`; **not currently present/approved**). It must build all four images, publish immutable versions, and run the relevant validation gates before allowing a release.
3. Configure CI triggers for approved pull requests and protected `main` updates. Block deployment on failing tests. Protect against untrusted PRs executing privileged build/deploy steps.
4. For production-like changes, use an explicit approval gate for migrations, changes to IAM/secrets, and rollout. A successful CI build alone must **not** automatically migrate the database or write to ADO.
5. Configure CD to deploy reviewed image **digests** to existing services, verify health/SQL/event delivery and preserve a rollback revision.
6. Document trigger/connection resource name, configuration commit, identities, test results, produced digests and rollback in §15.

**Verification:** A commit generates a green build, four matching image digests, authorized release approval and successful post-deploy checks. If source integration or permission is missing, leave G02-A **BLOCKED/NOT CONFIGURED** and use G02-B when authorized.

### G02-B — Manual cloud-side build and deployment fallback

Use this branch when automatic repo integration is not available but **manual Cloud Build submit is permitted**.

1. In Cloud Shell, obtain source from a **trusted approved checkout**, authenticate Git if necessary and confirm the exact full source SHA; never place PATs or credentials in the command history. Do not assume a private GitHub repository is directly reachable by Cloud Build.
2. Verify all four Dockerfiles and the reviewed build configuration use the same source/test/image contract as G02-A. If the common build configuration is not yet implemented, implement/review it before building rather than inventing an ad hoc, untested substitute.
3. Execute the approved CI test suite on a cloud-hosted runner (Python lint/tests, .NET 10 tests and isolated PostgreSQL migration/integration checks). Record results.
4. **After build configuration and IAM are approved**, operator-initiated build command pattern:

```bash
PROJECT="gbr-mts-wgh-hkt-dev"
REGION="europe-west3"

# Run from the approved repository checkout containing the reviewed build config.
SOURCE_SHA="$(git rev-parse HEAD)"
git status --short
echo "Reviewed source: $SOURCE_SHA"

# TEMPLATE: execute only AFTER cloudbuild.yaml has been created/reviewed and
# the exact commit, IAM identity, registry target and tests are approved.
gcloud builds submit . --project="$PROJECT" --region="$REGION" \
  --config=cloudbuild.yaml \
  --substitutions="_SOURCE_SHA=$SOURCE_SHA"
```

5. Inspect successful build logs; capture four artifact **digests**, do not rely solely on image tags. Proceed through G03–G05 using the same release gates as G02-A. Manual deployment updates **existing** Cloud Run services; it is not the Cloud Run **Create service → continuously deploy** wizard.
6. If `gcloud builds submit` fails because of IAM or organizational restrictions, stop and use another **approved cloud-hosted** builder only after permission review. Do not silently switch to a laptop build.

**Troubleshooting for both paths:** separate `PERMISSION_DENIED` (IAM), repository fetch failure (Git/source access), build failure (Dockerfile or dependencies), image push failure (registry IAM), and test failure (code/fixture). Never mask a failed step as PASS.

## 5. G03 — Cloud SQL migration and schema validation

**Purpose:** Bring existing Cloud SQL to the reviewed source's single Alembic head and validate DB role isolation. `db/cloud_manage.py` is the guarded cloud migration path; normal application startup must not run DDL.

1. Verify instance/database, current migration job image/command, migration head and required role grants. Capture backup/PITR state and an approved restore procedure.
2. Review every pending migration before applying. At the 12 Nov reviewed source, `20261112_0026` is the merged Alembic head in Git; this is **not proof that Cloud SQL has applied it**.
3. **G03-A:** trigger a separately approved migration stage in the CD pipeline after backup and release approval. **G03-B:** manually execute the approved `visioniq-db-migrate` Cloud Run Job with the reviewed image and cloud migration command. Never run the old local DB command against Cloud SQL.
4. Check `alembic_version`, `workflow`/`evidence`/`knowledge` schemas, expected tables/indexes/constraints/triggers, `vector` extension, runtime role grants and denied writes outside each role's scope.
5. Document exact job update/execute/read-only SQL verification commands and rollback approach **before** the first cloud migration. Do not run migrations on the shared DB as a testing shortcut.

**Read-only preflight (Google Cloud Shell Bash):**

```bash
PROJECT="gbr-mts-wgh-hkt-dev"
REGION="europe-west3"
gcloud sql instances describe visioniqdev --project="$PROJECT" \
  --format="yaml(name,state,region,databaseVersion,connectionName)"
gcloud sql databases list --instance=visioniqdev --project="$PROJECT"
gcloud run jobs describe visioniq-db-migrate --project="$PROJECT" --region="$REGION" \
  --format="yaml(metadata.name,status.conditions)"
gcloud run jobs executions list --job=visioniq-db-migrate \
  --project="$PROJECT" --region="$REGION" --limit=10
```

**G03 exit:** successful reviewed migration and verified SQL schema/grants, not merely a zero-exit job. **Rollback:** restore/roll forward using a compatible backup plan; do not assume arbitrary Alembic downgrade is safe.

## 6. G04 — Per-service Cloud Run configuration and Secret Manager

- `.env.example` is the shared **key-name contract**, not a deployable cloud settings file. Developers' `.env` files are not uploaded to GCP.
- Store DB passwords, ADO/Confluence credentials, and webhook secrets as **separate Secret Manager entries**; pin versions and grant each service account only needed access.
- Configure API, Intelligence, Ingestor, migration and ingestion/indexing jobs independently. Verify actual service account, network, model region and DB role on each.
- **Do not use `--set-env-vars` or `--set-secrets` on an existing service without checking the complete intended map**: those options can replace unrelated settings. Prefer reviewed declarative config or safe incremental updates and verify metadata afterwards.
- If automated deployment is enabled, configuration must be supplied through approved CI/CD variable/secret controls. If manual, use equivalent reviewed CLI configuration. **Secrets must never enter build logs.**
- Do not enable pickup or background workers until the policy, durable data flow and workload execution model are validated.

**Separate webhook settings (not interchangeable):**

| Receiver | Key | Configuration requirement |
| --- | --- | --- |
| Ingestor source-change webhooks | `ADO_WEBHOOK_USERNAME`, `ADO_WEBHOOK_SECRET` | HTTP Basic username + distinct password (minimum 32 chars) |
| API BRIEF pickup webhook | `PICKUP_WEBHOOK_SECRET` | Distinct HTTP Basic password (minimum 24 chars) |
| API BRIEF policy | `PICKUP_POLICY_FILE` | A **file path inside the container**, not a GitHub URL or Secret Manager value |
| Both source readers | `ADO_READ_PAT` | Outbound read-only ADO credential, not a webhook Basic-auth secret |

The template `config/pickup-policy.example.json` **already exists**, but contains sample organization/project/repository details. The current API Dockerfile does not copy `config/` into the runtime image. Package an approved scoped policy or mount it at an approved path before setting `PICKUP_POLICY_FILE`. Keep a pilot `PICKUP_ALLOWED_WORK_ITEM_IDS` allowlist and `PICKUP_SWEEP_INTERVAL_MINUTES=0` until intentional polling is approved.

## 7. G05 — Deploy and validate existing private Cloud Run services

**G05-A automated:** gated CD deploys the approved immutable image digests to existing `visioniq-api` and `visioniq-intelligence`. **G05-B manual:** operator invokes reviewed `gcloud run deploy` commands using those same digests after G03/G04 sign-off.

1. Record the existing ready revision and image digests for rollback.
2. Review API port 5100, Intelligence port 8000, internal ingress, authentication, `vpc-con`, Cloud SQL private IP/TLS and service-account identity.
3. Deploy **in place** (do not create duplicate services). Do not overwrite unrelated runtime settings by accident.
4. Verify latest ready revisions, authenticated `/health` and **database-backed** `/ready`, role-scoped SQL access, permitted service-to-service calls and logs.
5. On failure, restore traffic to the previous compatible revision and follow the documented DB-compatible recovery plan.

**Exact commands for the mutation must be recorded once approved image digests and final per-service configuration are known.** A generic `gcloud run deploy` command with guessed secrets is not a safe repeatable release command.

## 8. G06 — Pub/Sub topics, authenticated subscriptions, retries and DLQ

**Source of truth:** `contracts/events/topology.json`, with current events:

| Event | Topic | Subscription / destination |
| --- | --- | --- |
| `analysis.requested` | `visioniq-analysis-requested-v1` | `visioniq-intelligence-analysis-requested-v1` → Intelligence |
| `analysis.completed` | `visioniq-analysis-completed-v1` | `visioniq-api-analysis-completed-v1` → API |

Create the corresponding dead-letter topics and hold subscriptions, authenticated push invoker identity, publisher grants, retry policy, event deduplication and DLQ monitoring. The push path is `/events/pubsub`.

**Automated:** approved infrastructure provisioning stage using the topology and scoped IAM. **Manual:** review the same topology and the plan-only `scripts/pubsub/provision-pubsub.ps1`; perform equivalent approved cloud commands, recording every mutation. Do not provision until private target URLs and invoker permissions are known. Do not change other teams' Pub/Sub resources.

**PASS:** a controlled synthetic request is published, consumed once logically despite possible duplicate deliveries, completed and acknowledged; deliberate failure follows configured retry and DLQ behavior.

## 9. G07 — LEARN cloud ingestion, embeddings and lesson consolidation

**Automated:** scheduled/on-demand Cloud Run Jobs execute an approved workflow. **Manual:** operator runs the same reviewed Cloud Run Jobs in order. Both must use canonical Cloud SQL/GCS evidence, scoped ADO/Confluence source access and Vertex AI; no local fixture database.

1. Execute canonical ADO/Confluence ingestion to PostgreSQL and GCS and validate source revision counts, ACL boundaries and checkpoints.
2. Build/refresh embeddings (`EMBEDDING_PROVIDER=vertex`), pgvector index and published knowledge snapshot.
3. Validate source-linked retrieval, cross-project visibility and evidence citations.
4. Run lesson consolidation only through the approved cloud execution route; verify idempotency and recovery.
5. Add exact Cloud Run Job definitions, run commands, expected output and schedule triggers **when each cloud execution job is reviewed and created**.

## 10. G08 — ADO webhook ingress (two distinct endpoints)

| Host | Paths | Auth |
| --- | --- | --- |
| Ingestor webhook Cloud Run | `POST /webhooks/ado/work-items`, `POST /webhooks/ado/pull-requests` | `ADO_WEBHOOK_USERNAME` + `ADO_WEBHOOK_SECRET` (32+ chars) |
| API BRIEF pickup | `POST /webhooks/ado` | `PICKUP_WEBHOOK_SECRET` (24+ chars; current implementation validates password) |

ADO service hooks originate externally and cannot directly produce Google IAM ID tokens for private Cloud Run. Design an **approved HTTPS ingress/gateway** with authentication, abuse protection and narrowly authorized forwarding; keep API and Intelligence private. Build/deploy the dedicated Ingestor image as a separate service after approval. Distinguish webhook authentication from Pub/Sub push authentication.

**Automated:** approved webhook ingress and subscription provisioning. **Manual:** same explicit approved configuration and endpoint tests. Verify 401 for invalid Basic auth, valid test event canonical evidence update, duplicate idempotency, source re-fetch and retryable failures; register live ADO subscriptions only with authorization.

## 11. G09 — BRIEF end-to-end verification

Set `LLM_MODE=live`, Gemini on Vertex, and `BRIEF_PUBLISH_MODE=record` initially; approved scoped policy and item allowlist required for pickup. Confirm the event round trip:

```text
ADO test event → API pickup → DB outbox → Pub/Sub analysis.requested
→ Intelligence + Gemini → Pub/Sub analysis.completed → API brief record
```

Run the identical acceptance tests whether deployed via automated CD or manually. Real ADO comment publication is a separately approved decision.

## 12. G10 — DEFECT staging, no live execution until validated

Include the DEFECT schema and code in reviewed releases, but initially keep `DEFECT_PIPELINE_ENABLED=false`, `CLAUDE_ROUTE=off`, and `FIX_PR_MODE=record`. Existing `REPRODUCTION_TEST` and `CODE_FIX` model routes currently require Claude; the Cloud Run Intelligence image does not provide the Docker sandbox needed for live fix execution. **A deployed image is not a passing DEFECT E2E test.**

The same feature gate applies to automated and manual deployments. Enable live DEFECT only as a separate reviewed release after model and isolated runner tests are complete.

## 13. G11 — Scheduler, operations, monitoring and rollback

- Set up dedicated VisionIQ Cloud Scheduler entries only after their target Cloud Run jobs/services are deployed and verified. Do not reuse any unrelated job in the shared project.
- Validate event-driven workers under Cloud Run CPU/min-instance settings; a scale-to-zero request-based instance does **not** guarantee continuous background polling. Prefer jobs and event-driven workloads where appropriate.
- Verify Cloud Logging/Monitoring, DLQ alerting, Cloud SQL backup/PITR, secret rotation, cost limits, idempotent event replay and documented rollback.
- Automated path uses approved scheduling/deployment gates; manual path runs equivalent jobs and checks explicitly. If Scheduler create permission is unavailable, leave schedules PENDING, use approved on-demand job execution where suitable, and do not silently skip required recurring functionality.

## 14. Operational release record

Update this concise, **VisionIQ-only** table after each meaningful action. Keep verbose logs in an approved restricted archive; do not paste unrelated shared-project inventory or personal support history.

| UTC date | Release SHA / gate | Action / command | Status | Validation evidence | Rollback or next step |
| --- | --- | --- | --- | --- | --- |
| 2026-11-12 | G01 | Read-only infrastructure inventory | PASS — metadata only | Original VisionIQ Run services/job, SQL instance/database, registry, identities identified | Application/DB acceptance still pending |
| 2026-11-12 | G02.1 | Enabled Cloud Build and Artifact Registry APIs | PASS | Both APIs listed enabled | G02.2–G02.6, source access and build-submit IAM pending |
| — | G02.2–G02.6 | Read-only build path checks | PENDING | Not yet supplied | Select automated or manual path |
| — | G03–G11 | Cloud migration, release and integrations | PENDING | Acceptance not performed | Follow phase ordering and approvals |

## 15. Source references and change history

- `docs/OPERATIONS-AND-INFRASTRUCTURE/fnd04-manual-deployment.md` — historical manual deployment; local Docker image build is not required by this runbook's **cloud-side manual** route.
- `docs/OPERATIONS-AND-INFRASTRUCTURE/LRN12-WEBHOOK-CLOUD-DEPLOYMENT.md` — canonical webhook host and security boundary.
- `contracts/events/topology.json`, `scripts/pubsub/provision-pubsub.ps1` — event topology and provisioning reference.
- `db/cloud_manage.py`, `db/migrations/` — guarded Cloud SQL migration and schema.
- `config/pickup-policy.example.json` — policy template to be reviewed, **not** an active production policy.
- `.env.example` — canonical safe configuration key contract; secrets are sourced from Secret Manager.

**Google documentation:** [Cloud Build source triggers](https://docs.cloud.google.com/build/docs/automating-builds/create-manage-triggers) · [Manual Cloud Build submissions](https://docs.cloud.google.com/build/docs/manually-build-code-source-repos) · [Cloud Build to Cloud Run](https://docs.cloud.google.com/build/docs/deploying-builds/deploy-cloud-run).

**Documentation rule:** retain this **one** living runbook in the documentation draft PR until review/merge. Append exact commands, verified resource changes, sanitized results and rollback guidance as each stage is approved and executed. Add new sections for generally reusable procedures; avoid conversation-specific or unrelated-resource notes.

---

## Appendix A — Historical Cloud SQL configuration snapshot

**Provenance:** Cloud SQL settings supplied via Console screenshots on 29 October 2026, not a live schema or connectivity test. These remain useful as a baseline; reconfirm settings before major changes.


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
