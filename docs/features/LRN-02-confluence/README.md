# LRN-02 — Confluence design decisions

## Scope and evidence (2 November 2026)

- Approved source: Confluence Cloud, `ERK` space. Do not expand scope without authorization.
- Live inventory: 589 pages. ACL records: 589; 527 complete, 62 incomplete.
- 213 ACLs are complete/restricted; 314 complete/unrestricted; 1 incomplete/restricted; 61 incomplete/unrestricted.
- Controlled local JSONL sync since 2026-11-01: 2 pages exported with positive version numbers and version histories, zero unsafe exported-page ACL matches, 3 pages withheld from local page export. Run status **Partial**, exit 2. These are not equivalent to 589 imported page bodies.
- 37 Confluence-focused automated tests passed, zero failed/skipped. No real end-to-end unauthorized-engineer retrieval test has been shown.
- The two sampled pages yielded zero design-decision records; neither had labels or extracted decision properties. This does not mean no ERK design decisions exist.

## Safe discovery command

On `feature/learn`, from the repository root:

```powershell
git pull
dotnet run --configuration Release --project .\src\VisionIQ.Ingestor.Cli -- discover-confluence-decisions
Write-Host "Discovery exit code: $LASTEXITCODE"
```

The command queries **current metadata and view restrictions** for the approved Confluence Cloud spaces; **it does not request storage page bodies** and does not use ingestion sinks, write checkpoints or upload to PostgreSQL/GCS. It reports aggregate counts only, with no page titles, IDs, URLs or ACL members. It applies inherited restriction completeness and excludes restricted pages, **but this is not a verified per-engineer authorization check**.

**Candidate coverage is deliberately limited to title patterns** (ADR-number, Decision, Design decision, Architecture decision). Label-only ADRs and decision-template properties are not inspected by this metadata-only command. Empty results are inconclusive. Metadata discovery may still perform numerous Confluence API requests; avoid frequent repeat runs.

## Acceptance boundary

The original LRN-02 requirements are (1) preserve versions and permissions and (2) prevent disclosure of Confluence pages to unauthorized engineers. Ingestion-side ACLs and unit tests are implemented, but a real identity-aware retrieval denial test remains open for the LRN-03 retrieval integration. Never assume the ingestion credential grants all engineers the same visibility. Unknown ACLs fail closed; do not switch Confluence data to canonical ingestion based solely on this discovery output.

Reference: `docs/LRN-01-Test-Report.md` for the prior LRN-01 boundary; the LRN-02 test logs are kept locally outside source control.
