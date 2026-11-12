# DEF-05 — Draft PR with the full evidence trail

> Story: DEF-05 on the [Trello board](https://trello.com/b/AaucJawp/defect-intelligence) ·
> Gate G3 · Depends on DEF-03, HG-01 · Design v1.11 §8.6.1, §8.6.4
>
> **Status:** implemented. Record mode is tested on PostgreSQL. `ado` mode is tested against a
> fake HTTP handler only and was never run against Azure DevOps (see
> [Known limits](#known-limits)). 11 November 2026.
> Overall flow: [DEFECT-ARCHITECTURE.md](../DEFECT-ARCHITECTURE.md).

*As a reviewer, I want the PR to explain defect → cause → risk → fix with links to the test runs,
so that I can approve or reject it on evidence.*

| Acceptance criterion | How it is met | Status |
| --- | --- | --- |
| The draft PR is linked to the work item and created by the bot, which has no merge permission. | `AdoPullRequestPublisher` pushes one commit on a new branch and creates the pull request with `isDraft: true` and the work item. It has its own token and no call that votes, completes or updates a pull request. | Partly: the bot's permission in ADO is HG-01 and is not set up |
| The description cites commit SHAs, test IDs and run logs. | `report.description` (Python): defect, reproduction, cause, fix, a table of gates, risks, a table of runs with commit, outcome and log hash. The API adds the commit it really pushed. | Partly: logs are cited as hashes without an address |
| PR checks link to the validation results. | One PR status per sandbox gate, with the result and detail in its description. | Not met: a status has no link |

## Modes

| `FIX_PR_MODE` | What happens |
| --- | --- |
| `off` (default) | The fix result is stored. Nothing is queued |
| `record` | What would be pushed and opened is kept in `workflow.fix_pull_requests`. Nothing leaves the service |
| `ado` | Pushes the branch, opens the draft PR and posts statuses with `ADO_PR_PAT`. **Writes to a repository** |

`ADO_PR_PAT` must differ from `ADO_READ_PAT` and `ADO_COMMENT_PAT`; `ado` without it stops start-up.
`FIX_AUTO_START=true` asks for a fix run after a defect ticket's brief (not after an enhancement's,
a blocked brief or a fix result).

Only a `VERIFIED` result has a pull request. Every other result is `skipped` / `no_draft_pull_request`.
Before the pull request is opened, and again when a retry finds one that already exists, the
head commit's tree is compared with the tree that was tested; a different tree ends the delivery
as `failed`. A result without a tested tree, with a branch outside `visioniq/fix/`, or with a
ticket, repository or baseline other than the run's is refused before anything is pushed.

## Where the code is

| Task | File (under `src/VisionIQ.Api/Features/Fixes/`) |
| --- | --- |
| S04-05 ADO PR creation adapter | `FixPullRequestPublishers.cs` |
| S11-14 Create draft PR with evidence | `FixPullRequestPublisher.cs`, `FixPullRequestStore.cs`; text from `app/defect/report.py` |
| S13-06 Expose PR validation links | `FixGates.cs` (`Checks`) |
| Start of the fix run | `FixRunStarter.cs`, `Features/Events/InboundEvents.cs` |

## How to test

```powershell
dotnet test tests/VisionIQ.Api.Tests --filter FullyQualifiedName~FixPullRequestTests
```

On a disposable PostgreSQL (step 2C.2 of [TESTING-KICKOFF-TODAY.md](../../TESTING-KICKOFF-TODAY.md)):

```powershell
$env:VISIONIQ_FIX_DB_INTEGRATION = '1'
dotnet test tests/VisionIQ.Api.Tests --filter FullyQualifiedName~FixDeliveryDatabaseIntegrationTests   # 3 passed
```

| Check | Expected |
| --- | --- |
| A verified result arrives, twice | One row in `workflow.fix_pull_requests`: `published`, mode `record`, six checks, `"is_draft":true`; no row in `workflow.brief_publications` |
| A blocked result | `skipped` / `no_draft_pull_request` |
| A result whose baseline differs from the run's | `failed` / `fix_result_does_not_match_run`, nothing recorded as a pull request |
| `FIX_PR_MODE=off` | No row |
| A defect brief with `FIX_AUTO_START` | Exactly one deep run with its `analysis.requested` event, also after redelivery |
| `pr-description.md` from the demo run | Defect, reproduction, cause, fix, gates, runs |

**Completion evidence** (the draft PR link for the live defect): **blocked by the read-only rule.**
`FIX_PR_MODE=ado` must not be used against the real organization.

## Known limits

The review of PR #19 found that a retry took over an existing pull request without the tree
check, that a missing tree skipped the check, that the push trusted the stored result alone, and
that ticket and model text were raw Markdown. All are fixed; see
[DEFECT-ARCHITECTURE.md](../DEFECT-ARCHITECTURE.md#review-of-pr-19-fixed-and-still-open).

Still open:

- **`ado` mode was never run against Azure DevOps.** Every request shape is tested against a fake
  HTTP handler only. It also writes to a repository, which the read-only rule forbids on the real
  organization.
- A status has no link to its result, so "PR checks link to the validation results" is not met.
- The bot's permission in Azure DevOps (HG-01) is not set up.
- A branch that was pushed and then failed the tree check stays in the repository: the bot may
  not delete branches.
- `Retry-After` on a 429 is not honoured.
