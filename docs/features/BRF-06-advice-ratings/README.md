# BRF-06 — Rate each piece of advice

> Story: [Trello BRF-06](https://trello.com/c/V7mqfkUG/26-brief-brf-06-rate-each-piece-of-advice) ·
> Gate G2 · Depends on BRF-05 · Design v1.11 §7.4.1, §8.10, §8.11
>
> **Status:** implemented and unit-tested on branch `feature/BRF-02-08-brief-pipeline` (not
> committed). Run end to end locally with a sample comment event. Not yet tried with a real ADO
> service hook (see [Open items](#open-items)). 10 November 2026.
> Overall flow: [BRIEF-ARCHITECTURE.md](../BRIEF-ARCHITECTURE.md).

*As an engineer, I want to mark each advice item as accepted, modified, rejected or not applicable,
so that VisionIQ learns which advice helps.*

| Acceptance criterion | How it is met |
| --- | --- |
| Rating happens inside ADO, with no separate page. | The engineer replies on the ticket, one line per advice item (`ADV-001 accepted`). An ADO service hook for "work item commented" delivers the comment to the same `POST /webhooks/ado` as pickups. No VisionIQ page or login is involved. |
| Each rating is stored against the advice ID with who and when. | `workflow.advice_feedback` stores the advice id, the brief revision it belongs to (`result_id`), the disposition, an optional note, the reviewer's ADO identity and the comment's time. |

## How to rate

Reply on the ticket. Each line that starts with an advice id is one rating:

```
ADV-001 accepted
ADV-002 rejected: we never touched the retry counter
ADV-003 modified - kept the retry but added a dedupe key
ADV-004 n/a
```

| Word | Stored as |
| --- | --- |
| `accepted`, `accept` | `ACCEPTED` |
| `modified`, `modify` | `MODIFIED` |
| `rejected`, `reject` | `REJECTED` |
| `n/a`, `na`, `not applicable` | `NOT_APPLICABLE` |

Case does not matter. Text after the word is kept as a note (up to 500 characters). The rest of the
comment can say anything. The brief on the ticket ends with these instructions.

A rating records what the engineer did with the advice. It is not a statement that the advice was
correct (§8.10): approving a PR, or saying nothing, never marks advice as right.

## What happens to a comment

```
 POST /webhooks/ado   (same secret as pickups)
   eventType = workitem.commented ?            no → handled as a pickup or ignored (BRF-01)
   organization and project in the policy?     no → 202 ignored: organization_not_allowed | project_not_configured
   on PICKUP_ALLOWED_WORK_ITEM_IDS, if set?    no → 202 ignored: work_item_not_allowlisted
   written by a VisionIQ bot identity?         yes → 202 ignored: bot_comment
   any rating line? (a comment carrying the    no → 202 ignored: no_rating_in_comment
   brief marker never counts)
   AdviceFeedbackStore.RecordAsync
     the brief most recently published on this ticket     none → 202 ignored: no_published_brief
     advice ids that brief contains                       none match → 202 ignored: unknown_advice_ids
     INSERT one row per rating                            same ADO event again → 202 duplicate
   202 recorded, with the recorded and the unknown ids
   database unavailable → 503, ADO retries
```

A comment never starts a run, whoever wrote it.

Ratings are append-only: rows cannot be updated or deleted (a database trigger). If the engineer
changes their mind, the new comment adds a row and the latest row for an advice id is its current
disposition. The same ADO delivery never records twice
(`UNIQUE (source, delivery_id, advice_id)`).

A rating goes to the brief most recently published on the ticket. Advice ids restart at `ADV-001`
in every brief, which is why each row also stores the brief revision (`result_id`).

### ADO service hook setup (per project)

Project settings → Service hooks → Create subscription → **Web Hooks** → trigger **Work item
commented on**:

1. URL: `https://<api host>/webhooks/ado`.
2. Basic authentication: any user name, password `PICKUP_WEBHOOK_SECRET`.
3. Resource details to send: **All**. Messages and detailed messages: **None**.

Creating a service hook changes the ADO project. Under the read-only rule of
[TESTING-KICKOFF-TODAY.md](../../TESTING-KICKOFF-TODAY.md) it is not allowed against the real
organization; test with the sample event below.

## Code map

| Task | Where (`src/VisionIQ.Api/Features/Feedback/`) |
| --- | --- |
| S13-05 advice feedback mechanism | `AdviceFeedback.cs` (`AdviceFeedbackParser`: rating lines and the comment event), `AdviceFeedbackEndpoint.cs` |
| S10-09 record atomic advice outcomes | `AdviceFeedbackStore.cs` (`PostgresAdviceFeedbackStore`) |
| Routing | `Features/Pickup/PickupWebhookEndpoints.cs` (a non-pickup event that is a comment goes to feedback) |
| Schema | `workflow.advice_feedback` |
| Sample event | `docs/features/BRF-06-advice-ratings/sample-workitem-commented.json` |

Feedback is on whenever ticket pickup is on (`PICKUP_POLICY_FILE` set); it has no setting of its own.

## How to test it

```powershell
dotnet test tests/VisionIQ.Api.Tests --filter FullyQualifiedName~AdviceFeedbackTests
```

32 tests: each rating form; several ratings in one comment; comments that are not ratings; the
brief's own text never rating itself; bounded notes; the comment event parser; a rating stored with
who and when; bot, unknown project and wrong organization ignored; an older payload without an
identity id; a storage outage answered `503`; a wrong secret answered `401`.

Database test (opt-in): `BriefDeliveryDatabaseIntegrationTests` records ratings against a published
brief, proves a redelivery is a duplicate, a changed mind is a second row, and that an `UPDATE` is
refused.

Locally (completion evidence: one rating given and the stored record with its advice id), after the
[walkthrough](../BRIEF-ARCHITECTURE.md#how-to-test-the-whole-flow-locally) has published a brief:

```powershell
$comment = Get-Content docs/features/BRF-06-advice-ratings/sample-workitem-commented.json -Raw
Invoke-RestMethod -Method Post http://127.0.0.1:5100/webhooks/ado -Headers @{ Authorization = $auth } -ContentType application/json -Body $comment
# → status: recorded, recorded: ADV-001, ADV-002, ADV-003
docker compose exec postgres psql -U visioniq -d visioniq -c "SELECT advice_id, disposition, note, reviewer_id, rated_at, result_id FROM workflow.advice_feedback ORDER BY advice_id"
```

Post it again → `duplicate`. Change `"id"` and the `System.History` text to `ADV-001 rejected` →
a second row for `ADV-001`.

## Open items

- The "work item commented" payload is parsed from its documented shape (`System.History` holds the
  comment). It has not been checked against a delivery from a real ADO organization.
- `reviewer_id` is the ADO identity id. Older payloads carry only `"Name <unique name>"`; then the
  unique name is stored, because the story requires knowing who rated.
- A rating applies to the latest brief on the ticket. To rate an older brief there is no syntax yet.
- The evidence and outcome judgment of §8.10 (`SUPPORTED_BY_TEST`, `VERIFIED_CORRECT`…) and the
  events `AdviceDispositionRecorded` / `AdviceEvidenceUpdated` are not built; they belong with the
  HUMAN GATE and outcome-learning stories.
- Ratings are stored in the API's schema. Feeding them back into retrieval and lessons is a later
  story.
- The preferred UX of §8.11 (buttons in a work-item extension) is not built; structured comments
  are the MVP.
