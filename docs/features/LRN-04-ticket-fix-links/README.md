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

From repository root with branch `feature/learn`:

```powershell
git switch feature/learn
git pull --ff-only origin feature/learn
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj `
  --configuration Release `
  --filter "FullyQualifiedName~TicketFixResolverTests|FullyQualifiedName~TicketFixProjectionSinkTests" `
  --verbosity minimal
Write-Host "LRN-04 focused test exit code: $LASTEXITCODE"
```

**Expected based on source:** 15 xUnit cases (8 resolver, 7 projection). **Not yet executed in this review**; no result should be recorded as PASS before user output.

## Test gate B — real PostgreSQL

Use only the isolated `visioniq-lrn03-test-postgres` local database at `127.0.0.1:55433`. Confirm existing Alembic migrations, API role isolation and the actual table schema before writing any test rows. Test source claim replay, review state preservation, explicit vs inferred, dispute, and revert in an isolated transaction. Do not touch the ordinary database (`5432`) or Cloud SQL. **Pending test setup and execution.**

## Test gate C — five known source-grounded examples

Review five approved ADO ticket→PR/commit links, confirm the reference is actually declared by ADO, and compare project/repository scoping and revert evidence; log any errors. Do not publish private ticket titles, source content or personal data. The synthetic `Five_known_ticket_fix_associations_are_explicit_and_not_falsely_verified` test proves deterministic behavior, not the Trello completion-evidence requirement.

## Code review findings — 3 November 2026

Code inspection confirms `PostgresTicketFixLinkStore.UpsertSourceAsync` persists source classification and evidence refs, while `RecordReviewAsync` appends reviewed decisions transactionally and checks for a reverted association. The store maintains source association, reviewed verdict and revert markers as distinct evidence dimensions. **No real PostgreSQL integration run has yet been observed for LRN-04.**

**Potential gap — ingest ordering:** `RecordRevertAsync` updates only *existing* `ticket_fix_links` rows matching source, project, repository and original SHA. If a revert commit is processed **before** the corresponding ticket→commit claim exists, the UPDATE can affect zero rows; current `TicketFixProjectionSink` does not visibly retry that revert once the claim is subsequently created. Test reverse arrival order and checkpoint/replay behavior before signoff. This is a code-level risk, not a confirmed production failure.

**Potential gap — scope:** The source-scoped SQL update requires an exact repository and project match. Verify that artifact URL identity normalization makes the PR/work-item association and revert-commit project/repository IDs equivalent in real data.

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
| Out-of-order revert persistence | Not yet tested | OPEN RISK |
| Isolated PostgreSQL review/replay/revert | Not yet tested | PENDING |
| Five approved ADO reference checks | Not yet recorded | PENDING |

## Review log

3 November 2026: original Trello story retrieved, code and xUnit tests located on `feature/learn`, documentation created. **No LRN-04 test output supplied yet; acceptance remains OPEN.**

## Next stories today

After LRN-04 test evidence, apply the same Trello→code→focused tests→database integration→living docs sequence to LRN-05, LRN-06 and LRN-07. Keep `feature/learn-08-14` separate and draft PR unmerged until review completes.
