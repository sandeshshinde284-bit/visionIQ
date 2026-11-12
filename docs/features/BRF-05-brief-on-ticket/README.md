# BRF-05 — Brief appears on the ticket

> Story: [Trello BRF-05](https://trello.com/c/JHGZhiy2/25-brief-brf-05-brief-appears-on-the-ticket) ·
> Gate G2 · Depends on BRF-01, BRF-04 · Design v1.11 §3.5, §8.3, §8.11
>
> **Status:** implemented and unit-tested on branch `feature/BRF-02-08-brief-pipeline` (not
> committed). Run end to end locally in `record` mode. **Not yet posted to a real Azure DevOps
> ticket** (see [Open items](#open-items)). 10 November 2026.
> Overall flow: [BRIEF-ARCHITECTURE.md](../BRIEF-ARCHITECTURE.md).

*As an engineer, I want the brief posted as a comment on my ticket, so that I read it where I
already work.*

| Acceptance criterion | How it is met |
| --- | --- |
| One comment per run, with the run ID and status: published, partial or blocked. | One `workflow.brief_publications` row per run (primary key `run_id`), queued in the same transaction as the run's result. The comment's first lines show the status, the risk band and the run id. A retry first looks for the run's marker among the ticket's comments, so a failure after a successful post never gives a second comment. |
| Evidence links open the source item in ADO, Git or Confluence. | Each advice item and evidence entry links to the source URL that ingestion stored. Only `http` and `https` URLs become links. |
| VisionIQ's own comments never trigger a new run. | A comment is not a pickup: only a move into a pickup state is (BRF-01), and the bot's own updates are ignored by `bot_identities`. The rating parser also ignores the bot and any comment carrying the brief marker (BRF-06). |

## How delivery works

```
 analysis.completed arrives (POST /events/pubsub)
   one transaction: event_inbox + analysis_results + run completed
                    + brief_publications (status pending, picked_up_at)     only if BRIEF_PUBLISH_MODE is not off
 BriefPublishWorker (every 2 s)
   claim one pending publication at a time (lease 2 min, SKIP LOCKED)
   read the brief from knowledge.briefs by result id          unreadable → failed / brief_unreadable
   render the HTML comment (BriefCommentRenderer)
   publish:
     record → nothing leaves the service; the comment is stored in brief_publications.comment_body
     ado    → attempt 2 or later: GET the ticket's comments, reuse the one with this run's marker
              else POST {org}/{project}/_apis/wit/workItems/{id}/comments
   mark published: comment id, SHA-256 of the comment, published_at, time_to_brief_ms
```

A run answered only because the pipeline is switched off (`analysis_pipeline_disabled`) gets no
comment. Every other result does, including `BLOCKED`, so the engineer sees why there is no advice.

### What the comment shows (S13-04)

1. `VisionIQ brief · Published | Partial | Blocked · Risk: <band>`, the run id and the ticket revision.
2. For `PARTIAL` and `BLOCKED`: the reason in plain words.
3. The risk band with its explanation and factors (a factor without data shows "no data").
4. Advice: `ADV-001 [Approach | Do | Don't | Risk] statement — Source: E1` with a link. A second
   reviewer's doubt is shown under the item.
5. Likely affected files, the evidence list with its notes (for example "a fix linked to this
   ticket was later reverted"), and the limits of the brief.
6. How to rate the advice by replying on the ticket.
7. A marker line: `visioniq-brief run=<run id> result=<result id>`.

Every value from the brief is HTML-encoded, so text that came from tickets, commits or pages
cannot inject markup or script links into the comment.

### Writing to Azure DevOps (S04-03)

`AdoCommentPublisher` is the only code in VisionIQ that writes to a source system. It can add a
comment and nothing else: it never changes a title, field, state, assignee or an existing comment
(§8.3). It is separate from the read-only `AdoClient` on purpose.

| `BRIEF_PUBLISH_MODE` | Behaviour |
| --- | --- |
| `off` (default) | Results are stored; nothing is rendered or queued |
| `record` | The comment is rendered and kept in `workflow.brief_publications`. No external call |
| `ado` | The comment is posted with `ADO_COMMENT_PAT`. Needs `ADO_ORG_URL` (https) |

`ADO_COMMENT_PAT` must belong to the bot identity listed in the pickup policy's `bot_identities`,
with the scope Work Items (Read & write) and nothing else. Start-up fails if it equals
`ADO_READ_PAT`, so the token used for reading can never write.

### Failure handling (S10-08)

| Failure | Behaviour |
| --- | --- |
| ADO returns 429 or 5xx, or cannot be reached | Retried with backoff: 15 s doubling to 15 min. After 8 attempts: `failed` / `publish_attempts_exhausted` |
| 401, 403 or 203 (wrong, expired or under-scoped token) | `failed` / `ado_comment_not_permitted`, no retry |
| 404 (ticket or project gone) | `failed` / `ado_work_item_not_found`, no retry |
| The ticket id is not a work-item number, or the brief cannot be read | `failed`, nothing posted |
| Any other error (a dropped connection, a database error) | Retried and capped like an ADO error; it never stops the other publications |
| The API stops after posting but before recording | The next attempt finds the marker and records that comment instead of posting again |
| Several API instances | Claims use a lease with `SKIP LOCKED`; `published` is set only on a `pending` row |

A failed publication does not lose the result: it stays in `workflow.analysis_results` and
`knowledge.briefs`. To retry one after fixing the cause:
`UPDATE workflow.brief_publications SET status = 'pending', attempt_count = 0, available_at = now(), reason = NULL WHERE run_id = …`.

## Code map

| Task | Where (`src/VisionIQ.Api/Features/Briefs/`) |
| --- | --- |
| S04-03 ADO ticket brief publisher | `BriefCommentPublishers.cs` (`AdoCommentPublisher`, `RecordingCommentPublisher`) |
| S10-08 publish result to ADO | `BriefPublisher.cs` (`BriefPublisher`, `BriefPublishWorker`), `BriefPublicationStore.cs` |
| S13-04 structured ticket brief | `BriefCommentRenderer.cs`, `BriefDocument.cs` |
| Queueing with the result | `Features/Events/InboundEvents.cs` |
| Settings and wiring | `BriefPublishOptions.cs`, `BriefServiceRegistration.cs`, `Program.cs` |
| Schema | `workflow.brief_publications` |

## How to test it

```powershell
dotnet test tests/VisionIQ.Api.Tests --filter "FullyQualifiedName~BriefCommentRendererTests|FullyQualifiedName~BriefPublisherTests"
```

41 tests: the comment's content, encoding and links; a sample brief validated against the shared
JSON schema; one post per run; `record` mode; retry, backoff and permanent failures; the exact ADO
request (URL, token, body) against a fake HTTP handler; reuse of an existing comment on retry; and
the settings, including the refusal of the read token.

Database test (opt-in, disposable PostgreSQL; needs `DB_API_PASSWORD`, `DB_INTELLIGENCE_USER` and
`DB_INTELLIGENCE_PASSWORD`):

```powershell
$env:VISIONIQ_BRIEF_DB_INTEGRATION = '1'
dotnet test tests/VisionIQ.Api.Tests --filter FullyQualifiedName~BriefDeliveryDatabaseIntegrationTests
```

Locally, in `record` mode, follow the [walkthrough](../BRIEF-ARCHITECTURE.md#how-to-test-the-whole-flow-locally)
(steps 1 to 4) and open `brief.html`.

No automated test contacts Azure DevOps. Under the read-only rule of
[TESTING-KICKOFF-TODAY.md](../../TESTING-KICKOFF-TODAY.md), do not use `ado` mode against the real
organization.

### Completion evidence (when a write is approved)

The story's evidence is a screenshot of the brief on two live tickets. That needs a project where
a bot comment is allowed:

1. Create the bot's PAT (Work Items: Read & write) and add the bot to `bot_identities`.
2. Set `BRIEF_PUBLISH_MODE=ado`, `ADO_ORG_URL` and `ADO_COMMENT_PAT`.
3. Move a Bug to In Progress. Expect exactly one comment, and
   `SELECT status, mode, comment_id, time_to_brief_ms FROM workflow.brief_publications` showing
   `published` / `ado`.
4. Check that the comment did not start another run (`workflow.ticket_pickups` has no new row).

## Open items

- `ado` mode has not been run against a real organization: the comments API version
  (`7.1-preview.4`), the bot's permissions and how ADO renders the HTML are unverified.
- With two API instances, a post that takes longer than its 2-minute lease could be repeated by
  the other instance. Each publication is claimed alone to keep that window small.
- Existing comments are searched in the latest 200 only when deciding whether a retry already
  posted.
- A corrected brief is a new run and a new comment; the old comment is not edited or removed.
- Jira is not supported (§8.3 names it as a later adapter).
- The comment is the MVP surface. The work-item extension panel of §8.11 is not built.
