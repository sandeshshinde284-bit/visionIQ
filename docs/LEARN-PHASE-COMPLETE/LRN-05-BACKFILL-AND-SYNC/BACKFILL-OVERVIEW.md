# LRN-05 — Full historical backfill, currentness, reconciliation and quarantine

**Implementation:** Integrated source ingestion, checkpoints, reconcilers and quarantine logic; original feature PR/branch references are historical.  
**Local validation:** **PASSED for executed scenarios**, including scoped live ADO, disposable PostgreSQL and synthetic failure recovery.  
**Overall acceptance:** **OPEN / CONDITIONAL** pending approved full-history import, complete source-security handling, and live GCS/Cloud SQL/Cloud Run end-to-end validation.  
**Cloud owner of validation work:** FND-06 (no named individual owner).  
**Security:** This is a public repository. Do not add enterprise URLs, internal ticket IDs, identities, source titles, evidence payloads or PATs.

## Acceptance contract

LRN-05 must: (1) load **complete approved historical source scope** with honest per-stream counts, (2) retrieve new and updated records by delta and source events, (3) run periodic reconciliation without corrupting delta checkpoints, (4) quarantine unreadable items and recover later, and (5) preserve immutable canonical evidence versions without duplicate replay.

A successful fixture/unit test, isolated PostgreSQL test, narrow real-source run, and deployed cloud end-to-end run are **different acceptance levels**. Never substitute one for another.

## Current implementation

### Inventory and source scope

`VisionIQ.Ingestor.Cli` exposes `inventory`; `sync --source work-items,pull-requests,commits,builds,confluence [--since date] [--full]`; `sync --reconcile-days 7`; and `serve-webhooks`.

- `inventory` reads configured provider metadata; it does **not** import source history.
- Each sync stream reports `Synced`, `Partial` or `NotSynced`, with records, links, quarantined/not-stored counts and reason.
- The ordinary delta uses a saved checkpoint. `--since` applies **only without a checkpoint**. Use `--full --since ...` to re-read a known window; unrestricted `--full` must never be used on a shared project without approval.
- `CHECKPOINT_STORE=file` and `INGESTION_OUTPUT_MODE=jsonl` support isolated local tests. JSONL is **not** an ACL-aware evidence database: keep files outside git and restrict access.
- `INGESTION_OUTPUT_MODE=canonical` with PostgreSQL checkpoints and an approved private `GCS_EVIDENCE_BUCKET` archives raw source evidence to GCS and persists canonical revisions in PostgreSQL.
- Source items are durably flushed before their checkpoints advance. Work-item batch failures prevent advancing beyond potentially missing data.
- `ADO_PROJECTS` selects **project names**, not repositories. `ADO_REPOS` narrows **only repository streams** (PRs/commits). Work items remain project-wide; builds remain project-wide unless `ADO_PIPELINE_IDS` limits them. The work-item Recycle Bin is queried across the project even for a time-scoped delta.
- Current work-item snapshots and historical native revisions are stored separately.
- Active PRs are re-enriched in every scheduled delta. Local JSONL may repeat an unchanged active PR in distinct run folders; canonical revisions require separate content-based deduplication.

### Webhooks, retry and reconciliation

- Authenticated work-item service-hook notifications are only hints; a provider handler re-fetches the current record. The source-change inbox supports claim leases, replay controls, bounded retries and dead letters.
- The scheduled reconciliation command `sync --reconcile-days N`, where N is 1–90, re-reads a recent window **without advancing the normal delta checkpoint**.
- Unreadable items are quarantined with sanitized reason and attempt count. A later successful read resolves the entry. A partial stream is not presented as a clean successful history import.
- Source sync audit entries record mode and stream outcomes in canonical operation.

### Confluence security

Confluence reads configured space metadata and page/ancestor restrictions **before** filtering candidate content by `--since`. Restricted or unverified page bodies are **withheld from local JSONL**. A run with withheld candidates reports `Partial`, returns exit code **2**, and does **not** advance its Confluence checkpoint. ACL diagnostic snapshots may be written for all enumerated pages.

Known reporting improvement: withheld-by-policy pages appear in the reason text while the numeric `NotStored` field may remain zero. Record this gap and improve count reconciliation; do not change source permissions to force a green run.

## Repeatable PowerShell test workflow

Run on an approved nonproduction scope from the repository root. Use read-only source credentials via approved secure configuration.

```powershell
git branch --show-current
git rev-parse HEAD
git status --short
$env:ADO_ORG_URL = 'https://dev.azure.com/<approved-org>'
$env:ADO_PROJECTS = '<approved-project-name>'
$env:ADO_REPOS = '<approved-repository-name>'
$env:CHECKPOINT_STORE = 'file'
$env:INGESTION_OUTPUT_MODE = 'jsonl'
$env:INGESTION_OUTPUT_DIR = Join-Path $env:LOCALAPPDATA 'VisionIQ\LRN05-Sandbox'

dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- inventory

# Narrow approved repository backfill, then a delta with the SAME checkpoint store.
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- sync --source commits,pull-requests --since 2026-01-01T00:00:00Z --full
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- sync --source commits,pull-requests

# The reconciliation sweep must not change the saved checkpoint file.
$checkpoint = Join-Path $env:INGESTION_OUTPUT_DIR 'checkpoints.json'
$beforeHash = (Get-FileHash $checkpoint -Algorithm SHA256).Hash
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- sync --source commits,pull-requests --reconcile-days 7
$afterHash = (Get-FileHash $checkpoint -Algorithm SHA256).Hash
Write-Host "Checkpoint unchanged:" ($beforeHash -eq $afterHash)
```

Use a **separate isolated output folder** for work-item tests. ADO Task/Bug records belong to an ADO **project**, not exclusively to a test repository, so creation is visible to users with project/Area Path permissions. Use an approved synthetic Task and a recent `--since` value to verify its initial revision, an updated revision, and full `work-item-revisions.jsonl` history without importing the whole historical project.

Build tests must use an approved short time window and, if required, `ADO_PIPELINE_IDS`. A build sync does not start a build.

A Confluence `--since` filter does **not** guarantee a limited source metadata read. Obtain approval for permission/metadata scanning of the configured space and never publish JSONL body/ACL exports.

## Automated tests used in the hands-on validation

```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter 'FullyQualifiedName~SyncQuarantineTests|FullyQualifiedName~CheckpointDurabilityTests|FullyQualifiedName~WebhookAndReconcileTests'
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter 'FullyQualifiedName~CanonicalEvidenceSinkTests|FullyQualifiedName~Lrn03EvidenceBoundaryTests'
dotnet test .\tests\VisionIQ.Api.Tests\VisionIQ.Api.Tests.csproj --configuration Release --filter 'FullyQualifiedName~EvidenceIngestionTests'
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter 'FullyQualifiedName~ConfluenceTests|FullyQualifiedName~ConfluenceEvidenceAccessTests|FullyQualifiedName~ConfluenceReviewRegressionTests|FullyQualifiedName~ConfluenceSpaceAccessTests|FullyQualifiedName~ConfluenceAncestorLoopTests|FullyQualifiedName~ConfluenceEnrichPagingTests'
```

An additional real PostgreSQL test uses a **migrated disposable database** and a simulated GCS archive. Never point its opt-in environment variables at a company Cloud SQL/shared development database.

```powershell
# Verify disposable DB_HOST / DB_PORT / DB_NAME and restricted DB_API_USER first.
$env:VISIONIQ_EVIDENCE_DB_INTEGRATION = '1'
dotnet test .\tests\VisionIQ.Api.Tests\VisionIQ.Api.Tests.csproj --configuration Release --filter 'FullyQualifiedName~EvidenceDatabaseIntegrationTests' --logger 'console;verbosity=normal'
Write-Host "DB integration exit code:" $LASTEXITCODE
```

A separate earlier disposable-DB phase also exercised source-change, source-audit, ticket-fix and Python quarantine database tests. Their exact counts and full SHA were not reproduced in this public guide. **Skipped** database tests do not count as passes.

## Sanitized test evidence — 5 November 2026

The following evidence was gathered on a **separate private company `dev` checkout** and reported during hands-on acceptance; it does not establish identical pass counts on this public GitHub repository's `main`. Complete source names, internal run paths, test IDs, full SHA and output files belong in private acceptance records.

| Check | Observed result | Decision |
|---|---|---|
| Real ADO and Confluence inventory | Connected and inventoried approved scope | **PASS** for connectivity, not for history import |
| Restricted repository backfill + delta | Commit/PR streams synced; unchanged delta produced no new commits | **PASS** for selected time window |
| New commit and PR | Both surfaced in the next delta | **PASS** |
| Active PR edit | Updated PR title found in fresh snapshot | **PASS** |
| Seven-day reconciliation | Streams synced and checkpoint SHA-256 stayed identical | **PASS** |
| New/updated Task | Initial record detected and higher revision received | **PASS** |
| Task revision history | Six native revisions preserved in output | **PASS** |
| ADO completed builds | Nonzero source build records and declared links read | **PASS** for configured period |
| Failure, quarantine, webhook and checkpoint fixtures | **23 passed, 0 failed, 0 skipped** | **PASS** |
| Canonical sink/boundary tests | **16 passed, 0 failed, 0 skipped** | **PASS** |
| Evidence ingestion fixtures | **10 passed, 0 failed, 0 skipped** | **PASS** |
| Disposable PostgreSQL revision replay/idempotency | **1 passed, 0 failed, 0 skipped**; GCS archive simulated | **PASS** for PostgreSQL |
| Confluence restriction/paging fixtures | **36 passed, 0 failed, 0 skipped** | **PASS** |
| Live Confluence JSONL sync | One eligible recent page exported, restricted/unknown recent candidates withheld; `Partial`, exit 2, no checkpoint | **PASS** for fail-closed access; complete ingestion **OPEN** |
| Approved source-wide historical import | Not performed or independently reconciled to total history | **OPEN** |
| Live GCS raw archive / Cloud SQL canonical replay | Not tested as a single live deployed chain | **OPEN — FND-06** |
| Live authenticated webhook + scheduled Cloud Run job | Not verified end-to-end | **OPEN — FND-06** |

The specifically reported fixture/test runs above total **86 passed, zero failed and zero skipped**. This is **not** the total test count for the repository.

### Operational cautions

- Partial Confluence with withheld records is **not a successful complete-source import**, but may be correct fail-closed behavior.
- Do not use unrestricted `sync --full` against a large shared source or sync company data to a public repository.
- Work-item deletion reporting is project-wide; it observes deleted source records and does **not** delete source work items.
- Record full SHA, dirty/clean status, test commands, warnings, passed/failed/skipped, provider scopes, expected/actual counts, restricted artifacts and cleanup for every sign-off.

## Outstanding acceptance / FND-06 handoff

- [ ] Complete and count-reconcile an approved **full historical backfill** for each committed source scope; verify interruptions, resumability and no lost or duplicate canonical revisions.
- [ ] Validate private GCS bucket IAM, retention, immutability, content hash, fail-closed access and archive-before-database behavior **with approved synthetic enterprise-like evidence**.
- [ ] Validate migrated Cloud SQL runtime roles and canonical revisions, audits, quarantine and checkpoints on an approved nonproduction target.
- [ ] Validate the actual Cloud Run job/service, authenticated source hooks, retries/dead letters and scheduled reconciliation. Manual Artifact Registry deployment is acceptable; Cloud Build automation is a separate access-blocked task.
- [ ] Investigate unresolved Confluence ancestor/page permissions without loosening them to force a green status.
- [ ] Improve Confluence withheld-versus-`NotStored` reporting and audit consistency.
- [ ] Re-execute relevant tests against the intended release full commit SHA and capture formal approval.

**Decision: LRN-05 local behavior and the tested real-source windows have PASSED. The complete LRN-05 story remains OPEN/CONDITIONAL until full-history and cloud-dependent requirements are demonstrated.** See also [SYNC-WEBHOOKS.md](SYNC-WEBHOOKS.md) and the Developer Team Guide PDF.
