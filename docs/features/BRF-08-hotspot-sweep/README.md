# BRF-08 — Know the risky areas before tickets arrive

> Story: [Trello BRF-08](https://trello.com/c/5Y31kCDt/28-brief-brf-08-know-the-risky-areas-before-tickets-arrive) ·
> Gate G2 · Depends on LRN-09 · Design v1.11 §3.4, §7.5 · Tasks S10-12, PROP-07, PROP-08
>
> **Status:** implemented and unit-tested on branch `feature/BRF-02-08-brief-pipeline` (not
> committed). Run locally on synthetic history and against PostgreSQL. No Cloud Scheduler job
> exists yet (see [Open items](#open-items)). 10 November 2026.
> Overall flow: [BRIEF-ARCHITECTURE.md](../BRIEF-ARCHITECTURE.md).

*As a team lead, I want a regular sweep of the areas with the most incidents, so that briefs and
planning can flag hotspots early.*

| Acceptance criterion | How it is met |
| --- | --- |
| The sweep runs on a schedule and can be re-run on demand. | `python -m app.hotspots.cli` runs a sweep now and prints the report; a scheduler calls the same command with `--scheduled`. The service can also sweep by itself every `HOTSPOT_SWEEP_INTERVAL_HOURS`. An advisory lock makes sure two sweeps never run at once. |
| Results are adjusted for activity, so busy areas aren't flagged just for being busy. | An area is scored by its defect-fix **rate** (fixes per recorded change), smoothed and compared with its repository's rate. An area with many fixes and proportionally many changes scores about 1.0 and is not flagged. |

## How an area is scored (PROP-07)

An *area* is the first `HOTSPOT_AREA_DEPTH` (2) directories of a file path: `src/Payments`.

```
 incidents     = defect fixes + 1 extra for each fix that was later reverted
 smoothed rate = (incidents + 10 × repository rate) / (changes + 10)
 lift          = smoothed rate / repository rate           1.0 = like the rest of the repository
```

| Band | Rule |
| --- | --- |
| `INSUFFICIENT_DATA` | Fewer than 5 recorded changes in the window |
| `HOT` | Lift 1.5 or more, and at least 3 defect fixes |
| `WATCH` | Lift 1.2 or more, and at least 2 defect fixes |
| `NORMAL` | Everything else |

Why each part is there:

- **Rate, not count.** Twelve fixes in 300 changes is ordinary; twelve fixes in 40 changes is not.
- **Smoothing.** Three fixes in four changes is a 75% rate from almost no evidence. Adding ten
  "average" changes pulls small areas towards the repository rate, so thirty fixes in sixty changes
  outranks three in six.
- **Minimum fixes and minimum changes.** A band needs enough evidence; too little is
  `INSUFFICIENT_DATA`, not "normal".
- **Per repository.** Each area is compared with its own repository, so a repository where most
  changes are bug fixes does not flag all of its areas.
- **Reverts weigh double.** A verified revert is stronger evidence than a fix (§7.5).

Scores rank areas for attention. They are not failure probabilities. The constants are at the top
of `app/hotspots/scoring.py`, and every sweep stores the scoring version (`hotspot-v1`).

## What a sweep reads and writes (S10-12)

| Input | Table | Notes |
| --- | --- | --- |
| Changes | `evidence.fix_change_sets` | Every PR and commit with the files it changed, inside `HOTSPOT_WINDOW_DAYS` (180) |
| Defect fixes | `evidence.ticket_fix_links` | Explicit or inferred links, not disputed, not retracted, whose ticket type is Bug, Defect, Incident or Issue (or unknown) |

One transaction writes `knowledge.hotspot_sweeps` (trigger, window, depth, counts, scoring
version) and one `knowledge.hotspot_areas` row per area: changes, defect fixes, reverted fixes,
lift, band, rank, and the evidence (up to 50 ticket-and-fix pairs). Sweeps are kept, so a report
can be reproduced and compared with the next one.

**Briefs use it.** `PostgresBriefSources.history` reads the flagged areas of the latest sweep. An
area that contains a file in the ticket's scope becomes the `hotspot_areas` risk factor, and can
raise the band (a `HOT` area with two or more fixes is `HIGH`; any flagged area is at least
`MODERATE`). Before the first sweep the factor shows "no data".

## Running it

```powershell
python -m app.hotspots.cli                         # on demand, text report
python -m app.hotspots.cli --json --limit 50       # JSON report
python -m app.hotspots.cli --window-days 90 --depth 3
python -m app.hotspots.cli --scheduled             # what a scheduler runs
```

Exit code 0 when a sweep was stored, 3 when another sweep was already running. Output on the demo
history:

```
Hotspot sweep 9265a271-… (on_demand, last 180 days, hotspot-v1)
6 areas scored, 1 flagged

  #  band                lift fixes reverted changes  area
  1  HOT                 1.72     6        1       6  VisionIQ-Demo/demo-repo: src/Payments
       ticket 101 -> PR 201
       ticket 102 -> PR 202 (reverted)
       ticket 105 -> PR 205
  2  NORMAL              0.56     0        0       8  VisionIQ-Demo/demo-repo: src/Catalog
  3  NORMAL              0.46     0        0      12  VisionIQ-Demo/demo-repo: web/site
```

`web/site` has the most changes and is not flagged: none of them fixed a defect.

Two ways to schedule it:

| Way | How |
| --- | --- |
| In the service | `HOTSPOT_SWEEP_INTERVAL_HOURS=168` (weekly). The first sweep runs a minute after start-up |
| Cloud Scheduler | A Cloud Run job that runs `python -m app.hotspots.cli --scheduled` with the Intelligence image, started by a Cloud Scheduler trigger |

## Code map

| Task | Where (`src/VisionIQ.Intelligence/app/hotspots/`) |
| --- | --- |
| S10-12 schedule periodic sweeps | `cli.py` (`main`, `run_scheduled_sweeps`), `sweep.py` (`run_sweep`, `report`), `app/main.py` |
| PROP-07 activity-adjusted scores | `scoring.py` (`score`, `area_of`), `sweep.py` (`ACTIVITY_SQL`) |
| PROP-08 ranking and false positives | `tests/intelligence/test_hotspots.py`, `test_brief_pipeline_db.py` |
| Use in briefs | `app/brief/history.py` (`HOTSPOTS_SQL`), `app/brief/risk.py` |
| Schema | `knowledge.hotspot_sweeps`, `knowledge.hotspot_areas` |

## How to test it

```powershell
python -m pytest tests/intelligence/test_hotspots.py
```

13 tests: a busy area with an average rate is not flagged while a quiet area with the same number
of fixes is; a tiny sample is not a hotspot; smoothing; reverts weigh more; each repository judged
against itself; stable ranking; the report with its evidence; the CLI on demand and when another
sweep is running.

Database test (opt-in): `test_the_hotspot_sweep_scores_recorded_history_and_feeds_the_next_brief`
runs a sweep on PostgreSQL, checks the stored area and its evidence, and checks that the next brief
shows the flagged area and a `HIGH` band.

Locally (completion evidence: a hotspot report with the evidence behind each area), after seeding
the demo history ([walkthrough](../BRIEF-ARCHITECTURE.md#how-to-test-the-whole-flow-locally),
step 2):

```powershell
docker compose exec intelligence python -m app.hotspots.cli
docker compose exec postgres psql -U visioniq -d visioniq -c "SELECT rank, band, lift, defect_fixes, reverted_fixes, changes, area FROM knowledge.hotspot_areas WHERE sweep_id = (SELECT sweep_id FROM knowledge.hotspot_sweeps ORDER BY completed_at DESC LIMIT 1) ORDER BY rank"
```

## Open items

- The Cloud Scheduler trigger and the Cloud Run job are not provisioned (FND-06 territory).
- Reporting decision D11 is unresolved: the report is CLI text or JSON. Nobody receives it
  automatically.
- A sweep covers every project in the database and is not filtered by access scope. Its report
  names areas, counts and ticket ids, so treat the CLI as an operator tool. Briefs only use areas
  of the ticket's own organization and project. Two repositories of one project that share a
  folder name are not told apart there.
- The thresholds and the prior are an initial rule; they have not been checked against held-out
  outcomes (§7.5). PROP-08's false-positive checks here are on synthetic cases.
- An area is a directory prefix. Service or component ownership (§3.4 "files/services") is not
  used.
- A change's date is its PR's or commit's last source update, or the time ingestion saw it when
  that is unknown, so a fresh backfill counts as recent.
- Old sweeps are never deleted.
