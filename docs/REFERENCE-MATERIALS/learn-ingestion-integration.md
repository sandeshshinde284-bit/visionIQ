# LRN-01 + LRN-02 + LRN-03 integration review and runbook

**Branch:** `feature/LRN1-LRN2`, PR #5 targeting `feature/learn`.
**Status:** Code integration under review; all three user stories remain OPEN.

## What was corrected

- Incorporated `feature/learn` changes without deleting LRN-03 and LRN-06.
- Preserved immutable evidence migration `20261030_0002`; added connector
  checkpoint migration `20261030_0003` (down-revision `20261030_0002`).
- Azure DevOps `workitemsbatch` with `errorPolicy=Omit` now creates an
  explicit missing-record result for every ID absent in the response.
  The work-item checkpoint cannot advance during PARTIAL/NOT_SYNCED runs:
  an older inaccessible item will be retried, not permanently omitted.
- JSONL export withholds all Confluence page bodies and design decisions
  whose effective ACL is restricted or cannot be verified. Parent restrictions
  are inherited. ACL metadata continues to be written for diagnostics.
  The checkpoint is retained so a later permission change is reread.
- Unix JSONL files are created owner-readable/writable only (0600); the
  output run directory is chmod 0700. On Windows, use a private directory
  with appropriate NTFS ACLs, and do not share raw exports.
- Added opt-in canonical evidence handoff from the .NET Ingestor CLI to
  LRN-03 `EvidenceIngestionService`: archive raw records to GCS then
  transactionally upsert PostgreSQL source items and revisions.
- Every canonical record is marked **UNVERIFIED_ACCESS** (empty effective ACL):
  it is not retrievable by a VisionIQ end user until source-specific current
  access and Confluence ancestor-level conditions can be enforced.
- The explicit source link keys now include ADO project scope, preventing
  links from different projects with matching IDs from colliding.

## Local JSONL mode (default)

The existing CLI command and `INGESTION_OUTPUT_DIR` continue to work.
With no new setting, `INGESTION_OUTPUT_MODE=jsonl`.
The CLI writes JSONL only for unrestricted Confluence pages with complete
permissions; restricted/unknown page bodies are **not exported**.
It reports PARTIAL when pages are withheld; this is a truthful status.

The JSONL directory must stay private and outside source control.
JSONL is **not** the canonical evidence database; never index or publish
those files without provider-specific ACL verification and redaction.

## Canonical archive mode (explicit opt-in)

The .NET Ingestor CLI now supports:

```dotenv
# Add these entries to your private .env; never commit passwords or tokens.
INGESTION_OUTPUT_MODE=canonical
CHECKPOINT_STORE=postgres
GCS_EVIDENCE_BUCKET=<approved-private-bucket-name>
# DB_HOST, DB_PORT, DB_NAME, DB_API_USER, DB_API_PASSWORD, DB_SSL_MODE
# are the same variables used by FND-03.
```

Example commands using existing CLI arguments:

```powershell
dotnet run --project src/VisionIQ.Ingestor.Cli -- sync
```

Before running canonical mode:
1. Approve and provision a private bucket with appropriate GCS IAM and
   retention protection, by your GCP administrator. This PR does **not**
   create cloud resources or deploy workloads.
2. Use the pre-existing Alembic migration runner to upgrade the database
   through `20261030_0003`. Do not run a competing EF migration chain.
3. Use approved source read-only identities and DB API runtime credentials.
   Cloud credentials must come from workload identity or approved local
   Application Default Credentials, never repository files.
4. Review source payload sensitivity and redaction before enabling canonical
   mode with real enterprise data. It archives *original raw source JSON*.
5. Verify storage is private, encryption/retention policies are approved,
   and restricted source records are excluded from all search/briefs.
   Current code **does not** implement a user-facing evidence search route.

When canonical mode is enabled, the CLI calls `CanonicalEvidenceSink`;
each record has its source identity, native revision, UTC timestamp,
content fingerprint and archive URI. Missing permissions result in
fail-closed access. Any archive/write error prevents checkpoint advancement.
The report file remains a local diagnostic artifact.

## Important modeling limits

- ADO project's configured **name** is used as the project scope because
  LRN-01 currently does not supply a stable native project GUID.
  Reconcile to native project IDs before considering acceptance complete.
- The current work-item snapshot is stored under `work_item_current`;
  native historical versions from the revisions endpoint are grouped under
  `work_item`. This avoids hash conflicts between full and partial source
  payloads; a unified normalized representation is still a follow-up.
- ADO/Confluence current source permissions are **not** verified by the
  canonical sink: its `EvidenceAccess.Verified` stays false, intentionally.
- Confluence permission snapshots without provider update timestamps use
  the **observed timestamp**; they are not proof of the original change time.
- Direct links are recorded as source-declared evidence, not proven
  ticket-to-fix causality. LRN-04 must validate them.
- Local CI verifies PostgreSQL with a fresh volume, mocked source data
  and an archive adapter test. Live approved ADO/Confluence and GCS
  access/retention tests are not included in CI.

## Validation

```powershell
dotnet test VisionIQ.sln --configuration Release
python -m pytest -q
python -m ruff check .
python -m ruff format --check .
docker compose run --build --rm db-migrate
```

PR #5 CI also verifies `evidence.source_items`,
`evidence.source_revisions`, and `evidence.connector_checkpoints`,
with Alembic head `20261030_0003`.

**Do not mark LRN-01/02/03 Done on CI alone.** Test real approved source
instances, missing items, audit/counter visibility, reimports, permission
revocation, and source-backed linking before story acceptance.
