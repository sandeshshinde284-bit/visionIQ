# VisionIQ cloud deployment hardening — review before applying

This change targets GitHub source. It does **not** deploy to the corporate GCP project.
It must be reviewed and GitLab CI Lint must validate the included corporate template.

## Verified baseline (17 November 2026)

- Target project: `gbr-mts-wgh-hkt-dev`; Cloud Run region `europe-west3`.
- Team image registry: `europe-west1-docker.pkg.dev/gbr-mts-wgh-hkt-dev/docker-ps04-t11`.
- Live inventory showed ready `visioniq-api`, `visioniq-intelligence`, and existing `visioniq-db-migrate` job.
- Missing at the time: webhook service, ingestion/indexing jobs, and VisionIQ Pub/Sub subscriptions and Scheduler jobs.
- Four VisionIQ Pub/Sub topics already existed (two primary and two dead-letter).
- The older manual registry `team-visioniq-docker-dev` must be preserved until rollback dependencies are checked.

## What this PR changes

1. Adds offline contract checks to GitLab merge-request quality validation.
2. Keeps four Kaniko image builds in `docker-ps04-t11`.
3. Requires a **blocking manual approval** for migration before deployment (including routine pushes for now).
4. Uses the cloud-specific migration command `python -m db.cloud_manage all` and guarded cloud-only credentials.
5. Disables BRIEF/DEFECT/ADO writes and automatic event dispatch at first deployment.
6. Deploys the webhook Cloud Run service with authenticated, internal ingress; this cannot accept
   direct public Azure DevOps hooks without an approved authenticated gateway.
7. Introduces post-deployment metadata checks and plan-first Pub/Sub/Scheduler reconciliation.
8. Keeps infrastructure mutation manual, not on ordinary GitLab code pushes.

## Decisions and prerequisites before deployment

- **Evidence bucket:** The architecture says `visioniq-source-evidence-dev`; the earlier
  GitLab YAML used `gbr-mts-wgh-hkt-ps04-t11`. Both exist. Set protected
  GitLab CI variable `GCS_EVIDENCE_BUCKET` only after inspecting canonical evidence
  and persisted `gs://` URIs. No data migration is included.
- **DB credentials:** Provide protected `DB_CLOUD_USER`. Check three existing Secret Manager
  values `visioniq-db-admin-password`, `visioniq-db-api-password`,
  `visioniq-db-intelligence-password`; the migration job requires distinct passwords
  and approved administrator account.
- **IAM:** Validate TEAM_SA privilege scope, Pub/Sub Publisher on source topics,
  `roles/run.invoker` on services for authenticated push, Pub/Sub service agent's
  permissions to mint OIDC tokens and dead-letter messages, and scheduler identity's
  permission to execute the two Cloud Run jobs.
- **Runtime:** The current Cloud Run service CPU allocation and min-instances=0
  must be reviewed before enabling persistent pickup/background workers.
- **Secrets:** Additional ADO/Confluence/Claude values must be configured deliberately
  before enabling corresponding optional features; they are not turned on by this PR.
- **Security:** `semgrep-sast: allow_failure: true` comes from the earlier configuration
  and should be reviewed against corporate security policy.
- **GitLab template:** Verify `m3sacpc/m3sacpc-core` /templates/hackathon-ci.template.yml
  and the runner's availability. GitHub cannot expand that template.
- **Artifact hand-off:** The existing OAuth-based Kaniko config artifact contains a short-lived
  access token; ensure protected runner artifacts and consider per-job keyless auth.
- **Scheduler:** The `gcloud scheduler jobs create http` command cannot create a job paused.
  The Scheduler script intentionally supports only read-only plan and verify modes.
  Provision new jobs as paused through reviewed Terraform (`google_cloud_scheduler_job.paused = true`),
  then activate schedules only after cloud acceptance testing and approval.
- **Pipeline verification:** Run GitLab CI Lint, a feature-branch build, GCP secret/access audit,
  Cloud Run smoke tests, Pub/Sub push tests, DB permissions checks, ingestion/indexing E2E,
  then scoped ADO ticket acceptance.

## Local, read-only commands

```powershell
python scripts/cloud/validate_contracts.py
python scripts/cloud/reconcile_infrastructure.py --mode plan
python scripts/cloud/reconcile_scheduler.py --mode plan
```

The infrastructure scripts use the checked-in `contracts/events/topology.json` as the
single event naming source. Missing cloud resources do not imply code failure; never
replace or recreate a resource simply because it is in a different registry or region.

## Explicitly not completed

- Corporate GitLab pipeline execution or CI Lint.
- Cloud SQL migration execution or verification against a real database.
- Live Cloud Run deployment, Pub/Sub IAM bindings, Scheduler jobs, webhook gateway.
- Full cloud integration testing, ADO comment posting, or draft fix PR creation.
