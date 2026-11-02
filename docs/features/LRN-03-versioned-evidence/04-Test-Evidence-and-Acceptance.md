# LRN-03 — Actual test evidence and acceptance register

**Source of truth:** [LRN-03 Trello user story](https://trello.com/c/aqRFYZ9G/9-learn-lrn-03-store-history-as-versioned-evidence). Mark only executed, observed tests as PASS. All synthetic sample data mentioned here is fictional.

## Executed evidence — 2 November 2026

| Test/gate | Observed output | Status | Boundary |
| --- | --- | --- | --- |
| Synthetic focused service tests | `4 succeeded, 0 failed, 0 skipped`, net10.0, exit **0**, build succeeded | PASS | Archive ordering, archive failure, identity separation, metadata redaction through fakes |
| Disposable PostgreSQL container | `visioniq-lrn03-test-postgres`, localhost 55433, `pg_isready` exit **0** | PASS | Server readiness only |
| Alembic migration on isolated DB | `20261101_0012` | PASS | Migration applied in isolated database |
| Evidence table inventory | **14** evidence tables; `source_items`, `source_revisions` present | PASS | Table existence, not row correctness |
| First `db.manage bootstrap` command | CLI `invalid choice: 'bootstrap'`, exit **2** | COMMAND ERROR | Earlier command was wrong; correct CLI action is `roles`; not a database/bootstrap logic failure |
| `db.manage roles` | User reports completed; exact stdout/exit code not supplied | REPORTED COMPLETE | Runtime role/bootstrap verification |
| Two revisions + identical replay | User-run targeted xUnit: 1 succeeded, 0 failed, 0 skipped; exit 0; build succeeded with 63 warnings | **PASS — targeted integration** | Test asserts 1 logical item, 2 revisions, correct latest revision, replay statuses; no actual GCS |
| Existing revision ID + altered content | Included in same successful targeted xUnit integration test | **PASS — targeted integration** | `Conflict` return asserted; database revision count remains 2 |
| GCS raw snapshot integrity/immutability | No new LRN-03 run supplied | PENDING | Prior LRN-01 5/5 GCS hash sample is supporting context only |
| End-user Confluence ACL retrieval | Not executed | OUTSIDE STORAGE GATE / OPEN DEPENDENCY | LRN-02 authorization handoff |

## Acceptance criteria (original Trello wording)

- [ ] Each record keeps its source ID, revision, timestamps and access list. **Schema/code located; actual stored row test pending.**
- [ ] A raw copy is kept in Cloud Storage; sensitive fields are redacted before indexing. **Service code and fake-backed tests passed; live archive and indexing checks pending.**
- [x] Re-importing the same item updates it instead of creating a duplicate. **Targeted opt-in PostgreSQL integration test passed (1/1); synthetic source item and two revisions, replay and conflict asserted.**

**Completion evidence:** the same item imported twice leaves **one logical record with two source revisions** (after introducing the second distinct revision and replaying records). Do not mark complete before verifying counts and latest pointer in the disposable PostgreSQL database.

## Result caveats and next gate

The successful targeted integration does **not** prove a GCS object was created: the test uses a synthetic archive returning a `gs://` URI. Next audit the concrete GCS archive implementation and verify actual object integrity and immutability using an approved isolated bucket. Existing build warnings (63) are not test failures but need separate severity review. The two other original acceptance criteria require real stored-record field checks and GCS/index safety before signoff.

## Documentation policy

- Store **exact commands and sanitized results** here as soon as each test passes or fails.
- Track failures with root cause and corrective action; do not hide failed attempts.
- Never include tokens, passwords, confidential pages, source descriptions or unrestricted company identifiers.
- PR stays Draft; no cloud deployment or main-branch merge is authorized by these local checks.

## Verified role isolation — 2 November 2026

User executed `python -m db.manage verify` against the disposable local database, observing **`pgvector, full-text search and role isolation verified.`**, exit code **0**. **PASS:** capabilities and runtime schema isolation. This does not yet prove item/revision replay semantics.

## Next action

Execute a synthetic item + two revisions through the real `PostgresEvidenceStore`, verify the database row counts, latest revision and conflict semantics. This integration test remains pending.

## Real PostgreSQL replay test — ready for local execution

An opt-in xUnit test in `tests/VisionIQ.Ingestor.Tests/Lrn03PostgresIntegrationTests.cs` exercises the **actual PostgresEvidenceStore**, using unique synthetic evidence and a fake in-memory archive URI. It checks Created → NewRevision → Unchanged → Unchanged → Conflict, one source item, two revisions, latest ID `2`, and preserved ACL/redaction fields. It cleans up only its own synthetic item. **It does not contact real GCS.**

```powershell
# Run only in the existing isolated test-db PowerShell session.
if ($env:DB_HOST -ne "127.0.0.1" -or $env:DB_PORT -ne "55433" -or
    $env:DB_LOCAL_NAME -ne "visioniq_lrn03_test" -or
    $env:DB_API_USER -ne "visioniq_lrn03_api" -or
    [string]::IsNullOrWhiteSpace($env:DB_API_PASSWORD)) {
    throw "Isolated LRN-03 database configuration required"
}
$env:LRN03_TEST_DB = "1"
try {
    dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter "FullyQualifiedName~Lrn03PostgresIntegrationTests" --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw "LRN-03 PostgreSQL integration failed" }
}
finally {
    Remove-Item Env:LRN03_TEST_DB -ErrorAction SilentlyContinue
}
```

**Execution evidence, 2 November 2026:** user supplied the targeted xUnit log: `Test summary: total: 1, failed: 0, succeeded: 1, skipped: 0`, `Build succeeded with 63 warning(s) in 32.0s`, `Integration test exit code: 0`. Prior command set `LRN03_TEST_DB=1` before `dotnet test`. Test source verifies Created → NewRevision → Unchanged → Unchanged → Conflict, exact 1 item / 2 revisions, latest ID 2, ACL metadata, and redaction; it deletes its unique synthetic rows. **Qualification:** its early return when `LRN03_TEST_DB` is missing means the log alone does not independently show the opt-in environment state. For a future audit-grade rerun, make missing opt-in fail or use an explicit test trait/configuration gate. **No real GCS interaction was performed** and 63 build warnings remain to triage.
