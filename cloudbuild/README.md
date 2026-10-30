# VisionIQ Cloud Build deployment — FND-04 preparation

**Status: source configuration prepared; NOT connected, provisioned, run, or deployed.**

This folder contains two deliberately separate Cloud Build stages. The build stage
compiles and publishes images; the approved deployment stage creates or updates
only the **VisionIQ** Cloud Run application services. Neither touches the existing
`visioniq-db-migrate` job or runs SQL migrations.

| Stage | File | Required identity | Trigger |
| --- | --- | --- | --- |
| Build | `cloudbuild/build-images.yaml` | Dedicated image-build SA | Corporate `main` push, after review |
| Deploy | `cloudbuild/deploy-services.yaml` | Dedicated deploy SA | Manual, approval required, corporate `main` |

## Existing approved environment (verify before using)

- Project: `gbr-mts-wgh-hkt-dev`; region: `europe-west3`.
- Registry: `team-visioniq-docker-dev` (Docker).
- VPC connector: `vpc-con` (`READY` at last verification).
- Private Cloud SQL: `visioniqdev`, DB `visioniq_dev`,
  `10.102.16.8:5432`; no Cloud SQL migration is part of these stages.
- Runtime Google service account: `visioniq-runtime@gbr-mts-wgh-hkt-dev.iam.gserviceaccount.com`.
- Database logins: `visioniq_api` and `visioniq_intelligence`, not the migration administrator.
- Secrets: `visioniq-db-api-password` and
  `visioniq-db-intelligence-password`. Direct `secretAccessor` grants to
  the runtime account were confirmed on 30 October 2026.
- The shared runtime account can read *both* application secrets. This is the
  previously chosen hackathon trade-off, not strict per-service secret isolation.
  An organization security approval of this design is still required.

The project contains unrelated corporate services and secrets. Do not modify
them. Do not use existing GitLab tokens or a personal GitHub PAT to work around
corporate GitHub App approval.

## Prerequisites / IAM boundaries

1. Confirm corporate authorization to connect the private GitHub repository
   `https://github.com/sandesh-shinde_exakisc/Defect-Intelligence` to
   Cloud Build. As it is hosted on **github.com**, use the 2nd-gen GitHub
   connection type, not GitHub Enterprise Server. Organization administrators
   might have to approve installing the Cloud Build GitHub App.
2. Have an administrator provision/approve separate **build** and **deploy**
   Google service accounts. Do not reuse `visioniq-runtime` (application
   identity) or `visioniq-db-migrate` (database administrator).
3. Scope the **build** SA to Artifact Registry writer on
   `team-visioniq-docker-dev` and Cloud Logging writer for pipeline logs.
4. Scope the **deploy** SA to Cloud Run service creation/updates (an authorized
   scoped custom role or initially `roles/run.admin` where needed),
   `roles/iam.serviceAccountUser` on `visioniq-runtime`, and Cloud Logging
   writer. Its identity does **not** need permission to read database-password
   secret values. Verify service-agent image-pull permissions separately.
5. Grant appropriately scoped Cloud Build trigger create/run/approve rights
   to the authorized operators. Apply branch protections and require review.
6. Reconfirm organization policy on Cloud Run ingress, VPC routing, and
   whether a shared runtime service account is acceptable.
7. User-managed Cloud Build service accounts require an approved log
   destination. Both YAML configurations use `CLOUD_LOGGING_ONLY`; the build
   and deploy accounts must be able to write logs.

The original work-item requires separate identities for source read, model
calls, code execution, PR creation, deployment and a test runner with no
secret access. These app/agent identities must still be designed and tested;
the build/deploy separation here does not satisfy all those items.

## Step 1 — read-only checks

From PowerShell:

```powershell
$PROJECT = "gbr-mts-wgh-hkt-dev"
$REGION  = "europe-west3"

gcloud auth list
gcloud config get-value project
gcloud builds connections list --project=$PROJECT --region=$REGION
gcloud builds triggers list --project=$PROJECT --region=$REGION

# Metadata only; never use "versions access" in shared output.
gcloud secrets versions list visioniq-db-api-password --project=$PROJECT
gcloud secrets versions list visioniq-db-intelligence-password --project=$PROJECT

gcloud iam service-accounts list --project=$PROJECT --filter="email:visioniq"
gcloud run services list --project=$PROJECT --region=$REGION
```

## Step 2 — connect corporate GitHub (admin-approved operation)

After organization and Cloud IAM approval, create the connection:

```powershell
gcloud builds connections create github visioniq-corporate-github `
  --project=$PROJECT `
  --region=$REGION
```

The CLI displays an authorization URL. An authorized GitHub organization
administrator must approve and restrict the Cloud Build app installation to
the VisionIQ corporate repository. **Do not paste the installation token or
GitHub PAT into source control or chat.** Then verify:

```powershell
gcloud builds connections describe visioniq-corporate-github `
  --project=$PROJECT --region=$REGION
```

Proceed only when the installation state is `COMPLETE`. Link the repo:

```powershell
gcloud builds repositories create visioniq-corporate `
  --remote-uri="https://github.com/sandesh-shinde_exakisc/Defect-Intelligence.git" `
  --connection=visioniq-corporate-github `
  --project=$PROJECT --region=$REGION

gcloud builds repositories list `
  --connection=visioniq-corporate-github `
  --project=$PROJECT --region=$REGION
```

If this is blocked by permissions or org policy, do not bypass it with a
personal-token workaround. Record the exact error in
`VisionIQ_Cloud_Structure` and request an approved integration.

## Step 3 — two protected Cloud Build triggers

Use the Cloud Build Console (second-generation repository) to create two
triggers **after** the corporate PR for these files is merged:

- `visioniq-build-main`: repository `visioniq-corporate`, `^main$` push;
  config `cloudbuild/build-images.yaml`; dedicated build SA.
  Set `_IMAGE_TAG` to the built-in `$COMMIT_SHA` (dynamic substitution);
  builds only from reviewed `main` commits.
- `visioniq-deploy-approved`: **manual trigger**, source corporate
  `main`, config `cloudbuild/deploy-services.yaml`, dedicated deploy SA,
  `require approval` enabled. Its substitution values must be explicitly
  supplied per approved release.

Build outputs:
```text
europe-west3-docker.pkg.dev/gbr-mts-wgh-hkt-dev/team-visioniq-docker-dev/visioniq-api:<approved-40-char-sha>
europe-west3-docker.pkg.dev/gbr-mts-wgh-hkt-dev/team-visioniq-docker-dev/visioniq-intelligence:<approved-40-char-sha>
```

Both tags must exist *before* invoking the deployment trigger. When executing
the deploy trigger, supply `_IMAGE_TAG` (the built SHA), `_APPROVAL=approved`,
and numeric `_API_SECRET_VERSION` and `_INTELLIGENCE_SECRET_VERSION`.
The default `_INGRESS=internal` denies direct internet access. If integration
requires external access, perform a separate security review; a public
Cloud Run URL is not automatically the same as unauthenticated access.

A textual `_APPROVAL` guard is an accident-prevention check, **not** an
authorization mechanism. Actual authorization requires separate IAM,
protected trigger execution rights and Cloud Build approval.

## Step 4 — acceptance / verification before FND-04 completion

- Build log proves both images build and push from the corporate `main` SHA.
- Deploy log proves both Cloud Run **services** use only `visioniq-runtime`,
  not the migration SA; each service references its own password secret.
- Both revisions are ready, and both `/health` endpoints return HTTP 200
  **from an authorized allowed caller**.
- Test an actual database SELECT with each runtime identity, not only
  `/health`; verify cross-schema writes are denied.
- Confirm VPC connector reaches `10.102.16.8:5432` from *each* service.
- Confirm authentication between services and appropriate Cloud Run Invoker
  grants; service-to-service invocation code is not yet implemented.
- Demonstrate that test-runner identity cannot read either DB secret.
- Confirm billing alerts/budget, Cloud Logging, image provenance, rollback,
  and release approval evidence.
- Record URLs, deployed revisions, secret-version metadata (never values),
  timestamps, screenshots, IAM scope, errors and recovery in the permanent
  `VisionIQ_Cloud_Structure` guide.

**Operational caveats:** deploying Python then .NET is not atomic; if the
second deploy fails, the first might be updated. Use recorded prior Cloud Run
revisions/images and reviewed rollback steps. The dedicated DB migration job
is not modified by these files. The current `/health` routes are liveness
checks and do not query PostgreSQL.
