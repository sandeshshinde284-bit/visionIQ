# LRN-01 — Local testing guide (Windows PowerShell)

## Prerequisites

- Source checkout on `feature/learn`, .NET SDK matching project, permitted read-only ADO credentials, PostgreSQL test instance if using canonical mode.
- Explicitly approved ADO project and repository: the recorded trial used **WLMTS / Genoa Kiosk / KIOSK_TVM**.
- Never print `.env`, tokens, raw work-item comments or unredacted JSONL into test reports.
- Confirm `INGESTION_OUTPUT_MODE`, `CHECKPOINT_STORE`, `DB_HOST`, `DB_PORT`, `GCS_EVIDENCE_BUCKET` **before any write**.

## How to use this guide

**Do not run every section sequentially.** Sections 1 and 3 are safe verification workflows; section 4 **writes evidence** and is for approved integration runs only; section 6 references a historical disposable database container and may not exist on your machine. The recorded counts are dated evidence, not guaranteed output on every run.

### Quick daily verification (no evidence ingestion)

```powershell
git branch --show-current
dotnet build .\VisionIQ.sln --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
dotnet test .\VisionIQ.sln --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
```

**Expected:** branch `feature/learn`, build exit 0, nonzero number of discovered tests, 0 failing tests. **If different:** stop; do not start ingestion. The historical 402/402 pass applies to the recorded code version, not a future commit.

## 1. Validate code without contacting ADO

```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --no-restore
Write-Host "Exit code: $LASTEXITCODE"
```

For a focused failure-recovery suite:

```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~StreamIsolationTests|FullyQualifiedName~CanonicalEvidenceSinkTests|FullyQualifiedName~AdoWorkItemChangeHandlerTests|FullyQualifiedName~SyncRunnerTests|FullyQualifiedName~TicketFixProjectionSinkTests"
Write-Host "Failure recovery exit code: $LASTEXITCODE"
```

Observed on 2026-11-02: **30 passed, 0 failed, 0 skipped, exit 0**.

## 2. Check configuration without exposing secrets

**Important:** the values below show only process environment variables, not necessarily values loaded from the local `.env` file. Blank values **do not prove that the application will not write** to an external destination. Inspect the effective application's safe startup diagnostics and confirm the approved destination before any sync.

```powershell
$env:ADO_REPOS = "KIOSK_TVM"
"Output mode: $env:INGESTION_OUTPUT_MODE"
"Checkpoint store: $env:CHECKPOINT_STORE"
"Database host: $env:DB_HOST"
"Database port: $env:DB_PORT"
"Evidence bucket: $env:GCS_EVIDENCE_BUCKET"
```

If any destination is unexpected, **stop**. Don't run ingestion yet.

## 3. Inspect accessible source inventory

```powershell
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- inventory
Write-Host "Inventory exit code: $LASTEXITCODE"
```

Inventory reports discovered repositories and pipeline **definitions**, not proof that their commits/build runs have been archived.

## 4. Normal incremental sync — WRITE OPERATION

**Not a daily read-only test.** Ingestion can write local files, database rows, cloud evidence objects and checkpoint positions. Only execute these examples when both the source scope and actual resolved output destinations have been approved. For source connectivity without writes to evidence, use `inventory` above.

Run **only if the destinations above are approved**:

```powershell
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- sync --source work-items
Write-Host "Work items exit code: $LASTEXITCODE"
```

For repo-specific Git:

```powershell
$env:ADO_REPOS = "KIOSK_TVM"
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- sync --source commits,pull-requests
Write-Host "Git ingestion exit code: $LASTEXITCODE"
```

For project-level CI:

```powershell
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- sync --source builds
Write-Host "Build ingestion exit code: $LASTEXITCODE"
```

**Important:** `ADO_REPOS` limits **repositories only** (commits and pull requests). CI build ingestion is **never** limited by `ADO_REPOS`: builds are limited only by `ADO_PIPELINE_IDS` (comma-separated pipeline definition IDs; empty means every pipeline in the approved projects). Set `ADO_PIPELINE_IDS` before running `--source builds` against a large project. `inventory` reports only **how many** pipeline definitions exist; to find an ID, open the pipeline in Azure DevOps (Pipelines → select it): the URL contains `definitionId=<number>`.

## 5. Understand results

- `Stream ...: Synced`, zero `not stored` and `quarantined`, exit **0**: source processing completed for this run.
- `NOT SYNCED` / exit **2**: investigate; do not report an inaccessible source as empty.
- `report.json`: compare stream counts, start/end times and run ID; don't share raw source content.
- Second delta run with zero new records: useful checkpoint/replay evidence, not proof that the historical dataset is exhaustive.

## 6. Inspect isolated checkpoint rows — HISTORICAL TEST ENVIRONMENT ONLY

The following command refers to a disposable container used in the recorded test. It is **not** a general VisionIQ command. It will fail if that container or schema is absent; do not create or alter production databases merely to reproduce it.

Recorded disposable PostgreSQL container:

```powershell
docker exec visioniq-lrn01-test-postgres psql -U visioniq -d visioniq -c "SELECT source_id, scope, cursor_value, updated_at FROM evidence.connector_checkpoints WHERE source_id = 'WLMTS/ado' ORDER BY scope;"
```

Never reset checkpoints or delete GCS objects to make a test pass.

## 7. Full current-branch regression (completed on 2026-11-02)

```powershell
dotnet test .\VisionIQ.sln --configuration Release
Write-Host "Solution exit code: $LASTEXITCODE"
```

Previously recorded result (not a fresh run after the LRN-03 acceptance changes): **402 passed, 0 failed, 0 skipped, exit 0**, build successful with **66 warnings**. These are analyzer advisories, not errors: 15 × `CA1873` in production code (logging performance, already fixed on `feature/learn-08-14`) and performance/nullable advisories in test code; see [acceptance](acceptance.md) for the breakdown. The exact count varies by run (63 was recorded on an earlier run) and `NU1900` appears when NuGet's vulnerability feed is unreachable. Re-run after changes and note any skipped integration tests separately.
