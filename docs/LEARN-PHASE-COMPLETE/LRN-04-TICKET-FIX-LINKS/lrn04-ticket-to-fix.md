# LRN-04 — Ticket-to-fix provenance and reviewed outcomes

**Implementation:** Integrated into the current LEARN codebase. References to the original feature branch / PR are historical.  
**Acceptance:** **OPEN — five approved real ticket-to-fix examples have not been independently signed off.**  
**Related features:** LRN-01 ingestion, LRN-03 evidence, LRN-05 currentness, LRN-07 graph.  
**Public documentation:** Never publish enterprise source URLs, private ticket IDs, internal records, credentials or individual owners.

## Story and classification

LRN-04 connects a historical defect to source-backed PRs, commits, decisions, and possible reverts. It must never present time proximity or a ticket mention as proof of causality.

| Classification | Contract |
|---|---|
| `Unknown` | Association exists, but is not eligible to be called a fix; includes active or abandoned PRs. |
| `Inferred` | Reviewable hypothesis; a ticket ID mentioned in a commit message is inferred, not proof. |
| `Explicit` | The provider declares a work-item/PR/commit relationship; not necessarily a verified fix. |
| `Verified` | Authorized human review confirms an actual fix under current evidence. |
| `Disputed` | Human review contests the supposed fix. |

Source classification and human review outcome remain independent. A merged PR, a successful CI build, or a source-declared link alone does **not** prove that an issue was resolved, deployed, or regression-tested.

## Implemented source projection

- `TicketFixResolver` and `LinkExtractor` build deterministic candidates from ADO work-item/PR relationships, source PR commits and commit evidence, with project/repository-scoped identity. Multiple fixes for one ticket remain separate.
- Commit-message references to tickets are **`Inferred`** (review M-7), rather than explicitly proven defect fixes.
- Active and abandoned PRs remain documented as associated development work with source classification `Unknown`; a completed PR can upgrade a source association when the provider supplies new evidence.
- An unambiguous `This reverts commit <SHA>` message establishes an observed revert of that exact commit. Ambiguous shortened hashes are not guessed.
- An ADO-style PR title such as `Revert 'Earlier PR title'` produces at most a **candidate** title-matched revert when exactly one earlier matching PR was observed in the same process. Title matching never creates an authoritative revert, and cross-process title indexing is not implemented.
- A revert of a revert can restore a previous effective relation. Further reverts can invalidate it again. The observation chain and audit history are preserved.
- Changed source snapshots may retract links. Delayed, older source revisions and candidate-only evidence must not reactivate already retracted associations.
- Canonical source evidence is archived/persisted before relationship projection. A failed sink operation must not silently advance the source checkpoint.

**Source code:** `src/VisionIQ.Ingestor/Links/TicketFixResolver.cs`; `src/VisionIQ.Ingestor.Cli/TicketFixProjectionSink.cs`; `src/VisionIQ.Api/Features/TicketFixes/`.

## Persistence and security

The initial LRN-04 migration `20261030_0004` introduced `evidence.ticket_fix_links` and `evidence.ticket_fix_reviews`. Later schema work also maintains revert observations and link-retraction lifecycle. **Check the current migration head rather than assuming that the initial LRN-04 migration is the latest.**

The persistence layer uses restricted API-role credentials, maintains source scope/provenance and review decisions, rejects conflicting review-reference replays, and preserves immutable history across source replays, retraction and relinking. It stores relationship metadata, not raw free-text ticket bodies or credentials. Reading private evidence also requires current source authorization.

## Verification commands

Run from the solution root on .NET 10.

```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --filter 'FullyQualifiedName~TicketFixResolverTests|FullyQualifiedName~TicketFixProjectionSinkTests|FullyQualifiedName~LinkExtractorTests' --logger 'console;verbosity=normal'
dotnet test .\tests\VisionIQ.Api.Tests\VisionIQ.Api.Tests.csproj --configuration Release --filter 'FullyQualifiedName~TicketFixAssertionIdentityTests' --logger 'console;verbosity=normal'
```

The real PostgreSQL integration tests are **opt-in**. Verify that `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_API_USER` and `DB_API_PASSWORD` target a **migrated disposable database** (never Cloud SQL or shared development data) before enabling them:

```powershell
$env:VISIONIQ_TICKET_FIX_DB_INTEGRATION = '1'
dotnet test .\tests\VisionIQ.Api.Tests\VisionIQ.Api.Tests.csproj --configuration Release --filter 'FullyQualifiedName~TicketFixDatabaseIntegrationTests' --logger 'console;verbosity=normal'
Write-Host "Exit code:" $LASTEXITCODE
```

A green exit code with database tests **skipped** is not a database acceptance pass.

## Verified scope and remaining acceptance

| Check | Position |
|---|---|
| Five synthetic fixture associations, classification, multiple fixes, ambiguous SHAs, exact reverts | Implemented and covered by dedicated automated tests |
| Real PostgreSQL review history, replay, reverted/reinstated chains, stale-revision and candidate-revert guards | Dedicated opt-in integration tests exist; run against a disposable migrated database |
| Real work-item / PR change detection | Observed in LRN-05 tests on an approved separate company checkout; this does not prove an LRN-04 fix |
| Five **real** defect-to-fix associations reviewed, with supporting evidence and human decisions | **OPEN** |
| Current-ACL-checked user-visible trace from defect to fix and test | **OPEN / not demonstrated by the current LRN-04 acceptance record** |
| Enterprise Azure DevOps Revert-button semantics and cross-process title revert evidence | **OPEN**; title-only match remains candidate evidence |

The LRN-05 hands-on runs used a **separate private company `dev` checkout**, not an independently executed acceptance run on this public repository's `main`. The exact enterprise source IDs, full SHA, test logs and reviewer decisions must remain in the approved internal evidence location.

### Sign-off checklist

- [ ] Review **five known real defect cases** and record the exact provider-declared links, repository scope, approval/dispute outcomes and evidence references.
- [ ] Include a real revert/revert-of-revert case or declare the remaining provider coverage gap.
- [ ] Confirm source revision, current authorization and graph projection on one fixed release commit.
- [ ] Record actual test pass/fail/skip counts and disposable database results with full commit SHA in restricted evidence.

**Decision: LRN-04 code is integrated, but the user story is NOT yet fully accepted.**
