# BRF-07 — Brief arrives within minutes

> Story: [Trello BRF-07](https://trello.com/c/Vv4jeG1i/27-brief-brf-07-brief-arrives-within-minutes) ·
> Gate G2, measured at G5 · Depends on BRF-05 · Design v1.11 §11.1.1
>
> **Status:** implemented and unit-tested on branch `feature/BRF-02-08-brief-pipeline` (not
> committed). Measured locally on two demo runs only; the G5 measurement on real runs is open.
> 10 November 2026. Overall flow: [BRIEF-ARCHITECTURE.md](../BRIEF-ARCHITECTURE.md).

*As an engineer, I want the brief soon after I pick up the ticket, so that it helps before I start
coding.*

| Acceptance criterion | How it is met |
| --- | --- |
| Time from pickup to posted brief is recorded for every run. | `workflow.brief_publications` stores `picked_up_at`, `published_at` and `time_to_brief_ms` for each run, and the API logs one line per published brief with the time. |
| It is measured separately from PR and CI time. | The clock starts when the pickup is stored and stops when the comment is posted. Nothing after the brief (PR, build, review) is in it, and no PR or CI table is read. |

## What is measured

```
 pickup stored ──► run created ──► event to Intelligence ──► brief stored ──► event to API ──► comment posted
 │◄────────────────────────────────── time_to_brief_ms ──────────────────────────────────────────────────►│
                                     │◄── retrieval_ms ──►│◄─ generation_ms ─►│
                                     │◄──────────── total_ms ────────────────►│
```

| Measurement | Start | End | Stored in |
| --- | --- | --- | --- |
| `time_to_brief_ms` | The pickup's `received_at` (webhook or sweep). For a run without a pickup, such as `/dev/analysis-requests`, the run's `requested_at` | `published_at`, when the comment was posted or recorded | `workflow.brief_publications` |
| `retrieval_ms` | Before the history search | After it | `knowledge.briefs` |
| `generation_ms` | Before the model call | After the evidence check and any second opinion | `knowledge.briefs` |
| `total_ms` | Pipeline start | Brief built | `knowledge.briefs` |

`accepted_at` (when Intelligence stored the request) is kept with the brief too, so queueing time
can be told apart from analysis time.

The start is fixed when the publication is queued, in the same transaction as the result, so a
retried post still counts from the original pickup. `PARTIAL` and `BLOCKED` briefs are measured
like `PUBLISHED` ones and reported per state.

## The report (S10-11)

```powershell
python -m app.brief.latency                 # last 30 days
python -m app.brief.latency --days 7 --json
```

In Docker: `docker compose exec intelligence python -m app.brief.latency`. Output from the local
demo runs:

```
Time to brief (pickup -> comment posted), last 30 days
ALL                      runs     2   median     4.2s   p95     6.8s   max     6.8s
PUBLISHED                runs     2   median     4.2s   p95     6.8s   max     6.8s

Inside Intelligence (request accepted -> brief stored)
retrieval                runs     2   median    31 ms   p95    34 ms   max    34 ms
generation               runs     2   median     1 ms   p95     1 ms   max     1 ms
total                    runs     2   median    64 ms   p95    67 ms   max    67 ms
```

Those numbers come from the offline model stand-in on a laptop, so generation takes about a
millisecond. Almost all of the 4 to 7 seconds is waiting: the pickup worker, the two outbox
dispatchers and the publish worker each poll every 1 to 2 seconds, and the Pub/Sub emulator
delivers by push. A real model adds its own response time to `generation_ms`.

Percentiles are nearest-rank: the p95 is a time that one of the runs actually took.

Each published brief also logs (API, event id 621):

```
Brief for run … (VisionIQ-Demo #4711) published as PUBLISHED via Record: time to brief 2558 ms, 4 advice items
```

and Intelligence logs `brief_built` with `retrieval_ms`, `generation_ms` and `total_ms`. Both are
structured JSON on stdout, which Cloud Logging collects as they are.

## The benchmark (S15-07)

`test_benchmark_the_pipeline_overhead_is_small_and_repeatable` runs the pipeline 50 times with
in-memory history and the offline stand-in, and fails if the median exceeds 50 ms or the p95
exceeds 250 ms. It measures what VisionIQ's own code adds around retrieval and the model, the same
way every time, so a change that eats the time budget is caught in the test suite. It is not the
end-to-end number: that comes from the report over real runs.

## Code map

| Task | Where |
| --- | --- |
| S10-11 measure time-to-brief | `src/VisionIQ.Api/Features/Briefs/BriefPublicationStore.cs` (`EnqueueAsync`, `MarkPublishedAsync`), `BriefPublisher.cs` (log 621), `src/VisionIQ.Intelligence/app/brief/latency.py` (report), `app/brief/pipeline.py` (stage timings) |
| S15-07 benchmark brief latency | `tests/intelligence/test_brief_latency.py` |
| Schema | `workflow.brief_publications` (`picked_up_at`, `published_at`, `time_to_brief_ms`), `knowledge.briefs` (`accepted_at`, `retrieval_ms`, `generation_ms`, `total_ms`) |

## How to test it

```powershell
python -m pytest tests/intelligence/test_brief_latency.py
```

10 tests: the percentile function, the report per state and per stage, the CLI output and the
benchmark. `BriefDeliveryDatabaseIntegrationTests` (opt-in) checks that a published run has its
time recorded.

For the completion evidence (median and p95 over the test runs), publish several briefs with the
[local walkthrough](../BRIEF-ARCHITECTURE.md#how-to-test-the-whole-flow-locally) and run the report.
Or query it directly:

```powershell
docker compose exec postgres psql -U visioniq -d visioniq -c "SELECT result_state, count(*), percentile_disc(0.5) WITHIN GROUP (ORDER BY time_to_brief_ms) AS p50_ms, percentile_disc(0.95) WITHIN GROUP (ORDER BY time_to_brief_ms) AS p95_ms FROM workflow.brief_publications WHERE status = 'published' GROUP BY result_state"
```

## Open items

- No target is enforced. The story says "within minutes"; a threshold and an alert are for G5,
  once there are real runs.
- The clock starts when VisionIQ stores the pickup, not when the engineer changed the ticket. A
  webhook that ADO delivers late, or a pickup recovered by the sweep up to 15 minutes later, is
  not visible in this number. The ADO change time is not stored with the pickup.
- The measurement at "planned corpus size" (S15-07) needs a loaded knowledge base and a real
  model. The benchmark here covers the code path only.
- Timings are logged and stored; OpenTelemetry spans (named in the backlog) are not emitted.
