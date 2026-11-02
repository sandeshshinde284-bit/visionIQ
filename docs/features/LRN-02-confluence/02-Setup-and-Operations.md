# LRN-02 — Setup, commands and operating instructions

## Prerequisites
Use a local .NET SDK matching the solution, the approved project repository, and a Confluence account/token authorized for the specified spaces. Keep all credentials in untracked local `.env`; never put PATs, email/token pairs, page content, private identifiers or access restriction lists in GitHub issues or chat.

## Configuration names
```dotenv
CONFLUENCE_BASE_URL=https://<tenant>.atlassian.net/wiki
CONFLUENCE_SPACE_KEYS=<approved-space-keys>
CONFLUENCE_USER_EMAIL=<credential-email>
CONFLUENCE_API_TOKEN=<secret>
```
These are **templates**, not credential values. The observed integration used Confluence Cloud, the approved `ERK` space, and no other approved space.

## Read-only candidate discovery
```powershell
git switch feature/learn
git pull
dotnet build .\VisionIQ.sln --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
dotnet run --no-build --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- discover-confluence-decisions
Write-Host "Discovery exit code: $LASTEXITCODE"
```
Queries current page metadata, effective restrictions and labels of complete/unrestricted pages; requests no page storage bodies. Prints totals only. A 0 exit means all label requests succeeded, **not** that individual Confluence viewer permissions have been tested. It can make hundreds of REST calls. Do not run repeatedly without a reason.

## Inventory
```powershell
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- inventory
```
Inventory checks configured source reachability, including Confluence counts. It does not import content.

## Controlled local sync (only when needed)
For a bounded test, first configure local **file** checkpoints and an isolated JSONL output directory:
```powershell
$env:INGESTION_OUTPUT_MODE = "jsonl"
$env:CHECKPOINT_STORE = "file"
$env:INGESTION_OUTPUT_DIR = "artifacts/ingestion/lrn02-confluence/manual-check"
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- sync --source confluence --since 2026-11-01
Write-Host "Sync exit code: $LASTEXITCODE"
```
The `--since` filter does not avoid the need to compute current ACLs for the whole approved space; existing checkpoints can override `--since` for normal delta runs. A partial exit 2 is expected when restricted or unverifiable pages are withheld. Do not publish JSONL files: unrestricted does not imply publicly distributable corporate content. This is a **reference command**, not a request to rerun the already completed test.

## Safety and troubleshooting
- No page bodies or private identities should appear in discovery output.
- `label-lookup-failures > 0`: exit 2, inspect sanitized diagnostics; never assume a failed label request means no candidate.
- `withheld-or-unverified`: includes both known restricted and unknown ACLs, which are intentionally excluded.
- Zero title and label candidates is inconclusive: template-based detection requires normalization of page content.
- Avoid `--full` imports to find one ADR. A targeted security test and LRN-03 retrieval enforcement are preferable.
- Never point canonical mode at production-like storage merely to complete documentation or discovery.
