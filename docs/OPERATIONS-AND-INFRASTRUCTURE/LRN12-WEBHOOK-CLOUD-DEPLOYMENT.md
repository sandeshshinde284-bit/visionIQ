# LRN-12 — ADO PR webhooks: Cloud Run deployment and validation gate

## Current state
The dedicated .NET 10 `VisionIQ.Ingestor.Cli` container hosts the canonical ADO webhooks under
`/webhooks/ado/pull-requests` (and `/webhooks/ado/work-items`), and shares the same reviewed
evidence projection with periodic sync. **A successful container build is not a deployed endpoint.**
No new service is deployed by the regular `02-deploy-services.ps1` script.

## Critical inbound authentication decision
The existing `visioniq-api` and `visioniq-intelligence` services are internal and IAM
authenticated. An external Azure DevOps Service Hook cannot directly invoke such a Cloud Run
endpoint using a Google-issued invocation ID token.

**Do not change either existing service's ingress or IAM policy to accept ADO.** Treat the
webhook host as a distinct ingress boundary. Select an approved corporate ingress path
(e.g. an HTTPS load balancer / gateway with filtering and appropriate abuse protection,
or an explicitly security-approved public Cloud Run webhook service), with TLS and the
application's constant-time Basic credential verification. An ADO-shared Basic secret is not
equivalent to Google IAM identity. Do not enable unauthenticated public ingress silently.
A publicly reachable webhook requires an explicit security decision and review.

## Deployment package (prepared; not deployed)
- Image: `visioniq-ingestor-webhooks`, Dockerfile:
  `src/VisionIQ.Ingestor.Cli/Dockerfile`
- Build only: `./scripts/manual-deploy/01-build-push.ps1 -BuildOnly`
- After code review and appropriate permissions, image push:
  `./scripts/manual-deploy/01-build-push.ps1 -Push -ReviewedCommit (git rev-parse HEAD)`
- Project / region: existing approved development project, `europe-west3`
- Service should use a **dedicated, least-privileged runtime service account** with only the
  database, archive storage and secret access needed for canonical ingestion; avoid
  granting general admin permissions or reusing a broader service account without review.
- Cloud SQL connection via the approved VPC/private network; use the API-role credentials
  and existing `visioniq_dev` database. Never log secret values.

## Required secret environment
Provision in Secret Manager and reference **pinned versions** from Cloud Run, not the
repository or command history:

```text
ADO_ORG_URL              = approved ADO organization HTTPS URL
ADO_PROJECTS             = explicitly approved project list
ADO_REPOS                = approved repository list (empty means all approved project repos)
ADO_READ_PAT             = read-only ADO credential
ADO_WEBHOOK_USERNAME     = ADO Service Hook Basic username
ADO_WEBHOOK_SECRET       = strong (32+ character) ADO Service Hook Basic secret
DB_API_USER              = visioniq_api
DB_API_PASSWORD          = existing DB API-role secret
DB_HOST                  = approved private Cloud SQL address
DB_PORT                  = 5432
DB_NAME                  = visioniq_dev
DB_SSL_MODE              = require, subject to actual Cloud SQL transport configuration
CHECKPOINT_STORE         = postgres
INGESTION_OUTPUT_MODE    = canonical
GCS_EVIDENCE_BUCKET      = existing approved canonical evidence archive bucket
PORT                     = 8080 (Cloud Run injects this automatically)
```

Verify the precise database name, SSL transport, bucket, service-account grants and network
against the actual Cloud Run/SQL configurations before deployment. No configuration value
above authorizes broadening visibility of historical tickets.

## Production path and integration ordering

1. Merge a reviewed, green LRN-12 PR and build/tag the webhook image from its exact commit.
2. Identify the colleague's **actual BRIEF/DEFECT PRs or branch heads**. Do not deploy an
   unreviewed mixed set of services; integrate and run common canonical-contract tests first.
3. Complete external ADO ingress approval, Secret Manager and least-privilege VPC checks.
4. Deploy the webhook host as a **new** service with those settings. Do not mark
   LRN-12 as cloud-verified until Cloud Run reports a ready revision and the endpoint is reachable.
5. Register ADO PR created/updated/merged/comment service hooks on the approved endpoint.
   Abandonment and review-vote changes are represented by PR updated events.
6. Replay an approved test PR's event twice: one inbox identity, stable source revision and
   independent reviewer facts. Change one vote, then merge/abandon a test PR and verify
   new canonical revisions, without creating a verified root cause.
7. Check unauthorized/wrong Basic credentials produce 401; a failure produces retryable
   500; inspect sanitized error logs and durable event inbox state.
8. Compare the webhook-observed record against a periodic read-only reconciliation run:
   reviewer keys, per-vote versions, PR state, declared links and source revisions must match.
9. Only after LRN-12 passes should LRN-13 connect the PR event to targeted index refresh,
   lesson consolidation, and a schema-backed `knowledge.changed` Pub/Sub publisher.
10. Run the BRIEF/DEFECT integration suite against the same published snapshot and
    verify an evidence-cited Brief appears only for authorized, fresh evidence.

## What is NOT proven by current tests
Local tests establish in-process lifecycle, HTTP response handling, webhook/sync reviewer
projection parity and PostgreSQL readiness, not live ADO subscriptions, public ingress
approval, Cloud SQL networking, provider revocation, or an end-to-end BRIEF workflow.

Cloud Run deployment, ADO service-hook registration and integration with colleague's
unmerged work must remain open until verified with actual artifacts and logs.
