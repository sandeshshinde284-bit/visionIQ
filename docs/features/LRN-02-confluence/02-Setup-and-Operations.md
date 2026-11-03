# LRN-02 — Setup, commands and operating instructions

## Prerequisites
Use a local .NET SDK matching the solution, the approved project repository, and a Confluence account/token authorized for the specified spaces. Keep all credentials in untracked local `.env`; never put PATs, email/token pairs, page content, private identifiers or access restriction lists in GitHub issues or chat.

## Configuration names
```dotenv
CONFLUENCE_BASE_URL=https://<tenant>.atlassian.net/wiki
CONFLUENCE_SPACE_KEYS=<approved-space-keys>
CONFLUENCE_USER_EMAIL=<credential-email>
CONFLUENCE_API_TOKEN=<secret>
# Per-user read check (API only): JSON map of e-mail -> Atlassian account ID, kept outside source control.
CONFLUENCE_IDENTITY_MAP_FILE=<path-to-identity-map.json>
```
These are **templates**, not credential values. The observed integration used Confluence Cloud, the approved `ERK` space, and no other approved space. `CONFLUENCE_IDENTITY_MAP_FILE` is read only by the VisionIQ API: without it, Confluence evidence is never shown to anyone. The map is a decision for your Atlassian administrators (for example exported from Atlassian Guard or your SSO directory); see `docs/graph-api.md`.

## Which command should I run?

| Your goal | Action | Safety |
| --- | --- | --- |
| Check whether the code builds | Run the build command below | No source ingestion |
| Find decision candidates | Run `discover-confluence-decisions` below | Reads metadata, restrictions and eligible labels; no page bodies or evidence writes |
| Confirm ERK connectivity | Run `inventory` below | Read-only remote access; writes a local inventory result |
| Validate ACL logic | Run the automated tests below | Synthetic/test fixtures, no live user impersonation |
| Import historical pages | **Do not run by default** | Requires separate approval and destination review |

### Automated authorization/unit tests

```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj `
    --configuration Release --no-restore `
    --filter "FullyQualifiedName~Confluence|FullyQualifiedName~ContentRevisionTests" `
    --verbosity normal
if ($LASTEXITCODE -ne 0) { throw "Confluence tests failed" }
```

**Recorded result:** 37 passed / 0 failed / 0 skipped. Confirm a nonzero number of tests are discovered. These verify simulated ACL behavior, **not** the live API access-control acceptance requirement.

## Read-only candidate discovery
```powershell
git switch feature/learn
git pull
dotnet build .\VisionIQ.sln --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
dotnet run --no-build --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- discover-confluence-decisions
Write-Host "Discovery exit code: $LASTEXITCODE"
if ($LASTEXITCODE -ne 0) { throw "Discovery incomplete; check sanitized errors" }
```
Queries current page metadata, effective restrictions and labels of complete/unrestricted pages; requests no page storage bodies. Prints totals only. A 0 exit means all label requests succeeded, **not** that individual Confluence viewer permissions have been tested. It can make hundreds of REST calls. Do not run repeatedly without a reason.

## Inventory
```powershell
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- inventory
```
Inventory checks configured source reachability, including Confluence counts. It does not import content.

## Controlled local sync — do not repeat as routine verification

**This command has already been run for LRN-02 evidence.** Running it again can read the space's ACLs and write local snapshots and checkpoints. Check that `INGESTION_OUTPUT_DIR` resolves to the intended isolated folder, and never commit its outputs.

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

## Expected results from the completed discovery run

```text
Confluence space ERK: pages=589, eligible-complete-unrestricted=314, title-candidates=0, label-candidates=0, label-lookup-failures=0, withheld-or-unverified=275
```

This is a **recorded result**, not a hardcoded or guaranteed output. It means that the 314 ACL-complete unrestricted pages have no matching **title or label** under current patterns; it does not examine decision templates, and it does not prove that any specific engineer can retrieve a page.

If discovery reports `label-lookup-failures` above zero, treat the run as incomplete. If eligibility counts change, permissions may have changed; do not change the ACL safety logic merely to restore previous totals.

## Safety and troubleshooting
- No page bodies or private identities should appear in discovery output.
- `label-lookup-failures > 0`: exit 2, inspect sanitized diagnostics; never assume a failed label request means no candidate.
- `withheld-or-unverified`: includes both known restricted and unknown ACLs, which are intentionally excluded.
- Zero title and label candidates is inconclusive: template-based detection requires normalization of page content.
- Avoid `--full` imports to find one ADR. A targeted security test and LRN-03 retrieval enforcement are preferable.
- Never point canonical mode at production-like storage merely to complete documentation or discovery.
