# VisionIQ corporate platform alignment — PR #42

This is the review checklist for copying the GitHub feature-branch changes to the
company GitLab `core` repository. **Do not copy the full GitHub repository over
company GitLab `main`**, or overwrite changes maintained by the Cloud Architect.
Use a company feature branch / MR and compare the final diff against GitLab `main`.

Sources checked on 17 November 2026:
- Architect-provided `PLATFORM.md`
- Architect-provided `.gitlab-ci.yml.template`
- `ENTERPRISE_CI_AND_DEPLOYMENT_GUIDE.md`
- `GUIDELINES_CLOUD_HOSTING_AND_12FACTOR.md`
- VisionIQ application code, Dockerfiles, and `contracts/events/topology.json`

## Confirmed compatible setup

| Requirement | VisionIQ implementation |
| --- | --- |
| Corporate CI include | `m3sacpc/m3sacpc-core` /templates/hackathon-ci.template.yml retained |
| Target project | `gbr-mts-wgh-hkt-dev` |
| Artifact Registry | `docker-ps04-t11`, `europe-west1` |
| Cloud Run region | `europe-west3` |
| Runtime identity | Corporate `TEAM_SA` (verify equals `sa-ps04-t11@...`) |
| VPC connector | `vpc-con`, private Cloud SQL IP `10.102.16.8` |
| App images | API, Intelligence, Ingestor Webhooks, DB Migrator |
| Cloud Run Job definitions | DB migrations, ingestion sync, index refresh |
| Actual migration code | Python Alembic `db.cloud_manage` with cloud-only credentials |
| Pub/Sub topology | `contracts/events/topology.json` is the source of truth |
| Cloud resource changes | Existing `scripts/pubsub/provision-pubsub.ps1`, plan-first, manual approval |
| Local verification | `scripts/cloud/validate_contracts.py` |
| Deployed metadata verification | `scripts/cloud/verify-cloud-run.sh` |
| Platform preflight | `scripts/cloud/verify-platform.sh` (metadata only; optional manual CI job) |

The generic template's `HACKATHON_AUXILIARY_SERVICE=true` flag was **not**
added: this is the main application repository, not a confirmed auxiliary
service. Have the Cloud Architect clarify it if the central template requires it.

## Actual code fixes included in PR #42

- `visioniq-api` now receives `GCS_EVIDENCE_BUCKET`, required for canonical
  evidence and fix/report code.
- `LOG_PSEUDONYM_KEY` is bound to the application services and ingestion/indexing
  jobs; `ADO_READ_PAT` is available to the API and Intelligence where optional
  access-controlled source operations use it.
- Old Artifact Registry assumptions in manual recovery scripts and their static
  tests were corrected to the platform team registry/region.
- One stale test that expected `team-visioniq-docker-dev` was fixed.
- All 11 platform secret names can be inspected by the read-only preflight.
  Required runtime secrets fail preflight when absent; optional pilot-only
  secrets are reported but do not block a safe disabled-feature deployment.
  Secret payloads are never read or logged.
- The SSO-token-bearing Kaniko configuration artifact expiry is shorter than
  the earlier 24-hour setting. Access remains a GitLab runner concern.

## Disagreements with PLATFORM.md — DO NOT blindly overwrite code

1. **Actual database migration:** The platform diagram labels Flyway / EF Core
   and a default entrypoint. VisionIQ uses Python Alembic and the `db/Dockerfile`
   default `db.manage all` is for local operations. Cloud migrations **must**
   explicitly run `db.cloud_manage all --instance PROJECT:REGION:INSTANCE`.
   PR #42 uses the correct cloud-only command and credentials. It intentionally
   retains a **blocking manual migration approval** rather than auto-migrating
   Cloud SQL on every push. Architect must review the operational difference.

2. **Webhook ingress:** `PLATFORM.md` specifies `--ingress all
   --allow-unauthenticated`; the C# webhook handler validates Basic auth
   with a minimum 32-character shared secret, but making the Cloud Run service
   public bypasses Cloud Run IAM on that endpoint. PR #42 remains
   `--ingress internal --no-allow-unauthenticated` **until** Cloud Architect
   approves public direct ADO delivery or a secured intermediary/gateway.
   As configured, direct Azure DevOps Service Hooks will NOT reach the private
   service; scheduled ingestion remains the safe alternative. This is an open
   functional gap, not a completed webhook deployment.

3. **Pub/Sub consumer flow:** The platform Mermaid drawing routes
   analysis.requested toward API and analysis.completed toward Intelligence.
   The application's `EventTopology.cs` and `topology.json` specify the
   reverse: requested is consumed by Intelligence, completed by API.
   **Use the code contract**; request a correction to the platform diagram.

4. **Cloud NAT:** The platform claims all outbound traffic traverses NAT
   through `vpc-con`, but also specifies `private-ranges-only`; with that
   setting ordinary public traffic bypasses the VPC connector. Do not change
   to `all-traffic` without a networking review of Cloud NAT routing, firewall,
   approved static egress identity and cost.

5. **Cloud SQL PostgreSQL version:** The platform claims PostgreSQL 18 while
   earlier inventory indicated 16. Read-only platform preflight prints the
   actual current `databaseVersion`. Do not change migrations or extension
   assumptions based solely on the document.

6. **Canonical GCS bucket:** The platform-provisioned bucket is
   `gs://gbr-mts-wgh-hkt-ps04-t11`. Earlier VisionIQ local configuration
   used `visioniq-source-evidence-dev`. Before setting protected
   `GCS_EVIDENCE_BUCKET` in GitLab, inspect current DB `gs://` evidence
   references and approve a single destination. **Do not move data silently.**

7. **Background workers/FinOps:** Platform guidance requires Cloud Run
   min-instances=0; existing BRIEF, pickup, outbox and DEFECT service workers
   require continuous CPU or a request/event-triggered alternative. PR #42
   initially deploys workers disabled. An operational activation plan is
   needed for hackathon acceptance; deployment metadata alone is not enough.

8. **Secrets and feature enablement:** `PLATFORM.md` describes 11 secrets;
   it does not list Confluence tokens required to sync Confluence in the
   existing code. Also, missing optional ADO write secrets must not be
   worked around by weakening access controls. PR #42 leaves automated ticket
   write, BRIEF and DEFECT switches disabled until explicit pilot approval.

9. **Security scanning:** The Architect proposed ignoring all tests and
   `db/manage.py` in Semgrep. Do NOT add a blanket `.semgrepignore`
   exclusion without classifying the findings. `db/manage.py` performs
   privileged database setup. The existing `semgrep-sast: allow_failure: true`
   is a temporary operational compromise and needs explicit team approval.

## What actually happens after a corporate GitLab main push

1. The company repository triggers its *own* latest pipeline, not GitHub PR #42.
2. Required security/quality checks run according to the corporate include.
3. `prepare-gcp-session` requires nominative browser SSO; the Cloud Architect
   provided the sign-in link and company pipeline 20837937.
4. Four Kaniko containers are built using the team Artifact Registry.
5. PR #42's migration is **manual and blocking**, then services/job definitions
   deploy; this is *not* the architect's automatic-migration behavior.
6. Metadata verification checks Cloud Run services/jobs, but does not prove
   successful DB access, source ingestion, webhook delivery, Pub/Sub or AI flow.
7. Cloud Scheduler definitions and activation are *not* in this PR.
   The platform team must confirm Scheduler ownership and execute/approve it.

## Read-only checks before copying to GitLab

From the root of the updated GitHub feature branch:

```powershell
python scripts/cloud/validate_contracts.py
```

After corporate SSO, from an authorized runner where `GCP_PROJECT`,
`DB_CLOUD_USER`, and `GCS_EVIDENCE_BUCKET` are configured:

```bash
bash scripts/cloud/verify-platform.sh
bash scripts/cloud/verify-cloud-run.sh
```

`verify-platform.sh` audits metadata and enabled secret versions only; it
does not verify payloads or per-service IAM. `verify-cloud-run.sh` requires
`SERVICE_REGION` and `TEAM_GAR` from the corporate CI session.

**Not completed in this PR:** GitLab CI Lint after corporate template expansion,
real image builds, live secret/service access checks, public webhook approval,
Scheduler setup, migration execution, controlled E2E testing and merge.

**Never run `git push origin main` without reviewing the company MR and the
current pipeline's migration/deployment settings.**
