# LRN-04 — Local testing

Acceptance remains OPEN. Local and cloud results are tracked separately.

## Database readiness — 3 November 2026

User-executed PowerShell output confirms:

| Check | Observed result | Status |
| --- | --- | --- |
| Isolated container | `visioniq-lrn03-test-postgres`, Up 17 hours; `127.0.0.1:55433->5432/tcp` | PASS |
| PostgreSQL readiness | Accepting connections | PASS |
| Applied migration | `20261101_0012` | PASS |
| LRN-04 tables | `evidence.ticket_fix_links`, `evidence.ticket_fix_reviews`; owner `visioniq_test` | PASS |

These checks establish availability and table presence only. Runtime-role privileges, actual columns/constraints, persistence semantics and source-grounded acceptance are not yet established by this output.

Use only this isolated database for local persistence tests. The root `.env` currently selects the regular local database on port `5432`; do not use it unchanged for integration testing. Never record credentials here.

## Focused tests

Run from the repository root on `feature/learn-01-14-TEST`:

```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~TicketFixResolverTests|FullyQualifiedName~TicketFixProjectionSinkTests|FullyQualifiedName~LinkExtractorTests" `
  --verbosity minimal
Write-Host "LRN-04 focused test exit code: $LASTEXITCODE"
```

User-executed result on 3 November 2026: **PASS — 25 total, 25 succeeded, 0 failed, 0 skipped; exit code 0**. Test duration: 2.4 seconds; build succeeded in 10.0 seconds. This confirms the earlier agent execution. These synthetic tests do not replace database testing or the five known real ticket-to-fix reviews.

## Remaining checks

### Schema inspection — 3 November 2026

User-provided `\d+` output confirms both table definitions. Links have a SHA-256 association primary key, source/project/ticket scope, PR/commit presence constraint, explicit/inferred/unknown classification, separate verified/disputed review status, JSON-array evidence/revert fields and scoped indexes. Reviews have a unique association/review-reference pair and a foreign key with `ON DELETE RESTRICT`; graph assertions also reference links with `ON DELETE RESTRICT`.

Status: **PASS for schema inspection**. These definitions alone do not establish immutable review history or correct application transitions. The pasted output ends at the role-query column headings; role values and grant results were not received. Runtime-role permissions remain **PENDING**.

- Inspect actual table definitions and restricted API-role privileges.
- Verify source replay, review audit, classification preservation and revert persistence against PostgreSQL.
- Exercise revert-before-link arrival and project/repository identity consistency.
- Review five known ADO ticket-to-fix examples with the team.
- Complete cloud validation separately before overall acceptance.

### Runtime-role inspection — 3 November 2026

User output confirms `visioniq_lrn03_api` can log in and has no superuser, createdb or createrole capability. Effective SELECT/INSERT/UPDATE/DELETE privileges are all true on both ticket-fix tables. Role capability inspection: PASS. Append-only audit protection: OPEN FINDING (see acceptance findings). No permissions were changed.
