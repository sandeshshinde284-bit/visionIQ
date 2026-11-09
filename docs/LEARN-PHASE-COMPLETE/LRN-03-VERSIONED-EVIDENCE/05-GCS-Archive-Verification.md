# LRN-03 — Real GCS raw evidence integrity and immutability

**Status:** concrete GCS implementation audited; live GCS integration **PASSED on 2 November 2026** after ADC reauthentication, using isolated test bucket (set through `GCS_EVIDENCE_BUCKET` for that run). No production evidence or buckets are modified by this documentation.

## What existing code does

`src/VisionIQ.Api/Features/Evidence/RawEvidenceArchive.cs` defines `CloudStorageEvidenceArchive`. The object name is:

```text
raw/v1/{stableItemKey}/{sha256(nativeRevisionId)}/{sha256(rawPayload)}.json
```

The archive uses `UploadObjectOptions { IfGenerationMatch = 0 }`, which permits creation only if the object does not already exist. On a GCS HTTP 412 precondition failure, it treats the existing content-addressed object as an idempotent replay. A changed payload yields a different hash and object name rather than overwriting existing bytes. The client is reused via `Lazy<StorageClient>`.

**Important:** This is code inspection. Whether cloud bucket IAM permits reads, creates and conditional deletion, whether object retrieval matches the expected SHA-256, and whether actual replay retains the same generation require a real cloud check.

## Live integration test (existing implementation, new regression coverage)

`tests/VisionIQ.Ingestor.Tests/Lrn03GcsArchiveIntegrationTests.cs` invokes the **real** `CloudStorageEvidenceArchive` using entirely fictional JSON, checks the exact downloaded bytes and SHA-256, and re-uploads the same native revision to verify the GCS object generation does **not** change. Cleanup is restricted to the unique test object and original generation.

**Prerequisites:** An explicitly approved isolated GCS test bucket. Active Google Cloud Application Default Credentials must be authorized to create, read and conditionally delete test objects in that bucket. Do not use the normal VisionIQ production evidence bucket simply because a variable already points there.

### PowerShell command (run only after approving the isolated bucket)

```powershell
git switch feature/learn
git pull

# Supply the name of an APPROVED ISOLATED TEST BUCKET here.
$bucket = Read-Host "Approved isolated test GCS bucket (name only)"
if ([string]::IsNullOrWhiteSpace($bucket)) { throw "Test bucket name required" }

# Must deliberately opt in; do not reuse the normal evidence bucket variable.
$env:GCS_EVIDENCE_BUCKET = $bucket
$env:LRN03_TEST_GCS = "1"
try {
    dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj `
        --configuration Release `
        --filter "FullyQualifiedName~Lrn03GcsArchiveIntegrationTests" `
        --verbosity normal
    if ($LASTEXITCODE -ne 0) { throw "Live GCS test failed: inspect sanitized output" }
}
finally {
    Remove-Item Env:LRN03_TEST_GCS -ErrorAction SilentlyContinue
    Remove-Item Env:GCS_EVIDENCE_BUCKET -ErrorAction SilentlyContinue
}
```

**Warning:** The test uses the supplied bucket with the current Google credentials; it does not programmatically confirm organizational approval or bucket environment labels. Confirm the destination yourself **before executing**. If cleanup permissions are missing, the test can fail while leaving one uniquely named synthetic raw object. Do not respond by granting broad cloud permissions; inspect the object name under `raw/v1/` and follow approved cleanup procedures.

### Expected successful evidence

```text
Test summary: total: 1, failed: 0, succeeded: 1, skipped: 0
Integration test exit code: 0  # if printed by shell
```

The xUnit assertions require exact raw bytes, a matching SHA-256 and unchanged object generation after replay. A passing test demonstrates those properties for **one isolated synthetic object**, not general production bucket retention/ACL configuration or complete indexing.

## Remaining acceptance checks

- **PASS:** real GCS test run against isolated bucket (set through `GCS_EVIDENCE_BUCKET` for that run): 1/1, exit 0, 14.6 seconds, build succeeded with 65 warnings.
- Review GCS IAM and retention policy separately; the code prevents normal overwrites, but bucket-wide retention/governance is an infrastructure responsibility.
- Validate that **downstream indexing** reads redacted metadata only. LRN-03 normalizer's allowlist and fake-backed tests cover service boundaries, not an independent deployed search index.
- Keep LRN-02 per-requester Confluence ACL enforcement as a separate open integration gate.

**Evidence log:** [04-Test-Evidence-and-Acceptance.md](04-Test-Evidence-and-Acceptance.md).

## Recorded actual GCS result (2 November 2026)

User-provided terminal result: `Test summary: total: 1, failed: 0, succeeded: 1, skipped: 0, duration: 14.6s`; `Build succeeded with 65 warning(s) in 28.2s`; `LRN-03 GCS test exit code: 0`. The earlier ADC `invalid_rapt` failure was resolved by reauthentication. This is the successful result of the live synthetic-object regression, not proof that a deployed search index has enforced redaction.
