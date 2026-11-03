# LRN-04 — Link each ticket to the fix that closed it

> Story: [Trello LRN-04](https://trello.com/c/QBov52Kp/10-learn-lrn-04-link-each-ticket-to-the-fix-that-closed-it)
>
> **Status:** existing implementation identified; acceptance review in progress. Do not mark complete until real acceptance evidence is recorded. Updated 3 November 2026.

## Story and acceptance

An engineer must be able to see which PRs/commits were linked to a past defect and whether the fix was later reverted, without promoting guesswork to fact.

| Original acceptance | Existing code evidence | Required verification |
| --- | --- | --- |
| Links come only from explicit references; time proximity never creates a link | `TicketFixProjectionSink` maps ADO work-item artifacts and PR/commit declarations; resolver distinguishes source claims and mentions | Negative test with nearby unrelated commit, positive explicit reference |
| Each link is explicit, inferred, verified, disputed or unknown | `FixEvidenceStatus` and `TicketFixSourceClassification`; separate review outcome in `PostgresTicketFixLinkStore` | Verify provenance and persistent review transitions |
| Ticket may link to several fixes and a later revert | Resolver maintains multiple association keys and unambiguous full-SHA revert detection | Verify multiple source links, revert and idempotence, including actual PostgreSQL |

**Tasks:** S05-08 deterministic explicit links; S05-09 inferred association store; S05-10 unresolved/disputed handling.

**Required completion evidence:** five **known** ticket→fix links reviewed; each correct or error logged. A synthetic five-link test exists but **does not replace** a review of five source-grounded real examples.

## Existing code map

- `src/VisionIQ.Ingestor/Links/TicketFixResolver.cs`: explicit/inferred/verified/disputed/unknown, source-scope filtering, revert detection, review semantics.
- `src/VisionIQ.Ingestor.Cli/TicketFixProjectionSink.cs`: archives canonical evidence before projecting links; routes work-items, PRs and commits.
- `src/VisionIQ.Api/Features/TicketFixes/PostgresTicketFixLinkStore.cs`: scoped association persistence and review outcomes.
- `tests/VisionIQ.Ingestor.Tests/TicketFixResolverTests.cs`: eight xUnit cases, including five synthetic claims, multi-fix, no proximity inference, ambiguous revert and human-review transitions.
- `tests/VisionIQ.Ingestor.Tests/TicketFixProjectionSinkTests.cs`: seven xUnit cases, including archive-first ordering, explicit PR/work-item associations, inferred commit mentions and revert handling.

## Test gate A — focused synthetic tests

From repository root on `main` (or the corresponding company-repository feature branch):

```powershell
git switch main
git pull --ff-only origin main
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~TicketFixResolverTests|FullyQualifiedName~TicketFixProjectionSinkTests" `
  --verbosity minimal
Write-Host "LRN-04 focused test exit code: $LASTEXITCODE"
```

**Expected based on source:** 15 xUnit cases (8 resolver, 7 projection). **Not yet executed in this review**; no result should be recorded as PASS before user output.

## Test gate B — real PostgreSQL

Prefer the isolated `visioniq-lrn03-test-postgres` local database at `127.0.0.1:55433` (or another explicitly disposable CI test database). Confirm existing Alembic migrations, API role isolation and the actual table schema before writing any test rows. Test source claim replay, review state preservation, explicit vs inferred, dispute, and revert in a disposable database. Do not touch the ordinary development database (`5432`) or Cloud SQL. **Pending test setup and execution.**

## Test gate C — five known source-grounded examples

Review five approved ADO ticket→PR/commit links, confirm the reference is actually declared by ADO, and compare project/repository scoping and revert evidence; log any errors. Do not publish private ticket titles, source content or personal data. The synthetic `Five_known_ticket_fix_associations_are_explicit_and_not_falsely_verified` test proves deterministic behavior, not the Trello completion-evidence requirement.

## Code review findings — 3 November 2026

Code inspection confirms `PostgresTicketFixLinkStore.UpsertSourceAsync` persists source classification and evidence refs, while `RecordReviewAsync` appends reviewed decisions transactionally and checks for a reverted association. The store maintains source association, reviewed verdict and revert markers as distinct evidence dimensions. **No real PostgreSQL integration run has yet been observed for LRN-04.**

**Ingest ordering issue — code fix committed, execution pending:** Prior code updated only existing `ticket_fix_links` rows, so reverts arriving before ticket→commit links could be lost. Migration `20261103_0018` introduces source-scoped `evidence.ticket_fix_revert_observations`. The store now records each full-SHA revert observation independently and joins those observations into later claims. A shared transaction-scoped PostgreSQL advisory lock serializes the operations for a given source/project/repository/original SHA. Regression test `Revert_observed_before_a_fix_is_preserved_scoped_and_idempotent` was added. The fix remains **unverified by execution** until the isolated database test passes.

**Potential gap — scope:** The source-scoped SQL update requires an exact repository and project match. Verify that artifact URL identity normalization makes the PR/work-item association and revert-commit project/repository IDs equivalent in real data.

## New LRN-04 database remediation — execution gate

The repository now contains migration `db/migrations/versions/20261103_0018_ticket_fix_revert_observations.py`. Apply the full forward migration chain to a **disposable local PostgreSQL database** and run `python -m db.manage roles` as required to grant the limited API role write access to the new table. Never test on shared Cloud SQL.

**Opt-in PostgreSQL regression** (environment variables must point to a disposable migrated test database):

```powershell
$env:VISIONIQ_TICKET_FIX_DB_INTEGRATION = "1"
$env:DB_HOST = "127.0.0.1"
$env:DB_PORT = "55433"
$env:DB_NAME = "visioniq_lrn03_test"
$env:DB_API_USER = "visioniq_lrn03_api"
# DB_API_PASSWORD must already be securely configured in the current shell.
dotnet test .\tests\VisionIQ.Api.Tests\VisionIQ.Api.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~TicketFixDatabaseIntegrationTests" `
  --verbosity minimal
Write-Host "LRN-04 PostgreSQL test exit code: $LASTEXITCODE"
```

The two database test cases cover immutable review replay, reverse-arrival revert observations, scoped association isolation and duplicate replay. Note: the test suite also contains a non-opt-in association-key check; verify the runner's actual test count and skip status, not just process exit code. Revert projection into the relationship graph and five authorized source-grounded examples still require separate validation.

**Deployment sequencing:** Migration `0018` must complete before deploying the updated runtime store; the new SQL references the observation table.

## Findings to verify

- A commit-message `AB#1234` mention is **inferred**, not evidence that the commit fixed the ticket.
- Completed PR is not a verified fix by itself.
- Ambiguous abbreviated revert SHAs are not assigned automatically.
- One source ticket can have multiple fix associations, and source identity scopes must not cross projects.
- A failed raw archive prevents writing unbacked fix links.

## Test gate A result register

| Test | Executed result | Status |
| --- | --- | --- |
| Resolver focused xUnit (8 cases) | No user output yet | PENDING |
| Projection sink focused xUnit (7 cases) | No user output yet | PENDING |
| Out-of-order revert persistence | Fix + isolated PostgreSQL regression test committed; not executed | IMPLEMENTED / UNVERIFIED |
| Isolated PostgreSQL review/replay/revert | Existing review-history test + added reverse-order integration test; execution pending | PENDING |
| Five approved ADO reference checks | Not yet recorded | PENDING |

## Review log

3 November 2026: original Trello story retrieved, implementation reviewed, and acceptance documentation created. The source was then consolidated into `main`. Implemented reversal-order persistence remediation on `main` in commits `5b4c634` (Alembic migration), `e4b7b32` (.NET store), `4dc7b50` (PostgreSQL regression test). **No fresh LRN-04 test output has been observed; acceptance remains OPEN.**

## Next stories today

After LRN-04 test evidence, apply the same Trello→code→focused tests→database integration→living docs sequence to LRN-05, LRN-06 and LRN-07. The earlier LRN-01–14 feature branches were merged into the personal repository `main`. For corporate-repository transfer, inspect differences before cherry-picking; do not overwrite company changes.
