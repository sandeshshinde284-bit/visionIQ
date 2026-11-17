# FND-04 — Manual Cloud Run deployment from main

This release is separated from LRN-01–07. Tracking: GitHub issue #10; original merged feature PR #12. Automation is deferred to issue #11.

## Target environment
Existing GCP project `gbr-mts-wgh-hkt-dev`, region `europe-west3`, Artifact Registry `team-visioniq-docker-dev`, VPC connector `vpc-con`, Cloud SQL `visioniqdev` (development), runtime service account `visioniq-runtime`. Confirm current values and corporate authorization before running. Do not create duplicate infrastructure or modify the migration job as part of application deployment.

## From the repository root (Windows PowerShell)

1. Check Docker Desktop is running, and review `gcloud auth list`, `gcloud config get-value project`, and existing registry, Cloud SQL and VPC resources.
2. Use the approved and tested `main` source commit after this PR merges. Keep a clean working tree and record `$SHA = (git rev-parse HEAD).Trim()`.
3. Preview: `./scripts/manual-deploy/01-build-push.ps1`.
4. Build and push approved images: `./scripts/manual-deploy/01-build-push.ps1 -Push -ReviewedCommit $SHA`. Record pushed image digests; do not rely on mutable tags.
5. Check authorized numeric Secret Manager versions for API and Intelligence passwords (never print secret values).
6. Preview deployment: `./scripts/manual-deploy/02-deploy-services.ps1 -Commit $SHA -ApiSecretVersion 1 -IntelligenceSecretVersion 1` (replace both sample version numbers with approved current numeric versions).
7. After approval run the same command with `-Deploy` and the confirmed secret versions. Both Cloud Run services remain private with internal ingress.
8. Verify: `./scripts/manual-deploy/03-verify-deployment.ps1 -Commit $SHA`. From an authorized in-network authenticated caller, check `/health` and `/ready` for each service.
9. Confirm real Cloud SQL SELECT under each restricted service identity and explicitly rejected cross-schema writes; `/health` alone does not test DB access. Record deployed revision IDs, image digests, logs, approvals, timestamps, screenshots and rollback procedure in `VisionIQ_Cloud_Structure`.

## Acceptance
Do not mark FND-04 Done until actual Cloud Run deployment, authenticated health and SQL checks, permission-denial validation, approval and documentation are complete. These scripts do not run or change the FND-03 migration Cloud Run Job. Automated Cloud Build / Developer Connect belongs only to FND-AUTO (#11).
