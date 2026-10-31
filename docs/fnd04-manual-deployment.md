# FND-04 — Manual Docker → Artifact Registry → private Cloud Run (development)

**Tracking:** FND-04 GitHub issue #10. **Source of truth:** reviewed commit from `feature/learn`.
**Follow-on automation:** issue #11, existing draft PR #2. No Cloud Build or Developer Connect is used in this runbook.
**Status:** scripts and instructions prepared. **NOT deployed/accepted until actual cloud evidence is collected.**

## Existing infrastructure — inspect, do not recreate

| Resource | Development configuration documented in FND-03/FND-AUTO |
|---|---|
| Project / region | `gbr-mts-wgh-hkt-dev` / `europe-west3` |
| Artifact Registry | `team-visioniq-docker-dev` (Docker repository) |
| Cloud SQL | `visioniqdev`, database `visioniq_dev`, previously recorded private IP `10.102.16.8` |
| VPC connector | `vpc-con` (must be READY before deploying) |
| Cloud Run runtime service account | `visioniq-runtime@gbr-mts-wgh-hkt-dev.iam.gserviceaccount.com` |
| Migration job | `visioniq-db-migrate`; scope and image MUST be inspected before execution |
| DB application identities | `visioniq_api`, `visioniq_intelligence` |
| Secret Manager | `visioniq-db-api-password`, `visioniq-db-intelligence-password` |

The single runtime Google service account currently has access to both DB secrets.
This is a documented **hackathon compromise**, not ideal least privilege. Corporate
security must approve this explicitly or split runtime service accounts before
FND-04 is accepted. No script creates or widens IAM permissions. Do not modify
unrelated resources in the shared corporate project.

## 1. Prerequisites and read-only checks

Use Windows PowerShell in the VisionIQ Git repository root. Docker Desktop
(engine running), gcloud CLI, Git, a reviewed source revision and *approved
corporate identity* with registry push, Cloud Run deployment, `iam.serviceAccountUser`
on the runtime SA, and read-only discovery permissions are required.

```powershell
$PROJECT = 'gbr-mts-wgh-hkt-dev'
$REGION = 'europe-west3'
$REPOSITORY = 'team-visioniq-docker-dev'

gcloud auth list --filter='status:ACTIVE'
gcloud config get-value project
gcloud artifacts repositories describe $REPOSITORY --project=$PROJECT --location=$REGION
gcloud sql instances describe visioniqdev --project=$PROJECT --format='table(name,connectionName,state)'
gcloud compute networks vpc-access connectors describe vpc-con --project=$PROJECT --region=$REGION
gcloud run services list --project=$PROJECT --region=$REGION
gcloud run jobs describe visioniq-db-migrate --project=$PROJECT --region=$REGION
# Metadata only; never run 'secrets versions access' in shared logs.
gcloud secrets versions list visioniq-db-api-password --project=$PROJECT
gcloud secrets versions list visioniq-db-intelligence-password --project=$PROJECT
```

Cloud Build permissions are **not** required for local Docker build and push.
Artifact Registry Writer, Cloud Run deployment rights and service-account-use
rights remain independently necessary. The commands intentionally reuse
existing resources and do not create duplicate Artifact Registry repositories.

## 2. Local Docker build and manual Artifact Registry push

Select the reviewed source revision on your local machine. Confirm the review
and clean worktree before pushing; never use `latest` as a release tag.

```powershell
git checkout feature/learn
git pull --ff-only
$COMMIT = (git rev-parse HEAD).Trim()
git status --short

# Preview only
.\scripts\manual-deploy\01-build-push.ps1

# Test build locally; no cloud changes
.\scripts\manual-deploy\01-build-push.ps1 -BuildOnly

# Push the three pinned images once code/permissions are approved
.\scripts\manual-deploy\01-build-push.ps1 -Push -ReviewedCommit $COMMIT
```

The build/push script creates `linux/amd64` images for:
`visioniq-api`, `visioniq-intelligence`, and `visioniq-db-migrate`.
The migration image is **not executed** by the build/push step.

After pushing, capture the immutable sha256 image digest for each image with:

```powershell
$REGISTRY = "$($REGION)-docker.pkg.dev/$PROJECT/$REPOSITORY"
gcloud artifacts docker images list "$REGISTRY/visioniq-api" --project=$PROJECT --include-tags
gcloud artifacts docker images list "$REGISTRY/visioniq-intelligence" --project=$PROJECT --include-tags
gcloud artifacts docker images list "$REGISTRY/visioniq-db-migrate" --project=$PROJECT --include-tags
```

Image digest, reviewed Git SHA and build time are separate evidence. For
repeatable production-style releases, promote/deploy a resolved immutable
digest rather than relying solely on mutable tags.

## 3. Cloud SQL migrations (separate, explicit approval)

**Do not use `docker compose ... db-migrate` on Cloud SQL.** That image's
default `db.manage` entry point intentionally refuses cloud modifications.
The approved Cloud SQL one-shot entry point is
`python -m db.cloud_manage all --instance PROJECT:REGION:INSTANCE`.

FND-03 created the `visioniq-db-migrate` Cloud Run Job. Before executing or
updating it, read its existing job configuration, network access, execution
service account, secrets, revision and prior execution logs. Confirm that the
dedicated migration image contains the latest Alembic graph migration
`20261031_0006` and is supplied with the **three distinct** cloud DB secret
roles. The job needs `VISIONIQ_CLOUD_RUN_MIGRATIONS=approved`, `DB_TARGET=cloud`,
correct Cloud SQL instance identity, project/region, approved private SQL IP,
and `db.cloud_manage` as command (not local `db.manage`).

Never change or execute the existing privileged job silently from the
application deployment scripts. Migrations and role grants require a separate
review, approved image and explicit operator action. Record the result and
`alembic_version` before deploying code that needs the new graph tables.

## 4. Private Cloud Run deployment (no Cloud Build)

Resolve the approved **numeric** Secret Manager version for both application
secrets. The plan mode is non-mutating. Only add `-Deploy` after security
approval and verified image tags.

```powershell
$API_VERSION = 1            # REPLACE with the approved enabled numeric version
$INTELLIGENCE_VERSION = 1   # REPLACE with the approved enabled numeric version

# Inspect the two deployment plans first:
.\scripts\manual-deploy\02-deploy-services.ps1 -Commit $COMMIT \
  -ApiSecretVersion $API_VERSION -IntelligenceSecretVersion $INTELLIGENCE_VERSION

# After reviewing the plan, execute (single line is also supported):
.\scripts\manual-deploy\02-deploy-services.ps1 -Commit $COMMIT \
  -ApiSecretVersion $API_VERSION -IntelligenceSecretVersion $INTELLIGENCE_VERSION -Deploy
```

**PowerShell note:** the line continuations above are for illustration only;
PowerShell does not use a trailing backslash. Run each invocation as a single
line, or use PowerShell backticks for continuation.

The script verifies the documented Cloud SQL identity/IP and READY VPC
connector; it deploys:
- `visioniq-intelligence`: port 8000, `DB_INTELLIGENCE_USER`, pinned Intelligence secret
- `visioniq-api`: port 5100, `DB_API_USER`, pinned API secret

Both services use internal ingress, authenticated invocation,
`visioniq-runtime`, Gen2, VPC private-ranges egress, and scale-to-zero.
The deployment does not create a public endpoint, IAM grant or Cloud Build
trigger. Deployments are sequential, *not atomic*: record prior revisions
before a release so a failed second service does not leave an untracked
partial deployment.

## 5. Read-only verification and acceptance

```powershell
.\scripts\manual-deploy\03-verify-deployment.ps1 -Commit $COMMIT
gcloud run services describe visioniq-api --project=$PROJECT --region=$REGION
gcloud run services describe visioniq-intelligence --project=$PROJECT --region=$REGION
```

The verification script checks revision readiness, the reviewed image tag,
runtime identity, internal ingress and no public Invoker grants. It does
**not** claim DB connectivity. Because internal ingress blocks ordinary
external requests, run authenticated `/health` checks **from an allowed
in-network source or approved test workload**. Do not open the services to
the public solely to make smoke tests convenient.

**Mandatory acceptance beyond the scripts:**
1. Approved authenticated `/health` calls on both services return HTTP 200.
2. Both services actually connect to private Cloud SQL using their own
   least-privileged PostgreSQL role; perform a real `SELECT`, not a liveness
   request. Run deny tests for unauthorized cross-schema writes and test-runner
   secret access. Currently the public `/health` handlers do **not** query DB.
3. Check runtime Cloud Logging, error rate, revision startup, VPC and Secret
   Manager grants. Confirm Cloud SQL schema is at `20261031_0006`.
4. Roll back a controlled test deployment to the prior ready revision if
   necessary, and record both images/revisions. Confirm budgets and security
   approvals.
5. Append screenshots, command outputs (redacted), actual IAM scope, image
   digests, deployed revisions, date/time and any errors to the permanent
   `VisionIQ_Cloud_Structure` documentation. Never record passwords/tokens.

The LRN-01–07 ingestor and code scanner are **not** run by these two
HTTP services. Real-source Checkpoint A requires separate authorized batch
worker execution and evidence/graph validation; service `/health` alone is
not LRN acceptance.

## 6. Rollback and limitations

Read the previous ready revision before deployment using
`gcloud run services describe SERVICE --format='value(status.latestReadyRevisionName)'`.
Keep the old image digest and secret versions. If a deployment fails, review
the revision state and use a controlled Cloud Run traffic rollback to the
previous approved revision, with a logged change record. Do not blindly
change traffic on shared corporate services.

**FND-04 is NOT Done until the actual development deployment, authenticated
smoke tests, database tests, least-privilege/security approval and master
cloud guide evidence all pass.** GitHub PR/CI success means the manual scripts
are ready to review—not that anything was deployed.

**FND-AUTO (#11) may remain Open while FND-04 is accepted manually.**
