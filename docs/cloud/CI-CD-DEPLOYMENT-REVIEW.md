# VisionIQ Cloud deployment review — draft PR #42

This PR updates the **existing** GitLab CI/CD and gcloud/PowerShell approach.
It does not introduce Terraform, alter GCP resources, or change `main`.

## Verified baseline (17 November 2026)

- GCP project `gbr-mts-wgh-hkt-dev`, Cloud Run region `europe-west3`.
- Canonical image registry `europe-west1-docker.pkg.dev/gbr-mts-wgh-hkt-dev/docker-ps04-t11`.
- API and Intelligence Cloud Run services previously reported Ready; the migration job exists.
- Ingestor webhook service, ingestion and indexing jobs were missing in that inventory.
- Four VisionIQ Pub/Sub topics exist; the two push subscriptions and two DLQ-hold subscriptions
  were not listed.
- Two evidence buckets exist: `visioniq-source-evidence-dev` and `gbr-mts-wgh-hkt-ps04-t11`.
  Confirm the canonical bucket and persisted `gs://` evidence URIs before changing configuration.

## Existing repository ownership

| Area | Tool/file | Status |
| --- | --- | --- |
| Four image builds and Cloud Run deployments | `.gitlab-ci.yml` | Updated for review |
| Database migration job | `.gitlab-ci.yml` and `db/cloud_manage.py` | Blocking manual approval |
| Topics, subscriptions, DLQs | `scripts/pubsub/provision-pubsub.ps1` | Existing plan-first gcloud script |
| Event names/retry configuration | `contracts/events/topology.json` | Shared source of truth |
| Cloud Run metadata checks | `scripts/cloud/verify-cloud-run.sh` | Read-only |
| Offline file consistency checks | `scripts/cloud/validate_contracts.py` | No cloud credentials |
| Manual image build/deployment | `scripts/manual-deploy/*.ps1` | Optional recovery tooling |
| Scheduler triggers | Cloud configuration | **Not yet implemented** |

The existing Pub/Sub script supports read-only planning by default and an explicit
`-Apply` switch. Before using `-Apply`, confirm infrastructure ownership and the
existing topics and subscriptions, as some topics are labeled Terraform-provisioned by
the corporate platform. Do not create duplicate resources or modify team-shared IAM
without cloud-architect approval. Do not assume this script fully reconciles drift.

## GitLab pipeline review gates

- Corporate `m3sacpc/m3sacpc-core` include has not been validated here; run GitLab CI Lint.
- Main builds API, Intelligence, ingestor/webhooks and migration images in the team registry.
- Migration runs only after explicit manual approval, with cloud-only credential names.
- Three Cloud Run service deployments and the ingestion/indexing job definitions are configured.
- No Pub/Sub or Scheduler infrastructure provisioner automatically runs on GitLab pushes.
- The webhook service remains private/authenticated. Direct Azure DevOps webhooks need
  an approved gateway or alternate ingress design.
- Initial automated event dispatch, BRIEF, DEFECT and external ticket writes remain off.
  The exact enablement sequence and Cloud Run background CPU design need separate review.

## Outstanding before merging or deploying

1. Confirm authoritative evidence bucket, contents, and database object URIs; set
   protected `GCS_EVIDENCE_BUCKET` accordingly.
2. Verify `DB_CLOUD_USER`, three separate migration passwords, Cloud SQL backup and
   Cloud Run service identity/Secret Manager permissions.
3. Confirm Pub/Sub push identity, Cloud Run invoker, publisher roles and dead-letter IAM.
4. Agree Scheduler times and service account. Create schedules **paused** and enable
   only after manually tested ingestion/indexing jobs. No new Scheduler provisioning
   script is included in this PR.
5. Run Python static checks, GitLab CI Lint and actual builds; test webhook, DB access,
   Pub/Sub delivery, indexing, BRIEF and DEFECT against a scoped work item.
6. Revisit `semgrep-sast: allow_failure: true` and CI OAuth artifact handling.
7. Keep `team-visioniq-docker-dev` temporarily for existing-revision rollback until
   all workloads use `docker-ps04-t11`.

## Local read-only static validation

```powershell
python scripts/cloud/validate_contracts.py
```

The repository also includes `scripts/pubsub/provision-pubsub.ps1`, which prints
an infrastructure plan when invoked without `-Apply` and with the approved Cloud
Run URLs. Use separate authorized reviews for any cloud mutations.

**Status:** Draft PR only. GitLab CI Lint, real migrations, Cloud Run deployments,
Pub/Sub/Scheduler provisioning, and cloud end-to-end acceptance have not been executed.
