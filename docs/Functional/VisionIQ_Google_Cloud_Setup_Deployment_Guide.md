# VisionIQ - Google Cloud Setup & Deployment Guide

**Living guide | Cloud infrastructure and deployment | Revision 1.1: 29 October 2026**
**Cloud SQL instance:** `visioniqdev`  |  **Project:** `gbr-mts-wgh-hkt-dev`  |  **Region:** `europe-west3`

> **Status:** Cloud SQL configuration confirmed against four team-provided Google Cloud Console screenshots dated 29 October 2026. Cloud database readiness via API, network connectivity, cloud migrations and application deployment remain **unverified**. Do not store passwords, tokens or personal ownership labels in this document.

This is the dedicated, living cloud operations guide for VisionIQ. It records the Cloud SQL configuration supplied on 29 October 2026 and separates configured settings from verified connectivity, migrations, application deployments, and future changes.

## Current state

| Area | Status / evidence |
| --- | --- |
| Console screenshots reviewed | Four provided screenshots show the Cloud SQL Configurations page for `visioniqdev`, including network/security, backups, maintenance and Query Insights. This is visual verification of the displayed settings, not a live API or connectivity test. |
| Local development verified | PostgreSQL and both local APIs were healthy; the local Alembic one-shot container completed with Exited (0). |
| Cloud readiness not yet verified | A green status indicator is visible in the screenshots, but API-reported `RUNNABLE` state, the logical VisionIQ database, migrations, IAM access, private connectivity and Cloud Run deployment have not been independently tested. |

## Configuration snapshot - as provided

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

## Screenshots and configuration provenance

**Evidence source:** Four team-supplied screenshots from **Google Cloud Console → Cloud SQL → visioniqdev → Configurations**, received 29 October 2026. They confirm the displayed values in Sections 1–5 and the presence of two resource labels (identifiers deliberately not reproduced). These screenshots do **not** demonstrate database connectivity, SQL permissions, successful migration, or Cloud Run readiness. Record those results separately when tested.

**Provisioning method:** Manually created in Google Cloud Console; **no Terraform** is required or used for this instance.

## Working architecture and environment boundaries

```text
Developer workstation (Windows + Zscaler) -> private route NOT verified

Future VisionIQ Cloud Run (.NET 10 API / Python 3.12 Intelligence)
                |
        Approved VPC connectivity
                |
Cloud SQL: visioniqdev / PostgreSQL 16.15
        Private IP: 10.102.16.8  |  PSA
```

The working local Docker stack uses its own PostgreSQL container. Cloud SQL uses a separate private database in the recorded Google Cloud project. Do not assume that a laptop, Zscaler, or the Cloud SQL Auth Proxy alone provides the required private network route.

### Proposed schema and runtime responsibility (decision D07 is not yet signed off)

| PostgreSQL schema | Proposed writer | Proposed other-service access |
| --- | --- | --- |
| `workflow` | .NET API runtime | Read-only |
| `evidence` | .NET ingestion boundary / API runtime | Read-only |
| `knowledge` | Python Intelligence runtime | Read-only |

A distinct migration identity should own schema DDL. The role design is proposed and must be confirmed before cloud grants are applied.

## Read-only commands for inventory (after gcloud authentication)

Run only in the approved GCP context. These commands inspect metadata; they do not create resources.

**Inspect selected GCP project**
```powershell
gcloud config get-value project
```

**Describe instance (read-only)**
```powershell
gcloud sql instances describe visioniqdev --project=gbr-mts-wgh-hkt-dev --format="yaml(name,state,region,databaseVersion,connectionName)"
```

**List logical databases (read-only)**
```powershell
gcloud sql databases list --instance=visioniqdev --project=gbr-mts-wgh-hkt-dev
```

## Implementation and validation checklist

- [ ] **C01. Confirm instance is ready** - Configurations visually reviewed from screenshots (29 October 2026); still run `gcloud sql instances describe` to confirm `RUNNABLE` and capture the result.
- [ ] **C02. Confirm billing and authorized access** - Review ongoing cost of 2 vCPUs, 4 GB RAM, 10 GB SSD, backups and PITR. Validate required IAM permissions for managing Cloud SQL and any chosen migration execution environment.
- [ ] **C03. Create or verify the logical application database** - Choose and record the actual application database name. Proposed only: visioniq_dev. No name has been confirmed as created yet. Use separate cloud credentials; never reuse local passwords.
- [ ] **C04. Prepare cloud migration execution** - Current db/manage.py intentionally refuses DB_TARGET=cloud. Implement a reviewed, explicit cloud migration runner; do not point local Docker Compose migrations at this instance.
- [ ] **C05. Run schema migrations** - From an authorized execution environment with private VPC reachability, enable/verify pgvector, migrate workflow/evidence/knowledge, verify PostgreSQL full-text search, and record Alembic head.
- [ ] **C06. Enforce cloud roles and isolation** - Provision distinct cloud admin/migration and restricted .NET/Python runtime roles. Validate authorized reads/writes and denial of cross-schema writes.
- [ ] **C07. Connect Cloud Run to Cloud SQL** - Deploy .NET 10 and Python 3.12 services when FND-04 is ready; configure approved private VPC egress to the PSA-backed Cloud SQL instance and use managed secrets. Validate connectivity; do not treat HTTP health alone as database readiness.
- [ ] **C08. Record acceptance evidence** - Save sanitized command output: instance state, migration version, pgvector and full-text checks, runtime-role denial test, Cloud Run-to-Cloud SQL connectivity, and application smoke results.

## Operational cautions and decisions to verify

- **Private networking:** Public IP is disabled. A developer connected through Zscaler is not automatically able to reach VPC private address 10.102.16.8. Verify an approved private route, or run migrations and smoke tests from an authorized Google Cloud workload.
- **Cloud configuration separation:** Do not change the working local PostgreSQL data volume or use docker compose down -v. Keep Cloud SQL migrations explicit and separate from normal docker compose up.
- **TLS and credentials:** SSL-only access is enabled and IAM database authentication is on, but neither confirms how each app will authenticate. Choose the runtime authentication design and secret delivery before deployment.
- **Certificate rotation:** Automatic server certificate rotation is reported disabled; add an operational review well before 29 Oct 2027 and confirm rotation policy with the cloud administrator.
- **Region and data handling:** Cloud SQL is in Frankfurt (europe-west3), with backups in EU multi-region. Confirm this placement is authorized for any engineering tickets, code and documentation ingested later.
- **Feature toggles:** Vertex AI integration and Knowledge Catalog integration being enabled on Cloud SQL do not prove VisionIQ application integration is implemented or working.

## Change log

| Date | Change | Evidence / validation |
| --- | --- | --- |
| 29 Oct 2026 | Initial Cloud SQL configuration captured from supplied settings | Team-provided configuration |
| 29 Oct 2026 | Confirmed configuration against four Console screenshots; documented two resource labels without personal identifiers; confirmed manual provisioning, no Terraform | Screenshot visual review; no API connectivity or migration claimed |

## Document ownership and update rule

Update this single guide after **every** material Google Cloud change (Cloud SQL, VPC, IAM, secrets, Cloud Run, database migrations, monitoring and deployment). For each change, record the date, resource, actual configuration, reason, command or Console path, evidence, **tested/pending** status and any rollback considerations. Never claim a change is verified based only on configuration screenshots. Keep one-time laptop setup and daily local Docker instructions in their separate guides.
