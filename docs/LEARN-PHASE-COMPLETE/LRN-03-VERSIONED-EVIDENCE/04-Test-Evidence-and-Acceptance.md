# LRN-03 — Actual test evidence and acceptance register

> **Decision:** **LRN-03 COMPLETE (original storage scope)**, 2 November 2026. All three Trello
> acceptance criteria passed with real PostgreSQL and Cloud Storage tests; see
> [Final acceptance decision](#final-acceptance-decision--2-november-2026). The sections after
> "Documentation policy" are the **dated test history**, kept for traceability; read them in order.

**Source of truth:** [LRN-03 Trello user story](https://trello.com/c/aqRFYZ9G/9-learn-lrn-03-store-history-as-versioned-evidence). Mark only executed, observed tests as PASS. All synthetic sample data mentioned here is fictional.

## Executed evidence — 2 November 2026

| Test/gate | Observed output | Status | Boundary |
| --- | --- | --- | --- |
| Synthetic focused service tests | `4 succeeded, 0 failed, 0 skipped`, net10.0, exit **0**, build succeeded | PASS | Archive ordering, archive failure, identity separation, metadata redaction through fakes |
| Disposable PostgreSQL container | `visioniq-lrn03-test-postgres`, localhost 55433, `pg_isready` exit **0** | PASS | Server readiness only |
| Alembic migration on isolated DB | `20261101_0012` | PASS | Migration applied in isolated database |
| Evidence table inventory | **14** evidence tables; `source_items`, `source_revisions` present | PASS | Table existence, not row correctness |
| First `db.manage bootstrap` command | CLI `invalid choice: 'bootstrap'`, exit **2** | COMMAND ERROR | Earlier command was wrong; correct CLI action is `roles`; not a database/bootstrap logic failure |
| `db.manage roles` | User reports completed; exact stdout/exit code not supplied | REPORTED COMPLETE | Confirmed indirectly by the passing `db.manage verify` role-isolation check below |
| Two revisions + identical replay | User-run targeted xUnit: 1 succeeded, 0 failed, 0 skipped; exit 0; build succeeded with 63 warnings | **PASS — targeted integration** | Test asserts 1 logical item, 2 revisions, correct latest revision, replay statuses; no actual GCS |
| Existing revision ID + altered content | Included in same successful targeted xUnit integration test | **PASS — targeted integration** | `Conflict` return asserted; database revision count remains 2 |
| GCS raw snapshot integrity/immutability | Retried 2026-11-02 after ADC reauthentication; 1/1 passed, 0 failed, 0 skipped, exit 0 (14.6s); 65 warnings | **PASS — real isolated GCS** | One synthetic object: exact bytes, SHA-256, unchanged generation on replay; broader IAM/indexing not tested |
| Downstream indexable metadata safety | User-run focused xUnit: 8 succeeded, 0 failed, 0 skipped, exit 0; build succeeded with 65 warnings | **PASS — normalization boundary** | Actual deployed index writer not established |
| PostgreSQL persisted-metadata acceptance (final) | 1 succeeded, 0 failed, 0 skipped; exit 0; 2.9 s | **PASS — real isolated PostgreSQL** | All persisted identity, revision, timestamp, URL, retrieval identity, ACL, redaction and archive columns asserted |
| End-user Confluence ACL retrieval | Not executed | OUTSIDE STORAGE GATE / OPEN DEPENDENCY | LRN-02 authorization handoff |

## Acceptance criteria (original Trello wording)

- [x] Each record keeps its source ID, revision, timestamps and access list. **Final isolated PostgreSQL test passed (1/1), including real persisted-column assertions.**
- [x] A raw copy is kept in Cloud Storage; sensitive fields are redacted before indexing. **Live isolated GCS integrity/replay test passed (1/1); metadata redaction boundary passed (8/8).** Actual semantic indexing/retrieval is separate future scope.
- [x] Re-importing the same item updates it instead of creating a duplicate. **Targeted opt-in PostgreSQL integration test passed (1/1); synthetic source item and two revisions, replay and conflict asserted.**

**Completion evidence:** the same item imported twice leaves **one logical record with two source revisions** (after introducing the second distinct revision and replaying records). Do not mark complete before verifying counts and latest pointer in the disposable PostgreSQL database.

## Result caveats at the time (history)

*Written before the GCS and final PostgreSQL tests; both caveats were later resolved.*

The successful targeted integration does **not** prove a GCS object was created: the test uses a synthetic archive returning a `gs://` URI. Next audit the concrete GCS archive implementation and verify actual object integrity and immutability using an approved isolated bucket. Existing build warnings (63) are not test failures but need separate severity review. The two other original acceptance criteria require real stored-record field checks and GCS/index safety before signoff.

## Documentation policy

- Store **exact commands and sanitized results** here as soon as each test passes or fails.
- Track failures with root cause and corrective action; do not hide failed attempts.
- Never include tokens, passwords, confidential pages, source descriptions or unrestricted company identifiers.
- PR stays Draft; no cloud deployment or main-branch merge is authorized by these local checks.

## Verified role isolation — 2 November 2026

User executed `python -m db.manage verify` against the disposable local database, observing **`pgvector, full-text search and role isolation verified.`**, exit code **0**. **PASS:** capabilities and runtime schema isolation. This does not yet prove item/revision replay semantics.

## Next action at the time (history)

Execute a synthetic item + two revisions through the real `PostgresEvidenceStore`. **Done:** see the next section.

## Real PostgreSQL replay test (executed: PASS)

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

## GCS implementation review — 2 November 2026

Concrete class `CloudStorageEvidenceArchive` is in `src/VisionIQ.Api/Features/Evidence/RawEvidenceArchive.cs`. Its object key contains item identity, SHA-256 of native revision, and SHA-256 of raw payload. It uploads with `IfGenerationMatch = 0`, catching only HTTP 412 to treat the same content-addressed key as existing rather than overwriting it. This supports design-level immutability. A new optional real-bucket xUnit test checks downloaded raw bytes, SHA-256 and unchanged generation on replay, which was then executed (see the next two sections). See [GCS runbook](05-GCS-Archive-Verification.md).

## Live GCS test attempt — 2 November 2026

User executed `Lrn03GcsArchiveIntegrationTests.Real_cloud_storage_archive_is_immutable_idempotent_and_exact` against newly created isolated test bucket (set through `GCS_EVIDENCE_BUCKET` for that run). **Actual:** total 1, failed 1, passed 0, skipped 0; exit 1; 65 build warnings. Stack trace originates in `Google.Apis.Auth.OAuth2.UserCredential.RefreshTokenAsync` and `Google.Cloud.Storage.V1.StorageClientImpl.UploadHelper.ExecuteAsync`, before successful object upload, with OAuth `invalid_grant` / `reauth related error (invalid_rapt)`.

**Diagnosis:** ADC reauthentication required; no evidence of an archive logic defect or a stored test object. **Next:** run `gcloud auth application-default login`, then `gcloud auth application-default print-access-token *> $null` and check exit 0. Rerun the same isolated-bucket GCS integration test; do not mark acceptance PASS without output. Do not paste access tokens or credentials.

## Successful live GCS retry — 2 November 2026

After reauthentication of Application Default Credentials, user reran `Lrn03GcsArchiveIntegrationTests.Real_cloud_storage_archive_is_immutable_idempotent_and_exact` against isolated bucket (set through `GCS_EVIDENCE_BUCKET` for that run). Observed: `total: 1, failed: 0, succeeded: 1, skipped: 0`, duration 14.6 s, **exit code 0**. Build succeeded with **65 warnings**. The test source checks byte-for-byte download, SHA-256 hash, same GCS URI and unchanged object generation for the replay. The test deletes its uniquely named object on success. This resolves the earlier `invalid_rapt` authentication blocker. **Not validated by this single test:** bucket-wide retention and IAM; independent indexing pipeline safety; every production source/ACL mapping.

## Downstream indexing redaction review — 2 November 2026

**Code-level finding:** `EvidenceNormalizer.SafeFields` previously copied `commentTruncated` for commits and `environmentRaw` / `environmentReason` / `environment` for work item current records, even though these source-derived strings could contain free text or sensitive values. These have been removed from the normalized indexable metadata allowlist. The safe work-item-current fields now include `state`, `workItemType`, `environmentStatus`, `environmentSource`; commit records include only `kind`. The raw original still belongs in authorized GCS archive, not a search projection.

**Regression coverage added:** `Lrn03EvidenceBoundaryTests.Indexable_metadata_never_contains_synthetic_sensitive_source_text` has four synthetic examples covering commit commentary, work-item environment raw/reason, work-item description and Confluence page text. **Executed later: 8/8 PASS** (see the next section).

```powershell
git switch feature/learn
git pull
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter "FullyQualifiedName~Lrn03EvidenceBoundaryTests" --verbosity minimal
Write-Host "LRN-03 redaction tests exit code: $LASTEXITCODE"
```

**Important distinction:** tests prove the normalization/ingestion **boundary** excludes specified text and PostgreSQL stores its `redacted_payload`. They do **not** prove there is an actual deployed downstream vector/full-text index consuming only this JSON. A deployment-specific index writer or retrieval path must be located and tested independently before full indexing signoff. Existing stored rows may need a controlled backfill/reindex to pick up normalization changes; there is no automatic retroactive rewrite in this patch.

## Verified redaction regression — 2 November 2026

The user executed `dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter "FullyQualifiedName~Lrn03EvidenceBoundaryTests" --verbosity minimal` after the allowlist hardening. Output: **8 tests succeeded, 0 failed, 0 skipped; exit code 0**; build succeeded with **65 warnings** in 22.8 seconds. Tests completed in 2.7 seconds. This verifies the metadata normalization boundary; it does not exercise a deployed search index or retrospectively clean previously stored projections. **Do not equate this with end-to-end indexing signoff.**

## Deployed index/retrieval audit — 2 November 2026

Checked source entry points `src/VisionIQ.Api/Program.cs`, `src/VisionIQ.Ingestor.Cli/Program.cs`, `PostgresEvidenceStore` and targeted repository search. Found canonical ingest to raw GCS and redacted PostgreSQL evidence; .NET API registers a permission-aware graph API, but no demonstrated downstream redacted-metadata index writer or deployed evidence-search endpoint. **Status: not verified**, not evidence of failure or success. The required read-only Cloud Run and PostgreSQL inventory commands, sentinel query test design and requester ACL gate are documented in [06-Index-Retrieval-Deployment-Audit.md](06-Index-Retrieval-Deployment-Audit.md). Do not treat previously passing 8/8 normalizer tests as a deployed retrieval PASS.

## Final persisted metadata acceptance test — 2 November 2026 (executed: PASS)

Updated `tests/VisionIQ.Ingestor.Tests/Lrn03PostgresIntegrationTests.cs` to validate the **actual joined database rows** after importing two synthetic revisions and replaying them. Assertions now cover all identity columns (provider, instance, project, null repository, kind and source external ID); native revision ID; source and observation timestamps plus first/last observed values; HTTPS source URL; trusted retrieval principal; verified ACL and principal IDs; redacted JSON kind/state with private text excluded; `gs://` raw archive reference; and SHA-256 length. The existing assertions for Created → NewRevision → Unchanged → Unchanged → Conflict, one item, two revisions and latest revision remain. **The URI comes from an archive stub; real GCS was separately verified using the live bucket test.**

Run from the existing PowerShell session with the isolated database and credentials:

```powershell
git switch feature/learn
git pull
if ($env:DB_TARGET -ne "local" -or $env:DB_HOST -ne "127.0.0.1" -or
    $env:DB_PORT -ne "55433" -or $env:DB_LOCAL_NAME -ne "visioniq_lrn03_test" -or
    $env:DB_API_USER -ne "visioniq_lrn03_api" -or
    [string]::IsNullOrWhiteSpace($env:DB_API_PASSWORD)) {
    throw "Isolated LRN-03 test database required"
}
$env:LRN03_TEST_DB = "1"
try {
    dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj `
      --configuration Release `
      --filter "FullyQualifiedName~Lrn03PostgresIntegrationTests" `
      --verbosity minimal
    Write-Host "Final LRN-03 metadata test exit code: $LASTEXITCODE"
}
finally {
    Remove-Item Env:LRN03_TEST_DB -ErrorAction SilentlyContinue
}
```

**Status: PASS — user executed final test on 2 November 2026: 1 succeeded, 0 failed, 0 skipped, 2.9 seconds; build succeeded with 65 warnings in 28.6 seconds; final exit code 0.** Original LRN-03 storage acceptance is complete. Deployed semantic indexing and retrieval remain separate, as documented in [06](06-Index-Retrieval-Deployment-Audit.md).

## Final acceptance decision — 2 November 2026

**LRN-03: COMPLETE (original storage scope).** User-run final isolated PostgreSQL persisted-metadata test: **1/1 PASS, 0 failed, 0 skipped, exit 0, 2.9 s**; build success with 65 warnings in 28.6 s. The revised test asserts provider, source instance, project, original external ID, native revision IDs, UTC source/observed timestamps, first/last observed, source URL, retrieval identity, verified ACL/principal IDs, safe redacted metadata and raw archive reference, in addition to idempotency/conflict and latest revision. Real GCS archive integrity/replay **1/1 PASS**, redaction boundary **8/8 PASS**. All three original storage acceptance checks now supported. **Nonblocking follow-ups:** make a missing test opt-in an explicit skip (the GCS test currently fails without `LRN03_TEST_GCS=1`, which turns CI red; see the [README](README.md#follow-ups-non-blocking)); 65 analyzer warnings (advisories, see the [LRN-01 warning review](../LRN-01-history-connectors/acceptance.md)); separate deployed indexing/retrieval and requester authorization; any historic data reindex/backfill. Draft PR remains unmerged.
