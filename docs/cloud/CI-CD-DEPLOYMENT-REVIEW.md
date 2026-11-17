# VisionIQ Cloud deployment review — draft PR #42

This branch prepares VisionIQ's GitLab CI/CD, Terraform event infrastructure,
and manual recovery scripts. It has **not** deployed any Google Cloud resources.
`main` remains unchanged until the draft PR is reviewed and merged.

## Confirmed baseline (17 November 2026)

- Project: `gbr-mts-wgh-hkt-dev`; Cloud Run region `europe-west3`.
- Primary registry: `europe-west1-docker.pkg.dev/gbr-mts-wgh-hkt-dev/docker-ps04-t11`.
- Cloud Run API and Intelligence services were Ready; migration job existed.
- Missing on inventory: webhook ingestor, ingestion and indexing Cloud Run jobs.
- Four VisionIQ Pub/Sub topics existed; no matching subscriptions were listed.
- No VisionIQ-specific Scheduler jobs were listed.
- GCS evidence bucket is **unresolved** between `visioniq-source-evidence-dev`
  and `gbr-mts-wgh-hkt-ps04-t11`. Resolve persisted GCS evidence URIs first.
- The original `team-visioniq-docker-dev` registry remains for rollback.

## One owner per responsibility

| Concern | Primary owner | Notes |
|---|---|---|
| Existing topics | Existing corporate infrastructure | Reuse, do not recreate |
| Subscriptions, DLQs, Pub/Sub delivery IAM | `infra/visioniq/main.tf` | Manual approved Terraform apply |
| Scheduler definitions and job-execute IAM | `infra/visioniq/main.tf` | Creates two paused schedules |
| Topic names/retry policy | `contracts/events/topology.json` | Same contract as application |
| Schedule names/crons | `infra/visioniq/schedules.json` | Third trigger needs code/owner confirmation |
| Image builds and Cloud Run deployment | `.gitlab-ci.yml` | Four images, three services, three job definitions |
| CI contracts and smoke metadata | `scripts/cloud/` | Static checks and read-only service verification |
| Manual recovery | `scripts/manual-deploy/` | Not the default CI/CD path |
| Historic Pub/Sub PS script | `scripts/pubsub/provision-pubsub.ps1` | Plan-only; `-Apply` disabled |

The two Python infrastructure reconciliation scripts from an earlier draft
have been removed so Terraform is the single provisioning authority.

## Deployment gates

1. **Before merge:** GitLab CI Lint must expand the corporate include
   `m3sacpc/m3sacpc-core` `/templates/hackathon-ci.template.yml`.
2. **Before deployment:** Set protected `GCS_EVIDENCE_BUCKET` and `DB_CLOUD_USER`;
   confirm DB user passwords, Secret Manager, team workload identity and IAM.
3. **Before migration:** Backup Cloud SQL and approve the blocking migration job.
   It uses guarded `python -m db.cloud_manage all`, not the local migration command.
4. **After deployment:** Read-only metadata verification confirms all three
   services and three Cloud Run job definitions. This is not a runtime health test.
5. **Infrastructure:** Terraform remote state must be in a separately approved
   locked GCS state bucket (`TF_STATE_BUCKET`). Plan and apply are **manual**.
   Import any previously created subscriptions/IAM into the correct state first.
6. **Scheduler:** Two jobs start paused; do not enable until ingest/index end-to-end
   tests pass. Do not claim the diagram's third trigger without verifying its
   actual implementation and destination.
7. **Functional acceptance:** Test cloud SQL roles, GCS evidence, Vertex indexing,
   Pub/Sub OIDC delivery and a scoped ADO ticket. Enable BRIEF/DEFECT/ADO writes
   only through a separate approved deployment.

## Security and operational considerations

- The webhook Cloud Run service defaults to private/authenticated. Direct ADO
  Service Hooks need an approved gateway compatible with authentication.
- Initial Cloud Run deployment disables background event dispatch, BRIEF, DEFECT
  and external ADO writes; workers need a separate CPU/min-instance design.
- Review `semgrep-sast: allow_failure: true`, token-bearing CI artifacts,
  pinned image provenance, IAM least privilege and rollback revision references.
- GitLab's GCP runner must include Terraform for manual plan/apply. This feature
  branch cannot verify the corporate runner or the included template.
- Scheduled jobs are not executed by deploying their Cloud Run definitions.
- Data migration between the two evidence buckets is NOT included.
- The manual PowerShell deployment scripts use the team Artifact Registry by
  default but remain opt-in fallback tools.

## Offline checks

```powershell
python scripts/cloud/validate_contracts.py
terraform -chdir=infra/visioniq fmt -check -recursive
terraform -chdir=infra/visioniq init -backend=false -input=false
terraform -chdir=infra/visioniq validate
```

See `infra/visioniq/README.md` for remote-state setup, resource imports,
IAM requirements, Terraform plan/apply, and safe Scheduler activation.

**Open items:** GitLab CI Lint, Terraform format/validate on actual tooling,
cloud permissions, evidence bucket selection, a secure webhook gateway, runtime
smoke tests, migration test, event delivery and full VisionIQ acceptance.
