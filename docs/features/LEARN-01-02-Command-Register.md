# VisionIQ LEARN — LRN-01 and LRN-02 command register

**Purpose:** a safe, human-readable index of the commands actually used or discussed during LRN-01 and LRN-02. This is not a script to run from top to bottom. Pick only the command matching your task. Commands that contact live sources or write evidence must follow the source-approval and destination checks.

**Execution labels:** **Observed** = executed by the user in the recorded local test workflow; **Reference** = documented for future use and not confirmed executed with the shown arguments; **Do not repeat** = investigative or potentially costly operation already performed.

All commands assume Windows PowerShell at the VisionIQ repository root and `feature/learn`, with configured local credentials. Never paste secrets, page bodies or corporate records in a public report.

## A. Daily development: start here

| Goal | PowerShell | Class | Expected / caution |
| --- | --- | --- | --- |
| Check branch | `git branch --show-current` | Reference | Expect `feature/learn`; command does not switch or merge branches |
| Bring changes locally | `git switch feature/learn; git pull` | Reference | Only when your local work is clean and you intend to update |
| Build solution | `dotnet build .\VisionIQ.sln --configuration Release` | Observed workflow | Exit 0 before using `--no-build`; review warnings rather than treating them as errors |
| Full regression | `dotnet test .\VisionIQ.sln --configuration Release` | Observed | **402 passed, 0 failed** on recorded run; not guaranteed on later commits |
| Read-only source inventory | `dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- inventory` | Observed | Connected source counts, exit 0 on reachable sources; creates local inventory file |

## B. LRN-01: source-history checks

| Goal | PowerShell | Class | Expected / caution |
| --- | --- | --- | --- |
| Connector test project | `dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --no-restore` | Reference | Ensure restore has already succeeded; check number of discovered tests |
| Failure-recovery filter | `dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~StreamIsolationTests|FullyQualifiedName~CanonicalEvidenceSinkTests|FullyQualifiedName~AdoWorkItemChangeHandlerTests|FullyQualifiedName~SyncRunnerTests|FullyQualifiedName~TicketFixProjectionSinkTests"` | Observed | Recorded filtered run: 30 passed |
| Source-specific sync | `dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- sync --source work-items` | Reference | **Writes to configured output destination and checkpoints**; inspect destination first |
| Repository Git history | `dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- sync --source commits,pull-requests` | Reference | Confirm approved ADO repository filters before running |
| CI build history | `dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- sync --source builds` | Observed workflow | Limited **only** by `ADO_PIPELINE_IDS`, never by `ADO_REPOS` (empty = every pipeline in the approved projects); recorded sample 86 builds/149 links |
| Full backfill/reconcile | See [LRN-01 operations](LRN-01-history-connectors/operations.md) | Do not repeat casually | Heavy live read/writes; not a routine health check |

## C. LRN-02: Confluence and security checks

| Goal | PowerShell | Class | Expected / caution |
| --- | --- | --- | --- |
| Focused Confluence tests | `dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~Confluence|FullyQualifiedName~ContentRevisionTests" --verbosity normal` | Observed | **37 passed, 0 failed, 0 skipped** in recorded run |
| Safe title+label discovery | `dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- discover-confluence-decisions` | Observed | 589 pages, 314 eligible, 0 title matches, 0 label matches, 0 label failures; no page bodies exported. May make hundreds of Confluence calls |
| Date-scoped isolated JSONL sync | See [LRN-02 setup and operations](LRN-02-confluence/02-Setup-and-Operations.md) | Do not repeat | Recorded exit **2 (Partial)** is intentional withholding, **not** complete success |
| `FullyQualifiedName~ConfluenceAccess` test filter | Do not use as acceptance proof | Avoid | Earlier invocation discovered **zero tests** and is not a successful access-control test |

## D. Exit codes and report interpretation

- CLI exit **0** means the run completed in its defined scope; it does **not** establish complete historical coverage or user-specific retrieval authorization.
- CLI exit **1** means usage or configuration trouble.
- CLI exit **2** means partial/not synced or some reported failure; inspect sanitized report and reason. LRN-02 JSONL deliberately withholds restricted/unverified page bodies.
- For `dotnet test`, confirm **nonzero discovered tests** and 0 failures, not just the process exit code.
- `--since` is not a hard scan limit if a checkpoint exists. Never assume it limits Confluence permission enumeration.
- Only enable canonical PostgreSQL + GCS writes against explicitly approved destinations. Never wipe or reset checkpoints to obtain a green test.

## E. Safe preflight before any sync with writes

```powershell
Write-Host "Current branch:"; git branch --show-current
Write-Host "Output mode: $env:INGESTION_OUTPUT_MODE"
Write-Host "Checkpoint store: $env:CHECKPOINT_STORE"
Write-Host "Database host: $env:DB_HOST"
Write-Host "Evidence bucket: $env:GCS_EVIDENCE_BUCKET"
```

**Caveat:** Environment variables displayed here can be empty while settings are populated from `.env`. Empty preflight output is **not permission to proceed** and is **not proof the runtime has no destination**. For a write operation, independently verify the effective configuration and destination from the application's safe startup diagnostics and your authorized environment. Do not output passwords or API tokens.

## F. Further explanations and diagrams

- [LRN-01 visual walkthrough](LRN-01-history-connectors/visual-guide.md)
- [LRN-01 local testing](LRN-01-history-connectors/local-testing.md)
- [LRN-01 historical test evidence](../LRN-01-Test-Report.md)
- [LRN-02 illustrated walkthrough](LRN-02-confluence/05-Illustrated-Walkthrough.md)
- [LRN-02 test evidence](LRN-02-confluence/03-Test-Report-and-Evidence.md)
- [LRN-02 acceptance boundary](LRN-02-confluence/04-Acceptance-and-LRN03-Handoff.md)

**Scope note:** This register covers principal commands, not a verbatim exhaustive transcript of every one-off PowerShell inspection statement. Historical commands that read sensitive JSONL content should not be indiscriminately copied into a public playbook.
