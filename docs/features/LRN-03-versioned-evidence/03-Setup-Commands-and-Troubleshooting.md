# LRN-03 — Setup, commands and troubleshooting (Windows PowerShell)

**Use this guide with the isolated LRN-03 PostgreSQL test container only.** Never run a mutation against the normal VisionIQ database or Cloud SQL by accident. Work from the repository root on `feature/learn`.

## What to run and when

| Operation | Safe classification | What it does |
| --- | --- | --- |
| `dotnet test` focused filter | Local synthetic test | Tests service behavior without real PostgreSQL/GCS |
| `docker ps -a`, `pg_isready` | Read-only | Checks disposable database runtime |
| `python -m db.manage migrate` | Database write | Applies migrations to configured local DB |
| `python -m db.manage roles` | Database write | Creates/updates runtime roles and schema permissions |
| `python -m db.manage verify` | Database write during verification | Creates/drops isolated verification tables; checks privileges |
| `python -m db.manage all` | Multiple writes | Runs migrate + roles + verify; **not** a routine test |
| `python -m db.manage bootstrap` | **INVALID** | Internal Python function, not a CLI action |

### Actual CLI choices

The `main()` parser in `db/manage.py` accepts exactly `migrate`, `roles`, `verify`, and `all`. **Earlier guidance to run `bootstrap` was incorrect**; it returned exit **2** with `invalid choice`, and did not execute role configuration. The correct command is `roles`.

## Isolated database currently used

- Container: `visioniq-lrn03-test-postgres`
- Host: `127.0.0.1`
- Port: `55433` (not the usual local 5432)
- Database: `visioniq_lrn03_test`
- Admin user: `visioniq_test`
- Alembic schema at `20261101_0012` — user verified
- **Keep the PowerShell session with the temporary credentials. Never print or commit passwords.**

## Step A — Read-only readiness checks

```powershell
docker ps -a --filter "name=visioniq-lrn03-test-postgres" --format "table {{.Names}}\t{{.Status}}\t{{.Ports}}"
docker exec visioniq-lrn03-test-postgres pg_isready -U visioniq_test -d visioniq_lrn03_test
if ($LASTEXITCODE -ne 0) { throw "Disposable PostgreSQL database is not ready" }
```

Observed on 2 November 2026: `Up`, `127.0.0.1:55433->5432/tcp`, accepting connections, exit 0.

## Step B — Configure runtime roles (NEXT COMMAND)

The three distinct database passwords were already reported as configured. First validate that **all** effective target settings identify the disposable database.

```powershell
if (
  $env:DB_TARGET -ne "local" -or
  $env:DB_HOST -ne "127.0.0.1" -or
  $env:DB_PORT -ne "55433" -or
  $env:DB_LOCAL_NAME -ne "visioniq_lrn03_test" -or
  $env:DB_LOCAL_USER -ne "visioniq_test" -or
  $env:DB_API_USER -ne "visioniq_lrn03_api" -or
  $env:DB_INTELLIGENCE_USER -ne "visioniq_lrn03_intelligence" -or
  [string]::IsNullOrWhiteSpace($env:DB_LOCAL_PASSWORD) -or
  [string]::IsNullOrWhiteSpace($env:DB_API_PASSWORD) -or
  [string]::IsNullOrWhiteSpace($env:DB_INTELLIGENCE_PASSWORD)
) {
  throw "Incorrect or incomplete isolated test database configuration"
}

python -m db.manage roles
if ($LASTEXITCODE -ne 0) { throw "Isolated role configuration failed" }
```

**Expected:** `Database roles and schema permissions configured.` Do not mark PASSED until the real output is observed. This action changes users/grants **only** in the configured disposable database.

## Step C — Verify roles (after Step B passes)

```powershell
python -m db.manage verify
Write-Host "Verification exit code: $LASTEXITCODE"
```

**Important:** this verification can create/drop test tables in the isolated database. It is **not read-only**, despite its name. Run it only with the same safe target configuration.

## Step D — Read-only migration evidence

```powershell
docker exec visioniq-lrn03-test-postgres psql -U visioniq_test -d visioniq_lrn03_test -c "SELECT version_num FROM alembic_version;"
docker exec visioniq-lrn03-test-postgres psql -U visioniq_test -d visioniq_lrn03_test -c "\dt evidence.*"
```

Recorded: version `20261101_0012`, 14 evidence tables, including `source_items` and `source_revisions`.

## Troubleshooting history and lessons

| Observation | Root cause / action |
| --- | --- |
| `docker container inspect` showed No such container | Inspecting a nonexistent container is noisy in Windows PowerShell. For existence, use `docker ps -a --format "{{.Names}}"` |
| `RandomNumberGenerator.Fill` method missing | Windows PowerShell may not expose newer .NET static method; use `RandomNumberGenerator.Create().GetBytes()` |
| First disposable container exited 1 | Docker logs confirmed empty `POSTGRES_PASSWORD`; regenerated password and recreated **only** disposable container |
| `pg_isready` exit 0 | Confirms server accepting connections, not that migration/role permissions are correct |
| `python -m db.manage bootstrap` exit 2 | **Wrong CLI action**, no DB bootstrap attempted; run `roles` instead |
| Connection attempted on port 5432 | **STOP:** normal VisionIQ container uses that port. The LRN-03 test must use **55433** |
| Python traceback mentions DB permissions | Capture sanitized error; do not switch to admin credentials in application configuration to force test success |

**Safety concern:** original local VisionIQ container was observed publishing `0.0.0.0:5432`. Review its bind/firewall in a separate maintenance activity; don't restart it during LRN-03 verification.

## End-of-test cleanup

After **all tests** and documentation are complete, stop and remove only `visioniq-lrn03-test-postgres` by its exact name. Do not run `docker compose down -v`, which may delete the ordinary project volume. Cleanup is deliberately deferred until acceptance evidence is captured.
