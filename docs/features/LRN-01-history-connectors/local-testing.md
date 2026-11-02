# LRN-01 — Local testing guide (Windows PowerShell)

## Prerequisites

- Source checkout on `feature/learn`, .NET SDK matching project, permitted read-only ADO credentials, PostgreSQL test instance if using canonical mode.
- Explicitly approved ADO project and repository: the recorded trial used **WLMTS / Genoa Kiosk / KIOSK_TVM**.
- Never print `.env`, tokens, raw work-item comments or unredacted JSONL into test reports.
- Confirm `INGESTION_OUTPUT_MODE`, `CHECKPOINT_STORE`, `DB_HOST`, `DB_PORT`, `GCS_EVIDENCE_BUCKET` **before any write**.

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

## 4. Normal incremental sync

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

**Important:** `ADO_REPOS=KIOSK_TVM` limits repository selection; it does **not** necessarily limit project-level CI build ingestion. Verify pipeline IDs/scopes separately before running against broader datasets.

## 5. Understand results

- `Stream ...: Synced`, zero `not stored` and `quarantined`, exit **0**: source processing completed for this run.
- `NOT SYNCED` / exit **2**: investigate; do not report an inaccessible source as empty.
- `report.json`: compare stream counts, start/end times and run ID; don't share raw source content.
- Second delta run with zero new records: useful checkpoint/replay evidence, not proof that the historical dataset is exhaustive.

## 6. Inspect isolated checkpoint rows

Recorded disposable PostgreSQL container:

```powershell
docker exec visioniq-lrn01-test-postgres psql -U visioniq -d visioniq -c "SELECT source_id, scope, cursor_value, updated_at FROM evidence.connector_checkpoints WHERE source_id = 'WLMTS/ado' ORDER BY scope;"
```

Never reset checkpoints or delete GCS objects to make a test pass.

## 7. Full current-branch regression (pending)

```powershell
dotnet test .\VisionIQ.sln --configuration Release
Write-Host "Solution exit code: $LASTEXITCODE"
```

Note any **skipped** integration tests; they are not passes. See [acceptance](acceptance.md) for exact unverified gates.
