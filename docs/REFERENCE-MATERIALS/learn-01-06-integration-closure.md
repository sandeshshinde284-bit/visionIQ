# VisionIQ — Learn LRN-01–LRN-06 coding integration

**Working PR:** #8, targeting `feature/learn`.  
**Runtime:** .NET 10 ingestor/API, Python 3.12 Intelligence, Alembic 0001–0005, PostgreSQL 16, approved private Google Cloud Storage.  
**Scope:** coding and fixture/isolated database validation; no changes to live cloud resources or source credentials.

## Architecture and code paths

| Story | Implemented integration | Limitations / remaining external gate |
| --- | --- | --- |
| LRN-01 | .NET ingestor reads approved ADO history with resumable stream checkpoints, partial/error states; canonical sink archives before DB commit | Approve actual source projects, native project GUID resolution and history counts |
| LRN-02 | Confluence connector preserves page ACL snapshots and ancestor-level restrictions; current-access verifier re-reads source permissions | Source-authenticated retrieval integration is intentionally fail closed; provider identities need approval |
| LRN-03 | LRN-01/02 canonical archives, latest versioned evidence reader, ACL/principal filter and mandatory fresh permission verifier | No public evidence API. Records imported with unverified ACL remain invisible; validate trusted identity/ACL mapping before enabling retrieval |
| LRN-04 | Source PR/commit evidence is persisted first, then explicit ticket/PR/commit references are projected to PostgreSQL. Full SHA source-declared reverts update existing scoped associations | Five real ticket/fix cases require evidence review; an explicit link alone is not proof of a fix |
| LRN-05 | Production CLI records per-stream result/audit in `source_sync_runs`; backfill reconciliation retains cursors on missing records; .NET event inbox handles replay/quarantine | Authenticated provider events, Pub/Sub/Cloud Run scheduling and actual source delta cycle require FND-06; Python backfill protocols are a testable boundary, not an independent deployed worker |
| LRN-06 | Git scanner produces pinned symbols/imports/file inventory with durable local parser-failure recovery. Trusted CLI archives COMPLETE metadata (no source text) into LRN-03 GCS/PostgreSQL; PARTIAL does not replace latest-good | Scan on approved real repos, map approved repository IDs and validate access to private GCS/Cloud SQL. Data is stored `acl_verified=false` (not searchable) |

## Canonical ingestor

```powershell
# From the VisionIQ repo root. All variables go in a private local .env or
# approved workload environment; NEVER add tokens to git.
# INGESTION_OUTPUT_MODE=canonical
# CHECKPOINT_STORE=postgres
# GCS_EVIDENCE_BUCKET=<approved-private-bucket>
dotnet run --project src/VisionIQ.Ingestor.Cli -- sync --source work-items,pull-requests,commits,builds,confluence
```

In canonical mode, the ingestor now writes source-identified ticket/fix associations
to `evidence.ticket_fix_links` after LRN-03 successfully archives the related
PR or commit revision. A missing or failed archive prevents advancement of the
relevant connector checkpoint. Revert records require a *full* Git SHA;
abbreviated commit references are not guessed.

The CLI also writes sanitized stream results to `evidence.source_sync_runs`.
These contain successful-record counts, not independently verified full-source
totals. They never prove that an unreachable source is empty.

## Commit-pinned code scanner to canonical evidence

Use this mode ONLY after the private GCS bucket and restricted API database role
are provisioned and the Git repository has been approved.

```powershell
# Sign in using approved application default credentials or workload identity.
# Set environment DB_HOST, DB_PORT, DB_NAME, DB_API_USER,
# DB_API_PASSWORD, DB_SSL_MODE, and GCS_EVIDENCE_BUCKET privately.
$env:PYTHONPATH = "src/VisionIQ.Intelligence"

python -m app.code_intelligence.ingest_cli `
  --repo "C:\approved\repo" `
  --cache "C:\approved\visioniq-cache" `
  --project-id "<provider-native-project-id>" `
  --repository-id "<provider-native-repository-id>" `
  --source-url "https://github.com/approved-org/approved-repo"
```

The scanner parses **committed** code without executing it. The archive contains
structured file/symbol/import/call metadata, not source contents. Repository
scope is encoded using the LRN-03 SHA-256 length-prefixed source identity.
The content revision is the actual Git SHA and the source time comes from
`git show --format=%cI`. Source URL must be HTTPS without credentials.

A parser failure returns PARTIAL (process exit code 2) and preserves the previous
local and database last-good revision. New canonical records are *unverified*,
so no requester can retrieve them through the permission-aware read service
without a subsequent approved provider-specific ACL mapping.

## Verification and boundaries

```powershell
dotnet test VisionIQ.sln --configuration Release
python -m ruff check .
python -m ruff format --check .
$env:PYTHONPATH = "src/VisionIQ.Intelligence"
python -m pytest -q
docker compose run --build --rm db-migrate
```

PR #8 CI additionally runs isolated PostgreSQL sync-audit checks and tests
current ACL revocation, canonical-first link writes and source count mismatches.

**Not closed by this PR:** live provider integrations, complete ADO/Jira/Confluence
identity mapping, FND-04 application deployment, FND-06 event transport/scheduling,
storage IAM/retention verification, or all LRN-01–06 end-to-end acceptance.
Do not label this code production-ready before those gates are verified.
