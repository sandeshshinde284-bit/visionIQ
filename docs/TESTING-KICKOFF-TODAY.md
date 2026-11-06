# VisionIQ LEARN — Testing Kickoff Prompt (hand this to a fresh Claude session)

> **How to use this file:** After cloning the `dev` branch, open Claude Code in the repo root and say:
> *"Read docs/TESTING-KICKOFF-TODAY.md and follow it."*
> Everything below is written as an instruction to that AI session — it is self-contained and assumes zero prior context.

---

## Your Mission

Today you are testing **all seven LEARN features (LRN-01 through LRN-07)** of the VisionIQ project, both:
- **Local testing** — running everything on this machine, storing data in a local PostgreSQL (via Docker) and/or local JSONL files
- **Cloud testing** — only the parts that don't require FND-06 infrastructure (see "Cloud Testing Scope" below; most of LEARN can be verified locally, cloud end-to-end is a separate, smaller checklist)

You are not exploring or redesigning anything. The code is implemented and documented. Your job is to **run it, verify it behaves as documented, and report pass/fail per feature** with evidence (command output, row counts, exit codes).

---

## Step 0: Orient Yourself (10 minutes, do this first)

**Primary, authoritative source — read this first:**

**[docs/VisionIQ_Developer_Team_Guide.pdf](VisionIQ_Developer_Team_Guide.pdf)** — the team's own "Developer Bible" (v1.7, 36 pages). This is the single most important document for today's work:
- **Pages 1–27** (v1.6 original): full install/run/connect/test guide — Windows 11, PowerShell, .NET 10, Python 3.12, PostgreSQL, Google Cloud. Has step-by-step setup commands, a "Choose your next task" routing table, and dedicated chapters for LRN-01 (ADO), LRN-02 (Confluence), LRN-03 (evidence storage), LRN-04 (ticket-fix links), plus a "Connected test" chapter that exercises all four together.
- **Pages 28–36** (v1.7 LRN-05 addendum, added 5 Nov 2026): real tested local/source behavior for backfill, incremental sync, reconciliation, recovery, work-item revisions, Confluence scope safety, and — critically — **§17.9 "Cloud validation handoff (FND-06)"**, which lists the exact cloud acceptance gates (Cloud Run image, Cloud SQL, immutable GCS archive, canonical sync end-to-end, cross-run replay, negative access controls, scheduler/event delivery) all still marked **Pending**. Also has a full **§17.10 operations/troubleshooting table** (symptom → first check) — check this before treating something as a new bug.

**Extract its text if you can't open the PDF directly:**
```powershell
python -c "import pypdf; r = pypdf.PdfReader('docs/VisionIQ_Developer_Team_Guide.pdf'); [print(f'--- Page {i+1} ---\n' + p.extract_text()) for i, p in enumerate(r.pages)]" > guide-text.txt
```

**Then, supplementary material built this session** (fast orientation / code-level detail the Bible doesn't cover):

1. **[docs/LEARN-PHASE-COMPLETE/CODE-UNDERSTANDING-GUIDE.md](LEARN-PHASE-COMPLETE/CODE-UNDERSTANDING-GUIDE.md)** — how the code is organized, which files matter
2. **[docs/LEARN-PHASE-COMPLETE/ARCHITECTURE-DIAGRAMS.md](LEARN-PHASE-COMPLETE/ARCHITECTURE-DIAGRAMS.md)** — text diagrams of the pipeline, checkpoint logic, retraction lifecycle, webhook dedup
3. Open these in a browser for visual diagrams (double-click or `start <path>` on Windows):
   - `docs/LEARN-PHASE-COMPLETE/images/learn-pipeline-flow.html`
   - `docs/LEARN-PHASE-COMPLETE/images/webhook-at-least-once.html`
   - `docs/LEARN-PHASE-COMPLETE/images/file-connections.html`
   - `docs/LEARN-PHASE-COMPLETE/images/code-flow-scenarios.html`
4. **[docs/LEARN-PHASE-COMPLETE/COMMAND-REGISTER.md](LEARN-PHASE-COMPLETE/COMMAND-REGISTER.md)** — the canonical list of commands already validated for LRN-01/02 (reuse these patterns for LRN-03–07)
5. Each feature's own folder has a `README.md` and acceptance doc:
   - `docs/LEARN-PHASE-COMPLETE/LRN-01-HISTORY-CONNECTORS/`
   - `docs/LEARN-PHASE-COMPLETE/LRN-02-CONFLUENCE/`
   - `docs/LEARN-PHASE-COMPLETE/LRN-03-VERSIONED-EVIDENCE/`
   - `docs/LEARN-PHASE-COMPLETE/LRN-04-TICKET-FIX-LINKS/`
   - `docs/LEARN-PHASE-COMPLETE/LRN-05-BACKFILL-AND-SYNC/`
   - `docs/LEARN-PHASE-COMPLETE/LRN-06-CODE-INTELLIGENCE/`
   - `docs/LEARN-PHASE-COMPLETE/LRN-07-RELATIONSHIP-GRAPH/`

---

## Step 1: Local Environment Setup

**Prerequisites:** Docker Desktop running, .NET 10 SDK, Python 3.12 (for LRN-06 intelligence service).

```powershell
git branch --show-current          # expect: document-restructure-feature-flow
git log -1 --oneline               # confirm you have the latest commit
Copy-Item .env.example .env        # if .env doesn't exist yet
```

**Edit `.env`** and set at minimum:
- `DB_LOCAL_PASSWORD`, `DB_API_PASSWORD`, `DB_INTELLIGENCE_USER`, `DB_INTELLIGENCE_PASSWORD` — any local dev values, these are NOT production secrets
- Leave `ADO_ORG_URL` / `ADO_READ_PAT` empty unless you have an **approved test/sandbox ADO project** (never point this at a shared production org without explicit approval)

**Bring up the local stack** (PostgreSQL with pgvector, Pub/Sub emulator, migrations, .NET API, Python Intelligence service):

```powershell
docker compose up --build -d
docker compose ps                  # all services should show healthy
```

This runs `db-migrate` automatically (via `python -m db.manage all`), so your local PostgreSQL will have the full `evidence` schema with all LRN-01–07 tables already created.

**Verify the database is ready:**

```powershell
docker exec -it visioniq-postgres psql -U visioniq -d visioniq -c "\dt evidence.*"
```

You should see tables including `source_items`, `source_revisions`, `ticket_fix_links`, `ticket_fix_link_lifecycle_events`, `source_change_inbox`, `sync_checkpoints`, `sync_quarantine`.

**Build and test the solution:**

```powershell
dotnet build .\VisionIQ.sln --configuration Release
dotnet test .\VisionIQ.sln --configuration Release
```

Expected baseline (per COMMAND-REGISTER.md): **402 passed, 0 failed** on a known-good commit. If your count differs significantly, note it — that's a real finding, not noise.

---

## Step 2: Per-Feature Test Plan

For each feature below: run the commands, record exit code + key output, query the database to confirm data landed as expected, and mark PASS / PARTIAL / FAIL with a one-line reason.

### LRN-01: History Connectors (ADO work items, PRs, commits, builds)

```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter "FullyQualifiedName~StreamIsolationTests|FullyQualifiedName~CanonicalEvidenceSinkTests|FullyQualifiedName~AdoWorkItemChangeHandlerTests|FullyQualifiedName~SyncRunnerTests|FullyQualifiedName~TicketFixProjectionSinkTests"
```

If you have an **approved test ADO project**, also run a real sync (see `docs/LEARN-PHASE-COMPLETE/LRN-01-HISTORY-CONNECTORS/local-testing.md` for the exact env vars):

```powershell
$env:CHECKPOINT_STORE = 'file'
$env:INGESTION_OUTPUT_MODE = 'jsonl'
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- inventory
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- sync --source work-items
```

**Verify:** checkpoint file was written; re-running the same command produces zero new records (idempotent resume — see the checkpoint diagram in learn-pipeline-flow.html).

### LRN-02: Confluence + ACL

```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter "FullyQualifiedName~Confluence|FullyQualifiedName~ContentRevisionTests" --verbosity normal
```

Expected: **37 passed, 0 failed** per COMMAND-REGISTER.md baseline.

### LRN-03: Versioned Evidence (PostgreSQL + Cloud Storage)

```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter "FullyQualifiedName~CanonicalEvidenceSinkTests|FullyQualifiedName~Lrn03EvidenceBoundaryTests"
dotnet test .\tests\VisionIQ.Api.Tests\VisionIQ.Api.Tests.csproj --configuration Release --filter "FullyQualifiedName~EvidenceIngestionTests"
```

**Local-only verification (no real GCS needed for the DB side):**

```sql
-- via docker exec -it visioniq-postgres psql -U visioniq -d visioniq
SELECT item_key, provider_revision_id, content_sha256, raw_snapshot_uri FROM evidence.source_revisions LIMIT 10;
```

Confirm: identical content never produces a duplicate row (content-hash dedup — see RawEvidenceArchive.cs comments).

### LRN-04: Ticket-Fix Links (use the new tools!)

Two purpose-built test tools now exist in `tools/` (added this session — read their header comments first):

**`tools/Lrn04CanonicalCheck`** — full end-to-end pipeline test: synthetic PR → GCS archive → PostgreSQL evidence → ticket-fix link, with automatic cleanup and idempotency replay check.

```powershell
dotnet run --project tools/Lrn04CanonicalCheck            # dry run first — shows the approved target, makes no connections
# Only if you have an isolated test DB at localhost:55433/visioniq_lrn03_test and a test GCS bucket:
$env:DB_HOST='127.0.0.1'; $env:DB_PORT='55433'; $env:DB_NAME='visioniq_lrn03_test'
$env:DB_API_USER='visioniq_lrn03_api'; $env:DB_API_PASSWORD='<your test password>'
$env:GCS_EVIDENCE_BUCKET='visioniq-lrn03-test-1'; $env:LRN04_CANONICAL_BUCKET_CONFIRM='visioniq-lrn03-test-1'
dotnet run --project tools/Lrn04CanonicalCheck -- --execute
```

**`tools/Lrn04Replay`** — replays already-exported `pull-requests.jsonl` against 7 known bug→PR pairs (5 distinct bugs) to verify explicit classification and idempotency. Needs `LRN04_REPLAY_JSONL`, `LRN04_REPLAY_PROJECT`, `LRN04_REPLAY_REPO_ID`, `LRN04_REPLAY_PAIRS` env vars — read `Program.cs` header comments for the exact format.

**Also run unit tests:**

```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter "FullyQualifiedName~TicketFixResolverTests|FullyQualifiedName~TicketFixProjectionSinkTests|FullyQualifiedName~LinkExtractorTests"
```

See [DESIGN-CONCEPTS.md](LEARN-PHASE-COMPLETE/LRN-04-TICKET-FIX-LINKS/DESIGN-CONCEPTS.md) for the full classification contract (Unknown/Inferred/Explicit/Verified/Disputed) and the retraction state machine diagram in `code-flow-scenarios.html` → Scenario 3.

### LRN-05: Backfill, Sync & Webhooks

```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter "FullyQualifiedName~SyncQuarantineTests|FullyQualifiedName~CheckpointDurabilityTests|FullyQualifiedName~WebhookAndReconcileTests"
```

**Test the reconciliation sweep doesn't move the checkpoint** (see [BACKFILL-OVERVIEW.md](LEARN-PHASE-COMPLETE/LRN-05-BACKFILL-AND-SYNC/BACKFILL-OVERVIEW.md) for the full repeatable PowerShell workflow with checkpoint-hash verification):

```powershell
$checkpoint = Join-Path $env:INGESTION_OUTPUT_DIR 'checkpoints.json'
$before = (Get-FileHash $checkpoint -Algorithm SHA256).Hash
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- sync --reconcile-days 7
$after = (Get-FileHash $checkpoint -Algorithm SHA256).Hash
Write-Host "Checkpoint unchanged:" ($before -eq $after)   # must be True
```

**Webhook dedup test** (local): start `serve-webhooks`, send the same payload twice, confirm the second call is a no-op (see `webhook-at-least-once.html` for the exact scenarios to reproduce — fresh event, duplicate, simulated crash/retry).

### LRN-06: Code Intelligence (Python service)

```powershell
docker compose logs intelligence --tail 50     # confirm service is healthy
# Python tests live at the repo root under tests/intelligence/, not under src/:
pytest tests/intelligence -v
```

Check `docs/LEARN-PHASE-COMPLETE/LRN-06-CODE-INTELLIGENCE/ARCHITECTURE.md` for what "pass" looks like (symbol extraction, parser-failure recovery).

### LRN-07: Relationship Graph

Check `docs/LEARN-PHASE-COMPLETE/LRN-07-RELATIONSHIP-GRAPH/ARCHITECTURE.md`. Verify bounded traversal (depth 1–3, max 100 nodes) and that ACL is re-checked per neighbor, not just at the root.

---

## Step 3: Cloud Testing Scope (what's realistic today)

Per the acceptance docs — and the Developer Team Guide PDF, **§17.9 "Cloud validation handoff (FND-06)"**, which lists seven specific gates (Cloud Run image, Cloud SQL, immutable GCS archive, canonical sync end-to-end, cross-run replay, negative access controls, scheduler/event delivery) all marked **Pending** as of the 5 Nov 2026 edition — **full cloud end-to-end is explicitly OPEN and owned by FND-06**. Do not treat it as in scope for today unless your team has already provisioned that infrastructure and can check off those exact gates. What you *can* test today if cloud credentials exist:

- Point `GCS_EVIDENCE_BUCKET` at a real (test) GCS bucket and confirm `RawEvidenceArchive.cs` uploads work (content-addressed path, `IfGenerationMatch=0` idempotency)
- Point `DB_HOST` at a real (test) Cloud SQL instance and run the same migration + sync flow
- **Never** run an unrestricted `--full` sync against a shared/production ADO project or Confluence space

If cloud infra isn't provisioned yet, say so explicitly in your report rather than skipping silently.

---

## Step 4: Reporting Results

For each LRN feature, report:

| Feature | Local Unit Tests | Local Integration (DB/files) | Cloud (if applicable) | Verdict |
|---|---|---|---|---|
| LRN-01 | pass/fail counts | checkpoint resume verified? | — | PASS/PARTIAL/FAIL |
| LRN-02 | ... | ... | ... | ... |
| ... | | | | |

Include: exact commands run, exit codes, any deviation from documented expected baselines (e.g., COMMAND-REGISTER.md says "402 passed" — if you get a different number, that's a finding).

**Do not commit or push anything without asking first.** Keep test artifacts (JSONL output, local credentials, `.env`) out of git entirely — they're already covered by `.gitignore` where applicable, but double-check before `git add -A`.

---

## Known Open Items (don't re-discover these, they're already tracked)

- LRN-04: five real (not synthetic) ticket-to-fix examples still need independent sign-off
- LRN-05: full historical backfill + live cloud chain still OPEN under FND-06
- LRN-02: Confluence real-source acceptance is partial (ACL fail-closed behavior confirmed, full space scan still pending)

See `docs/REVIEW-AND-AUDIT/2026-Q4/INTEGRATION-REVIEW.md` **and** the Developer Team Guide PDF §17.10 (operations/troubleshooting table) for the complete, authoritative list of open findings and known symptoms before you report something as a "new" bug — check it's not already known.
