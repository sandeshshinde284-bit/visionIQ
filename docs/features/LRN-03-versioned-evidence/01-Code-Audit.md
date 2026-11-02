# LRN-03 — Existing code audit (review increment 1)

**Source of truth:** [Original LRN-03 Trello acceptance criteria](https://trello.com/c/aqRFYZ9G/9-learn-lrn-03-store-history-as-versioned-evidence). This file records observations from source on `feature/learn`, **not** a claim that today's build, database or GCS integration tests passed.

## 1. How the existing pipeline works

```mermaid
sequenceDiagram
    participant Connector
    participant Sink as CanonicalEvidenceSink
    participant Service as EvidenceIngestionService
    participant GCS as Raw archive
    participant DB as PostgresEvidenceStore
    Connector->>Sink: Source record and native identity
    Sink->>Service: EvidenceEnvelope, raw JSON, access metadata
    Service->>Service: Validate HTTPS, revision, payload, UTC times
    Service->>Service: Redact/allowlist metadata; SHA-256 of original bytes
    Service->>GCS: StoreAsync(item key, revision, raw bytes, SHA)
    GCS-->>Service: gs:// URI
    Service->>DB: SaveAsync(item key + source revision + GCS URI)
    DB->>DB: Upsert logical item; lookup revision and hash
    alt identical revision exists
       DB-->>Service: Unchanged
    else native revision ID exists but hash differs
       DB-->>Service: Conflict
    else new revision
       DB->>DB: Insert revision; update latest pointer; commit
       DB-->>Service: Created/NewRevision
    end
```

## 2. Code-to-Trello evidence

| Task | Verified source observation | Remaining verification |
| --- | --- | --- |
| S05-01 schema | `PostgresEvidenceStore` references `evidence.source_items` and `evidence.source_revisions`, with revision, observed/source timestamps, raw URI, ACL and redacted JSON fields | Migration `db/migrations/versions/20261030_0002_versioned_evidence.py` reviewed: item_key PK; revision composite PK (item_key, provider_revision_id); FK from revision to item and deferred FK from latest (item_key, latest_revision_id) back to the corresponding revision. Real PostgreSQL query pending. |
| S05-02 identity | `EvidenceIdentity.ItemKey()` hashes length-delimited UTF-8 identity fields; distinguishes null repository from an empty string, and provider, project and external ID | Real insert and collision/identity fixture checks |
| S05-03 archive | `EvidenceIngestionService` hashes raw bytes and invokes archive before `SaveAsync`; accepts `gs://` return | Review concrete GCS implementation and verify immutability/object content and hash in real bucket |
| S05-04 work items | `CanonicalEvidenceSink` maps `work-items` to current evidence and `work-item-revisions` to historical revisions | Check historical/replay fidelity and missing source fields |
| S05-05 PR/commits/reviews | PR and commit cases are present; their native revision may be extended with a content hash | Confirm actual review artifact stream mapping; currently not shown in audited `Describe` switch |
| S05-06 CI/deployment/incident | CI builds map to `ci_run`; `EvidenceNormalizer` defines metadata allowlist for CI/deployment/incident | Verify the source-to-canonical switch has mappings for deployment and incident; definitions in normalizer alone are not ingestion proof |
| S05-11 dedupe/upsert | Store uses item upsert, existing (item, revision) hash check and transactional revision insert; returns Unchanged/Conflict/NewRevision | Live two-revision + replay + conflict DB test |

## 3. Important distinctions found

- `EvidenceNormalizer.Redact` is a **conservative allowlist**, not general-purpose PII detection. It copies only approved short scalar fields with restricted characters for supported record kinds, and nothing beyond `kind` for unknown kinds. Raw contents still exist in GCS by design and require storage security.
- `CanonicalEvidenceSink` has explicit mappings for work item current/revisions, PR, commit, CI build, declared links, Confluence pages/decisions/ACL. **No standalone review, deployment or incident branch appeared in the audited switch.** This is a candidate scope gap to resolve against the accepted source streams; don't invent coverage just because the metadata normalizer recognizes those kinds.
- `EvidenceIngestionService` archives before checking the database for an identical revision; replay therefore still invokes the archive abstraction. Whether replay creates any duplicate **GCS objects** depends on the concrete archive object-key/conditional-write implementation, which is not verified in this review.
- For Confluence page/decision evidence, sink access metadata uses a space principal; **that alone does not implement inherited per-engineer page authorization**. LRN-02 retrieval acceptance remains open.
- `PostgresEvidenceStore.SaveAsync` serializes concurrent item writes via upsert in a transaction, verifies existing revision hashes, and uses source-update time plus revision comparisons for latest-selection. These are code observations, not a proven physical database outcome.

## 4. Migration audit (2 November 2026)

The checked migration defines `evidence.source_items` with `item_key CHAR(64) PRIMARY KEY`, provider/source/project/repository/kind/external identity, latest revision pointer and observation timestamps. It defines `evidence.source_revisions` with composite primary key `(item_key, provider_revision_id)`, source and observation timestamps, SHA-256, GCS URI, redacted JSON, ACL verification and principal list. JSONB type constraints enforce array ACLs and object metadata; the deferred composite FK protects the latest pointer. Schema migration depends on `20261029_0001`.

Preflight observed: Docker engine 29.5.3; existing `visioniq-postgres` running healthy on host port 5432; pgvector/pgvector:pg16 available; branch `feature/learn`. The pre-existing container appears published on **all interfaces** (0.0.0.0:5432); review its host firewall and replace with loopback-only binding during a separately approved maintenance change. **Do not restart or alter that container as part of this integration test.**

**Observed user-run test container readiness (2 November 2026):** `visioniq-lrn03-test-postgres` running with isolated host bind `127.0.0.1:55433`; `pg_isready -U visioniq_test -d visioniq_lrn03_test` returned 0. **Migration now confirmed by user-run output:** Alembic head `20261101_0012`; `evidence.source_items` and `evidence.source_revisions` both listed among 14 `evidence` tables in isolated test database. **Revision import/replay counts remain untested.** `db.manage.LocalConfig` reads local environment variables and `.env`, and migration selection requires local target; the next command must override host/port/db identity explicitly to prevent accidental writes to the usual database.

## 5. Next actual validation

First run the committed focused synthetic boundary tests and capture discovered count and exit code:

```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj `
    --configuration Release `
    --filter "FullyQualifiedName~Lrn03EvidenceBoundaryTests" `
    --verbosity normal
Write-Host "LRN-03 focused test exit code: $LASTEXITCODE"
```

**Expected and observed (user-run, 2 November 2026):** four tests discovered, four passed, zero failed, zero skipped, build succeeded, exit 0. xUnit VSTest Adapter 2.8.2, net10.0. This validates the synthetic service boundaries only; it is not a PostgreSQL or GCS integration result.

Then use an **isolated approved PostgreSQL test schema/database** to demonstrate exactly one source item with two revisions after replay. Do not run test imports into shared evidence tables or mutate production-like checkpoints. The exact executable integration command and SQL must be grounded in the existing test harness/schema before publishing it as copy-paste instruction.

## 5. Documentation update rule

Each reviewed task gets one of `CODE FOUND`, `TEST PASSED`, `BLOCKED`, `GAP`, or `NOT APPLICABLE`. Never upgrade to `TEST PASSED` based on reading code alone. Record sample counts and sanitized output; don't commit source page contents or credentials.

## Migration verification evidence — 2 November 2026

**Environment:** disposable container `visioniq-lrn03-test-postgres`; host `127.0.0.1:55433`; database `visioniq_lrn03_test` (not the existing VisionIQ database).

**User-run verification:**

```powershell
docker exec visioniq-lrn03-test-postgres psql -U visioniq_test -d visioniq_lrn03_test -c "SELECT version_num FROM alembic_version;"
docker exec visioniq-lrn03-test-postgres psql -U visioniq_test -d visioniq_lrn03_test -c "\dt evidence.*"
```

- Actual Alembic version: **20261101_0012** (migration chain applied successfully).
- Actual evidence tables: **14**, including `source_items` and `source_revisions`.
- The table owners in this isolated database are `visioniq_test`, as expected from this test migration identity.
- **Status:** migration/schema-existence check **PASS**. This does **not** verify the schema's write permissions for the separate runtime API role, deduplication outcomes, raw GCS objects or Confluence requester authorization.

**Next gate:** isolated API role bootstrap, then replay of two source revisions through the actual `PostgresEvidenceStore` with verified database row counts and metadata.

## Bootstrap command correction and documentation coverage

The user ran `python -m db.manage bootstrap` with the correct isolated environment, but `db/manage.py` accepts only `migrate`, `roles`, `verify`, and `all`. The result was **CLI usage exit 2**; this is not evidence of a failed database permission grant. The Python function `bootstrap(settings)` is reached through the `roles` action. New [operations](03-Setup-Commands-and-Troubleshooting.md) and [test evidence](04-Test-Evidence-and-Acceptance.md) documentation covers this incident plus prior Docker/PowerShell issues. Role verification is still pending.
