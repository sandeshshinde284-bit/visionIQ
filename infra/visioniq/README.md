# VisionIQ DEV event infrastructure (Terraform)

**Single source of truth:** Terraform in this directory manages Pub/Sub **subscriptions**, their
delivery IAM bindings, and **paused** Cloud Scheduler jobs. Topic names and retry contracts
come from `contracts/events/topology.json`. Schedules are in `schedules.json`.

The four **existing corporate Terraform-owned topics are intentionally DATA references**:
`visioniq-analysis-requested-v1`, `visioniq-analysis-completed-v1`, and their `-dlq` topics.
Do not re-create or import those topics into this module. Their owning platform Terraform
configuration remains authoritative. Do not delete the older image registry as part of this change.

## Prerequisites (not created by this module)

- Approved GCP project `gbr-mts-wgh-hkt-dev`, location `europe-west3`.
- All four Pub/Sub topics exist; existing API/Intelligence Cloud Run services have working URLs.
- `visioniq-ingestion-job` and `visioniq-indexing-job` have been deployed as **job definitions**.
- Approved service accounts `visioniq-pubsub-push` and `visioniq-scheduler` exist; verify
  the account names with the Cloud Architect (override Terraform variables if needed).
- A **dedicated** versioned, protected GCS Terraform **state bucket**, approved and
  created by the infrastructure team; separate from VisionIQ source evidence.
- Authenticated `gcloud`/Terraform credentials with restricted Pub/Sub, Cloud Run
  IAM, Scheduler, state bucket, and service-account IAM permissions. Public ingress is not required.
- Corporate Terraform state locking and change-control procedure. Terraform plan may
  contain sensitive identifiers and IAM data; treat outputs and state as restricted.
- The topic/subscription and service IAM ownership boundary is approved by the cloud team.
  If corporate Terraform already owns the subscriptions or IAM bindings, do not manage them
  in two states: coordinate an ownership transfer first.

## Local validation and deployment

Run from repository root:

```powershell
python scripts/cloud/validate_contracts.py
terraform -chdir=infra/visioniq fmt -check -recursive
terraform -chdir=infra/visioniq init -backend=false -input=false
terraform -chdir=infra/visioniq validate
```

After infrastructure approval and authentication, select an approved remote-state bucket:

```powershell
$stateBucket = "<approved-dedicated-terraform-state-bucket>"
terraform -chdir=infra/visioniq init -input=false "-backend-config=bucket=$stateBucket"
terraform -chdir=infra/visioniq plan -input=false -lock-timeout=5m -out=visioniq.tfplan
```

Do **not** apply until the plan has been reviewed. The normal GitLab pipeline
does not automatically apply infrastructure. A separate manual job uses the
saved Terraform plan with remote state.

**Existing subscriptions:** The last read-only GCP inventory returned no VisionIQ
subscriptions. Re-check immediately before applying. For any managed resources that
already exist, import into this *same remote state* before planning. Examples:

```powershell
terraform -chdir=infra/visioniq import 'google_pubsub_subscription.push["visioniq-intelligence-analysis-requested-v1"]' 'projects/gbr-mts-wgh-hkt-dev/subscriptions/visioniq-intelligence-analysis-requested-v1'
terraform -chdir=infra/visioniq import 'google_pubsub_subscription.push["visioniq-api-analysis-completed-v1"]' 'projects/gbr-mts-wgh-hkt-dev/subscriptions/visioniq-api-analysis-completed-v1'
```

Also import any already-existing DLQ-hold subscriptions, Scheduler jobs and IAM
member bindings into the correct resource address. Inspect actual state; never
apply a resource creation over something already managed elsewhere.

## What Terraform provisions

- Two authenticated OIDC **push subscriptions** and two pull-based DLQ-hold subscriptions.
- Retry/backoff, retention and dead-letter policies from the shared event topology.
- Scoped `roles/run.invoker` for the Pub/Sub push identity and the Pub/Sub service
  agent's DLQ roles and OIDC token permissions.
- Two **PAUSED** Scheduler HTTP jobs: hourly ingestion and daily indexing. They call
  the Cloud Run Jobs v2 `:run` API with OAuth; Scheduler's service account receives
  scoped `roles/run.invoker` on the two jobs.
- **It does not automatically publish events, resume schedules, execute jobs, run
  database migrations or post to Azure DevOps.**

The architecture mentioned a third scheduled trigger, but the exact corresponding
runtime endpoint and acceptance criteria are not yet verified; do not invent
a timer or enable a ticket pickup loop as part of this infrastructure change.

## Activation and operations

1. Validate Cloud Run job configuration, DB role access, GCS evidence bucket, Vertex AI,
   and authorized ADO read credentials.
2. Exercise one manually triggered ingestion/indexing run under controlled conditions
   and inspect their actual output records. Schedule names alone do not prove operation.
3. Verify that private API/Intelligence services accept authenticated Pub/Sub push OIDC.
4. Check retries, DLQ-hold delivery, logs, IAM privilege boundaries and failure recovery.
5. Approve a separate, **explicit** change to resume schedulers. Terraform currently
   pins `paused = true`; scheduling must not be enabled by an out-of-band Console
   click followed by a Terraform run that would pause it again.
6. Keep `BRIEF_PUBLISH_MODE=off`, `FIX_PR_MODE=off`, and automated DEFECT execution
   disabled until an independently approved ticket-scoped end-to-end test.

## Legacy tooling

`scripts/pubsub/provision-pubsub.ps1` remains for historical FND-05 diagnostics,
but its `-Apply` mode is disabled to avoid competing infrastructure owners.
`scripts/manual-deploy/*` remains recovery tooling; it is NOT the normal deployment
path. Its defaults now point at team registry `docker-ps04-t11` (`europe-west1`)
with Cloud Run in `europe-west3`. Those scripts still require human approval
and must not run automatically from CI.
