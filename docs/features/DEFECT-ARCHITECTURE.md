# DEFECT phase — architecture (DEF-01 to DEF-06)

> Design v1.11 §1.2, §4.5, §4.5.1, §8.6, §8.6.1 to §8.6.4, §8.7, §11.1.1 ·
> Branch `defect-stories_1to6` (PR #19) · 11 November 2026
>
> **Status:** implemented and tested locally on both sides, with fakes in place of Azure DevOps
> and of the model. Intelligence (Python) reproduces, fixes and verifies on a synthetic repository
> with real `git` and real test runs. The API (.NET) starts the fix run, opens the draft pull
> request, posts its checks and marks them stale after a new commit. **Not done:** a run against
> a real Azure DevOps organization, a real model or the Docker runner, and reading native CI
> results into the gates. The migrations and the SQL of both services were run on PostgreSQL 16
> (opt-in tests), and the Docker runner was started once against a local image. See
> [What is not done](#what-is-not-done) and
> [Review of PR #19](#review-of-pr-19-fixed-and-still-open) for what the review found, what was
> fixed and what is still open.

When a defect ticket has its brief, VisionIQ goes one step further: it shows the bug with a
failing test, proposes a small fix, proves the fix with the same test and the area's old tests,
and prepares a draft pull request with the whole evidence trail. An engineer decides; the bot
cannot approve or merge.

| Story | What it adds | Where |
| --- | --- | --- |
| DEF-01 | Reproduce the defect before fixing it | `workspace.py`, `runner.py`, `results.py`, `generate.py` |
| DEF-02 | Propose a bounded fix | `generate.py`, `guardrails.py`, the budget in `pipeline.py` |
| DEF-03 | Prove the fix works (same test, performance, flakiness) | `pipeline.py`, `results.py`, `performance.py` |
| DEF-04 | Old bugs don't come back | `regression.py`, `sources.py` |
| DEF-05 | Draft PR with the full evidence trail | `report.py`, `schema.py`; API: `FixPullRequestPublisher.cs`, `FixPullRequestPublishers.cs` |
| DEF-06 | Native CI runs on the PR, honestly | `gates.py`; API: `FixGates.cs`, `FixPullRequestHeadWatcher` |

Python paths are under `src/VisionIQ.Intelligence/app/defect/`; API paths under
`src/VisionIQ.Api/Features/Fixes/`.

Per story (acceptance criteria, how to test, known limits):
[DEF-01](DEF-01-reproduce-defect/README.md) ·
[DEF-02](DEF-02-bounded-fix/README.md) ·
[DEF-03](DEF-03-prove-fix/README.md) ·
[DEF-04](DEF-04-old-bugs-stay-fixed/README.md) ·
[DEF-05](DEF-05-draft-pr/README.md) ·
[DEF-06](DEF-06-honest-checks/README.md).
Test steps for the whole phase: [TESTING-KICKOFF-TODAY.md](../TESTING-KICKOFF-TODAY.md), step 2C.

## The flow

```
 analysis.requested (mode "deep", repository, base commit SHA)        FND-05 event, as for a brief
   │  push handler: stored as pending, 204                             app/events/processing.py
   ▼
 FixWorker claims the request (lease)                                  app/defect/worker.py
   1. policy: is this repository allowed? which runner, commands, paths, budgets     policy.py
   2. ticket text, under the run's access scope (as the brief)                       sources.py
   3. workspace: clone, both trees at exactly the base SHA                           workspace.py
   4. files to show the model: the brief's predicted scope + files the ticket names
   ── DEF-01 ───────────────────────────────────────────────────────────────────────────────
   5. model writes ONE new test file and predicts how it will fail                   generate.py
   6. execution manifest written (M1), then the test runs on the UNCHANGED baseline  runner.py
   7. red only if the failure is the predicted one, confirmed by a second run        results.py
        anything else → BLOCKED, with the reason, for an engineer
   ── DEF-02 ─────────────────────────────────── up to max_fix_iterations ───────────────────
   8. model proposes whole-file changes                                              generate.py
   9. guardrails: block → tell the model, next iteration; review → stop for engineer guardrails.py
  10. baseline + test + change = one commit on branch visioniq/fix/<ticket>-<run>    workspace.py
   ── DEF-03 / DEF-04 ───────────────────────────────────────────────────────────────────────
  11. the SAME test on the candidate (manifest M2); flaky → INCONCLUSIVE             results.py
  12. the area's old tests on the candidate; one that passed before and fails now
      → tell the model, next iteration                                               regression.py
  13. the touched project's suite on the candidate
  14. paired benchmark runs, baseline against candidate                              performance.py
   ── DEF-05 / DEF-06 ───────────────────────────────────────────────────────────────────────
  15. gates, each its own piece of evidence; never green before CI and review        gates.py
  16. draft PR: branch, title, description, checks (only for a verified fix)         report.py
 one transaction: fix_runs + fix_executions + llm_calls
                  analysis_requests = completed, outbox: analysis.completed (ids only)

 VisionIQ.Api (.NET, workflow schema)
   before: analysis.completed of a DEFECT ticket's brief + FIX_AUTO_START
           → a second run, mode "deep", same ticket revision and base SHA     FixRunStarter.cs
   after:  analysis.completed of the deep run → fix_pull_requests (pending)    InboundEvents.cs
   FixPullRequestWorker reads knowledge.fix_runs by result id
     no pull_request in the result → skipped (not verified, blocked, in review)
     mode ado: push ONE commit on a new branch from the base SHA, check the pushed
               commit's tree equals the tested tree, open a DRAFT PR linked to the
               work item, post one status per sandbox gate    (ADO_PR_PAT)
     mode record: keep what would be sent in fix_pull_requests, call nothing
   POST /webhooks/ado "pull request updated" with a new head commit
     → every check is posted again as pending (stale)        FixPullRequestHeadWatcher
```

Three choices run through all of it, the same as in the brief.

- **Code decides, the model writes.** Which commands run, where a file may go, whether a failure
  is the defect's failure, whether a patch may be applied and what the runs add up to are all
  computed by code. A model writes the test and the patch, nothing else.
- **Nothing is invented to fill a gap.** Every way of falling short has a state and a reason
  code. A run that used up its budget is `FAIL` or `INCONCLUSIVE`, never green.
- **Each service writes only its own schema** (FND-03). Intelligence stores the result; the API
  reads it by id and owns everything on Azure DevOps.

## Terms

| Term | Meaning |
| --- | --- |
| Baseline | The exact commit the defect is shown on. A full SHA; a branch name is refused because it moves. |
| Candidate | Baseline + the reproduction test + the proposed change, as one commit on a new branch. |
| Red / green | The same test fails on the baseline (red) and passes on the candidate (green). |
| Failure signature | The headline of a failure with addresses, line numbers and timings removed. The test's author predicts it before the test is run; red counts only if the prediction holds. |
| Execution manifest | What a run was pinned to: SHAs, runner image, tool versions, lock-file hashes, commands, environment variable names. Written before the code runs and never changed. |
| Gate | One piece of evidence (reproduction, fix test, old tests, suite, performance, patch policy, native CI, engineer review), kept apart from the others. |

## States, verdicts and reason codes

`analysis.completed` carries the state and the reason codes. The stored result also has a verdict.

| State | Verdict | Meaning | Reason codes |
| --- | --- | --- | --- |
| `PUBLISHED` | `VERIFIED` | Red on the baseline, green on the candidate, no known regression. A draft PR is prepared. Native CI and the engineer are still open. | none |
| `PARTIAL` | `FAIL` | Reproduced, but no candidate passed every check. | `fix_budget_exhausted` with `candidate_test_failed`, `regression_failed` or `suite_failed`; `performance_regression` |
| `PARTIAL` | `INCONCLUSIVE` | Reproduced, but a check gave no clear answer. | `candidate_test_inconclusive`, `regression_inconclusive`, `suite_inconclusive`, `performance_inconclusive`, `llm_budget_exhausted`, `llm_unavailable`, `llm_refused` |
| `PARTIAL` | `NEEDS_REVIEW` | The proposed change needs an engineer before it may be applied or run. The diff and the rules it hit are in the result. | `fix_needs_engineer_review` |
| `BLOCKED` | `BLOCKED` | The defect was not reproduced, so nothing was fixed. | `reproduction_unavailable`, `reproduction_not_red`, `reproduction_different_failure`, `reproduction_flaky`, `reproduction_environment_error`, `reproduction_test_not_run`, `reproduction_timeout`, `reproduction_test_invalid`, `llm_no_allowed_provider`, `repository_not_configured`, `baseline_not_pinned`, `baseline_not_found`, `repository_unavailable`, `runner_not_allowed`, `ticket_not_readable`, `scope_not_resolved`, `analysis_failed`, `deep_analysis_not_implemented` |

### Test outcomes (design §8.6.3)

`PASS`, `FAIL_TARGET_SIGNATURE`, `FAIL_DIFFERENT_SIGNATURE`, `FLAKY`, `ENVIRONMENT_ERROR`,
`TIMEOUT`, `NOT_RUN`, `UNAVAILABLE`, `INCONCLUSIVE`.

- A compile error, a missing module or a crashed runner is `ENVIRONMENT_ERROR`, not red.
- Where a pass is expected, a failure is rerun up to twice. One pass among the reruns makes the
  test `FLAKY`. The first result is kept in `first_outcome` and is never replaced.
- A red is confirmed by one more run. Runs that disagree make it `FLAKY`.
- A run the environment broke is retried once.

The three counts are `flaky_reruns`, `red_confirmations` and `environment_retries` in the policy.

## Per story

### DEF-01 — Reproduce the defect before fixing it

| Acceptance criterion | How it is met |
| --- | --- |
| The baseline commit and runner image are pinned before any test runs. | `workspace.prepare` accepts only a full commit SHA that exists in the repository and checks both trees are at it. A Docker runner needs an image by digest; a tag is refused when the policy is loaded. The manifest with both is written to disk before the first execution (a test checks the order). |
| The runner has no secrets and restricted network access. | Commands get an environment built from an allowlist, never the service's own (a test sets a database password and a PAT and shows the command does not see them). The Docker runner uses `--network none`, drops all capabilities and mounts only the tree. The local runner cannot cut the network: it records `network: unrestricted`, the result says so, and it is refused when `APP_ENV=production`. |
| The test fails with the target failure signature; anything else is reported as UNAVAILABLE, FLAKY or ENVIRONMENT_ERROR and escalated. | `results.classify` reads the JUnit report. Only the predicted failure, seen twice, is `REPRODUCED`. Every other outcome ends the run `BLOCKED` with its own reason code and a line saying an engineer is needed. No fix is attempted. |

Tasks: S11-01 `schema.py` and `contracts/fixes/*.schema.json`; S11-02 `workspace.py`,
`runner.py`; S11-03 `results.py`; S11-04 `generate.propose_test`.

The model may add one new file, in a path the policy allows, and must state the expected failure
with fragments that are not generic ("AssertionError" alone predicts nothing). A rejected test
gets one more attempt (`max_test_attempts`); both attempts are recorded.

### DEF-02 — Propose a bounded fix

| Acceptance criterion | How it is met |
| --- | --- |
| An approved model proposes the patch inside an isolated workspace. | `CODE_FIX` and `REPRODUCTION_TEST` are routed to Claude only, with no fallback, and only when decision D04 allows Claude to see the data class. The patch is applied in the throwaway clone; the source repository is never written to. |
| Changes to security, payment, identity, schema or hardware areas, to secrets, or to dependency versions stop for engineer review. | `guardrails.check` returns `review` findings for those (by path, and for text that looks like a credential) and for a change broader than the repository's budget. The run stops as `NEEDS_REVIEW` with the diff and the findings; the change is not applied and the model is not asked to work around it. |
| Iterations are capped; when the budget runs out the result is FAIL or INCONCLUSIVE, never green. | `max_fix_iterations` per repository, plus the model budget (`DEFECT_MAX_*`). Tests cover both ways of running out. |

A patch may never touch a test, a generated file, a path outside `writable_paths`, or an existing
file the model was not shown in full (it returns whole files, so the rest would be lost). Those
are `block` findings: the proposal is rejected, the model is told the rule code and may try again.

Note on S11-13: the card puts the iteration budget in .NET. It is in the Python pipeline, because
that is where the loop runs.

### DEF-03 — Prove the fix works

| Acceptance criterion | How it is met |
| --- | --- |
| The same test passes on the candidate in the same pinned environment. | Same test id, same recipe, same runner; the result links the red execution (baseline SHA) and the green one (candidate SHA). |
| The touched path is benchmarked with repeated paired runs; a noisy result is INCONCLUSIVE. | `performance.py`: alternating baseline and candidate pairs, warm-up pairs dropped, medians compared, spread as noise. Over the noise limit → `INCONCLUSIVE`. Slower than the threshold and more than the noise → `REGRESSION`. Otherwise "no regression demonstrated under these conditions". With no benchmark for the changed path the result says `NOT_MEASURED`. |
| Flaky results are rerun up to twice; the first failure is never erased. | `results.run_with_policy`. |

### DEF-04 — Old bugs don't come back

| Acceptance criterion | How it is met |
| --- | --- |
| Historical regression tests are found through the ticket → PR → test links. | `sources.HISTORICAL_FIXES_SQL` finds past fixes of readable tickets that changed the same files and were not reverted; the test files those fixes changed are the candidates (design §4.5.1 tier 2). Tests found only by file name are run too, but labelled `inferred`. |
| An unsafe fix that passes the new test but fails an old one is caught before the PR. | The old tests run on every candidate. A test that fails is also run on the baseline: passing there and failing on the candidate is a regression, and the model gets another iteration. The demo's first prepared fix is exactly this case. |
| If no runnable old test exists, the brief and PR say so. | The regression result is `NOT_FOUND` with `HISTORICAL_TEST_NOT_FOUND`, the gate is `UNAVAILABLE`, and the PR description shows it. The brief is not changed by this work. |

Note on S11-08 and S11-09: the cards put these in .NET. They are in Python, next to the runner.
Tier 1 of §4.5.1 (declared Test Case links, `evidence.tested_by_links`) is not used yet, because
an ADO automated test name does not map to a file a recipe can run.

### DEF-05 — Draft PR with the full evidence trail

| Acceptance criterion | How it is met |
| --- | --- |
| The draft PR is linked to the work item and created by the bot, which has no merge permission. | `AdoPullRequestPublisher` pushes the candidate as one commit on a new branch, then creates the pull request with `isDraft: true` and the work item in `workItemRefs`. It uses its own token (`ADO_PR_PAT`, refused if it equals the read or the comment token). The class has no call that votes, sets auto-complete, completes or updates a pull request, and a test checks that only pushes, commits, pull-request creation and statuses are ever requested. **The permission itself** (a bot identity that cannot merge, and the rejected merge attempt in the ADO audit log) is ADO administration and is HG-01: not done. |
| The description cites commit SHAs, test IDs and run logs. | `report.description` (Python): defect, reproduction, cause (marked as the model's proposal), fix, a table of gates, risks, and a table of runs with commit, outcome, exit code and log hash. Cut to Azure DevOps's 4000 characters. The API posts it unchanged. |
| PR checks link to the validation results. | One PR status per sandbox gate (genre `visioniq`), with the gate's result and detail. The execution ids behind each gate are in the description's table and in the stored result. A status has no link to a log yet, because the logs are on the runner's disk (`DEFECT_ARTIFACT_BASE_URI` is where a bucket would go). |

The commit Azure DevOps creates has another SHA than the sandbox candidate. Before the pull request
is opened, the API compares the pushed commit's Git tree with the tree that was tested
(`patch.tree_sha`). A different tree ends the delivery as `failed` /
`pushed_tree_differs_from_tested_tree`: content that was not tested is never offered for review.

A retry first looks for the pull request, then for the branch, so a failure halfway never
produces a second commit or a second pull request. A result without a `pull_request` (blocked,
failed, inconclusive, stopped for review) is `skipped` with `no_draft_pull_request`.

### DEF-06 — Native CI runs on the PR, honestly

| Acceptance criterion | How it is met |
| --- | --- |
| CI runs on the PR after it exists; results are published as PR checks. | **Partly.** The repository's own pipeline and branch policy run on the pull request because it is an ordinary pull request; VisionIQ does not start or imitate them. VisionIQ's own sandbox results are published as PR statuses. **Not built:** reading the pipeline's result back into the `native_ci` gate, so that gate stays `NOT_RUN` in VisionIQ's own summary. |
| A required check that is failing, NOT_RUN or INCONCLUSIVE is never shown as green. | `gates.summarize` (Python) and `FixGates.Summarize` (.NET, S15-12 as an xUnit test): `GREEN` only if every required gate is `PASS`. Tests on both sides try every other status on every gate, with CI green and an approval. A PR status is `succeeded` only for `PASS`. |
| A new commit on the PR invalidates earlier results. | Each gate keeps the commit it was measured on. On a "pull request updated" service hook with a new head, `FixPullRequestHeadWatcher` posts every VisionIQ status again for the new head, where each is stale and therefore `pending`. If the head returns to the verified commit, the results apply again. |

The service hook "Pull request updated" must be added to the same subscription target as the
work-item hooks (`POST /webhooks/ado`, same secret).

## Data model (migration `20261110_0024`)

| Table | Owner | Holds |
| --- | --- | --- |
| `knowledge.fix_runs` | Intelligence | One immutable result per deep run: the full document (JSON), state, verdict, reproducibility, overall gate status, base, candidate and tree SHA |
| `knowledge.fix_executions` | Intelligence | One row per runner execution: purpose, baseline or candidate, commit, outcome, exit code, duration, log address and hash |
| `knowledge.llm_calls` (existing) | Intelligence | The model calls of the run: `reproduction_test`, `code_fix` |
| `knowledge.analysis_requests` (existing) | Intelligence | The queue. The brief worker now takes `brief` requests only and the fix worker `deep` ones |

| `workflow.fix_pull_requests` (migration `20261111_0025`) | API | One row per deep run: status (pending, published, skipped, failed), mode, repository, pull request id, branch, the verified head commit, the head last seen, the checks last posted |

Results and executions cannot be updated or deleted (triggers). The database also refuses a
`PUBLISHED` row that is not `VERIFIED`, and a `VERIFIED` row without a reproduction and a
candidate. Both migrations were applied to a fresh PostgreSQL 16 on 11 November 2026 (head
`20261111_0025`), and `tests/intelligence/test_defect_pipeline_db.py` checks the triggers and
constraints.

The result's schema is [contracts/fixes/fix-result.v1.schema.json](../../contracts/fixes/fix-result.v1.schema.json),
generated from the Python model (`python -m app.defect.schema`); a test fails if the two differ.

## Configuration

Everything is off by default. With nothing set, a deep request is answered `BLOCKED` /
`deep_analysis_not_implemented`, as before.

| Variable | Default | Meaning |
| --- | --- | --- |
| `DEFECT_PIPELINE_ENABLED` | `false` | Run the fix worker |
| `DEFECT_POLICY_FILE` | none | The fix policy (required when enabled). Example: [config/defect-policy.example.json](../../config/defect-policy.example.json) |
| `DEFECT_WORK_DIR` | system temp `visioniq-fix` | Throwaway workspaces |
| `DEFECT_ARTIFACT_DIR` | under the work dir | Manifests, logs and the candidate bundle of each run |
| `DEFECT_ARTIFACT_BASE_URI` | empty | Prefix of a log's address in the result, e.g. `gs://bucket/fix-runs/` |
| `DEFECT_KEEP_WORKSPACES` | `false` | Keep the cloned trees after a run (debugging) |
| `DEFECT_ALLOW_LOCAL_RUNNER` | `false` | Allow a policy with `"kind": "local"`. The local runner executes model-written code on this host with network access: a developer machine only. Ignored (refused) when `APP_ENV=production` |
| `DEFECT_BOT_NAME`, `DEFECT_BOT_EMAIL` | `VisionIQ Bot`, a no-reply address | Author of the candidate commit |
| `DEFECT_MAX_CALLS_PER_RUN`, `DEFECT_MAX_TOKENS_PER_RUN`, `DEFECT_MAX_SECONDS_PER_RUN` | `6`, `400000`, `1800` | Budget of one run. The seconds cover the test runs too |
| `DEFECT_WORKER_INTERVAL_SECONDS` | `2` | Polling interval of the worker |
| `DEFECT_OFFLINE_SCRIPT` | none | With `LLM_MODE=offline`: prepared test and patch answers for a demo |

The model settings are the brief's (`LLM_MODE`, `CLAUDE_ROUTE`, `LLM_CLAUDE_MAX_CLASSIFICATION`,
…). The code tasks go to Claude only, so with `CLAUDE_ROUTE=off` (the default until decision D04)
a live run ends `BLOCKED` / `llm_no_allowed_provider`.

API settings:

| Variable | Default | Meaning |
| --- | --- | --- |
| `FIX_PR_MODE` | `off` | `record` keeps what would be opened in the database; `ado` pushes the branch and opens the draft PR |
| `ADO_PR_PAT` | none | Required for `ado`: the bot's token for code (read and write) and pull requests. Must differ from `ADO_READ_PAT` and `ADO_COMMENT_PAT` |
| `FIX_AUTO_START` | `false` | After a defect ticket's brief, request a fix run (needs `DEFECT_PIPELINE_ENABLED` in Intelligence) |
| `FIX_PR_INTERVAL_SECONDS` | `2` | Polling interval of the delivery worker |

`.env.example` lists `DEFECT_PIPELINE_ENABLED`, `DEFECT_POLICY_FILE`, `DEFECT_OFFLINE_SCRIPT`,
`FIX_PR_MODE`, `FIX_AUTO_START` and `ADO_PR_PAT`. Note that it sets `FIX_PR_MODE=record`, while
the default without the variable is `off`. The other variables above are not in it; add the ones
you need by hand.

## Running against a real repository

Added for the first live end-to-end run (14 November 2026). All of it is configuration; with the
defaults nothing changes.

| Need | Setting | What it does |
| --- | --- | --- |
| Clone a private repository | `ADO_READ_PAT`, `ADO_ORG_URL` (the existing settings; scope *Code: Read* is enough) | Authenticates only the clone. The token goes to git through its environment as an HTTP header scoped to the organization: never on the command line, never in the clone's config, only over HTTPS, and only to repositories of `ADO_ORG_URL` (not to another organization on the same host). Redirects are refused. |
| Do not download the repository on every run | `DEFECT_REPO_CACHE_DIR` | One local bare mirror per repository. No network when the pinned commit is already there; otherwise only new commits are fetched. Each run still clones its own copy, so runs cannot affect each other or the mirror. |
| Download only what a run uses | `DEFECT_REPO_FETCH=commit` | Fetches the single commit the run is pinned to instead of cloning the repository with its history. A run never reads history: it lists and checks out that commit, commits on top, diffs, exports and bundles, and the bundle still applies to a full clone. If the server does not hand out a single commit the repository is cloned as before, so the mode cannot make a run fail. With the cache, single commits are kept in their own local store. |
| A .NET repository without changes to it | recipe `--results-directory . --logger trx;LogFileName={junit}` | The report at `{junit}` may be JUnit XML or TRX (xUnit, NUnit, MSTest). Only `Passed` is a pass; an unclear outcome is an error. |
| Cheaper models for simple fixes | `DEFECT_MODEL_SELECTION=by_complexity`, `DEFECT_MODEL_SIMPLE` / `_STANDARD` / `_COMPLEX` | See below. |

### Model selection by complexity

The routing table sends the two code tasks (reproduction test, fix) to Claude. With
`DEFECT_MODEL_SELECTION=by_complexity`, code rates each run before any model is called:

| Tier | When |
| --- | --- |
| simple | brief risk band `LOW`, 1 or 2 files in scope, no restricted area |
| complex | risk band `HIGH`, or a restricted area in scope, or 6 files or more |
| standard | everything else, including a run with no brief or an unresolved scope |

The two file counts (2 and 6) are constants in `app/defect/complexity.py`, not settings: there is no
data yet to tune them with. Change them there once real fix runs show how the first tier compared
with how each run went.

Each tier names a provider and optionally a model (`gemini`, `claude:claude-haiku-5-5`, ...). A
failed attempt is retried one tier up. If a tier's model returns an error, the usual route is
tried once. A tier's provider must be enabled and allowed for the run's data classification, the
same check as for any provider; a tier that is empty or not available follows the routing table.
The model of every call is stored in `knowledge.llm_calls`; the tier and its reasons are in the
logs (`fix_model_tier`, and `routing_reason=complexity_<tier>` on `llm_call`).

Sending code tasks to Gemini is possible this way but is not benchmarked (see the routing table's
note); measure it on real tickets before relying on it.

#### Not built: an AI-assisted difficulty rating

The rating above uses risk and scope. Those are not difficulty: a one-file race condition is hard
and looks simple; a renamed constant in seven files is easy and looks complex. Letting a model
rate the difficulty was analysed on 14 November 2026 and **deliberately not built yet**:

- It is a new task in the routing matrix (the task kinds mirror the master design's v1.11
  table), so it changes the design and the pinned routing revision: a decision for the team.
- It would have Gemini read source code to judge it, which the routing table defers until a
  benchmark justifies it.
- Its value is the quality of the rating, and that cannot be measured before real fix runs exist.

When it is built: the model **proposes** a difficulty (a category with reasons, no percentage);
code decides. The rule-based rating stays as a floor for risk (a restricted area or a HIGH risk
band is never rated below complex), the model may raise any rating, and may lower one only where
no risk rule applies. No answer, an invalid answer or "unsure" keeps the rule-based rating. The
ticket text stays untrusted data: it cannot talk the rating down.

Acceptance: on real fix runs, compare each run's first tier with how it went (attempts needed,
verdict). Build it only if the rule-based rating is shown to choose badly, and accept it only if
it chooses better at no higher cost per verified fix.

### When the clone fails

A failed clone ends the run as `BLOCKED` with one of these fixed codes. Git's own message is never
logged or stored (it can quote addresses and account names).

| Code | Meaning | What to check |
| --- | --- | --- |
| `git_not_installed` | No `git` executable where the pipeline runs | Rebuild the Intelligence image; on a host, install Git |
| `repository_access_denied` | The server refused the request (401/403, or no credentials) | `ADO_READ_PAT` is set, has scope *Code: Read*, has not expired, and belongs to an account that may read the repository; and the repository's address is in the organization of `ADO_ORG_URL` (the token is sent nowhere else) |
| `repository_not_found` | The repository does not exist at that address, or the token may not see it | `source` in the fix policy; the token's organization |
| `repository_unreachable` | DNS, connection or TLS failure | Network, proxy, firewall |
| `repository_redirected` | The address redirects; redirects are refused when a token is sent | Use the final address in the fix policy |
| `repository_timeout` | The clone took longer than 30 minutes | Repository size; use `DEFECT_REPO_CACHE_DIR` |
| `baseline_not_found` | The clone worked, but the commit is not in the repository | The branch of the pickup policy; the commit SHA |
| `repository_unavailable` | Any other failure | Run the clone check below |

### Verify a deployment: the clone check

`python -m app.defect.clone_check` does only what a fix run does first: read the policy, clone
with the read token, check out exactly that commit, delete the copy. It writes nothing to the
repository, runs no model and no test, and prints no token and no address beyond the host name.

Local Docker (PowerShell, from the repository root). Only the Intelligence service is rebuilt and
restarted; the database, its volume and the other containers are not touched.

```powershell
# 1. In .env (never commit it): ADO_ORG_URL and ADO_READ_PAT (the settings you already have), and
#    DEFECT_POLICY_FILE=/app/config/defect-policy.local.json  (a copy of the example in .\config)

# 2. Build the image and restart only this service
docker compose build intelligence
docker compose up -d --no-deps intelligence

# 3. Git is in the image; the service is ready
docker exec defect-intelligence-intelligence-1 git --version
Invoke-RestMethod http://127.0.0.1:5100/ready
Invoke-RestMethod http://127.0.0.1:8000/ready

# 4. The policy is readable in the container (prints repository ids, nothing secret)
docker exec defect-intelligence-intelligence-1 python -c "from pathlib import Path; from app.defect.policy import load_policy; import os; print([r.repository_id for r in load_policy(Path(os.environ['DEFECT_POLICY_FILE'])).repositories])"

# 5. A real, read-only clone at an exact commit (use a repository id of your policy and a full SHA)
docker exec defect-intelligence-intelligence-1 python -m app.defect.clone_check --repository <repository_id> --base-sha <40-hex commit>
if ($LASTEXITCODE -ne 0) { throw "clone check failed" }

# 6. No credential was written to the logs (the token itself is never printed here)
docker logs defect-intelligence-intelligence-1 2>&1 | Select-String -Pattern "Authorization|extraheader" -Quiet
```

Expected in step 5: `commit matches  yes`, `token in config none`, `RESULT: OK`. Step 6 prints
`False`. The same check as a test, against a real repository, without fakes:

```powershell
$env:VISIONIQ_REAL_CLONE_TEST = "1"
$env:VISIONIQ_REAL_CLONE_URL  = "https://dev.azure.com/<organization>/<project>/_git/<repository>"
$env:VISIONIQ_REAL_CLONE_SHA  = "<40-hex commit>"
# ADO_ORG_URL and ADO_READ_PAT must be set in this session as well
python -m pytest tests/intelligence/test_defect_clone_review.py -k real -v
```

Without `VISIONIQ_REAL_CLONE_TEST=1` both tests are reported as **skipped**, never as passed.

### Google Cloud (`visioniq-intelligence`)

| Setting | Value |
| --- | --- |
| `ADO_READ_PAT` | The same setting the API uses; the Intelligence service did not have it before. Map a Secret Manager secret (a fixed version; the runtime service account needs *Secret Manager Secret Accessor* on it). **Recommended: give this service its own token with scope *Code: Read* only.** It clones and does nothing else in Azure DevOps, so it does not need the wider token the sync uses; each Cloud Run service maps its own secret, so the name stays the same. |
| `ADO_ORG_URL` | The same value as on the API, for example `https://dev.azure.com/<organization>`. The read token is sent only to repositories under this address. |
| `DEFECT_POLICY_FILE` | A path inside the container. **The image does not contain `config/`**, so the policy must be mounted, for example as a Secret Manager volume (`/app/config/defect-policy.json`). The same holds for the API's `PICKUP_POLICY_FILE`: without it ticket pickup is off. |
| `DEFECT_WORK_DIR`, `DEFECT_REPO_CACHE_DIR` | By default Cloud Run's writable file system is in memory and counts against the instance's memory limit. Cloud Run can also mount a per-instance **ephemeral disk** (see Google Cloud's documentation on ephemeral disks for Cloud Run), which does not use memory: point both settings at it. Either way the storage is deleted when the instance stops and is not shared between instances, so a cache there only helps while an instance stays warm. |
| `DEFECT_REPO_FETCH` | `commit`. A run then downloads one commit, not the repository's history, which is what makes a persistent cache unnecessary in the cloud. Add a persistent volume only if measured clone times justify it. |

Egress: the service reaches `dev.azure.com` over HTTPS for the clone (the architecture's egress is
"private ranges only", so public traffic leaves through the service's default route).

Read-only checks for whoever manages the cloud project (they change nothing):

```powershell
$p = "gbr-mts-wgh-hkt-dev"; $r = "europe-west3"
# Which image is deployed, and which settings and secrets it has (names only, no values)
gcloud run services describe visioniq-intelligence --project $p --region $r --format "value(spec.template.spec.containers[0].image)"
gcloud run services describe visioniq-intelligence --project $p --region $r --format "yaml(spec.template.spec.containers[0].env, spec.template.spec.volumes)"
# The secret exists and has an enabled version
gcloud secrets versions list <secret mapped to ADO_READ_PAT> --project $p --filter "state=ENABLED" --format "value(name)"
```

After a deployment, run the clone check once with the same image, settings and service account,
for example as a one-off Cloud Run job whose command is
`python -m app.defect.clone_check --repository <id> --base-sha <sha>`. Creating that job is a
deployment step for the cloud manager; it writes nothing to Azure DevOps.

### What a complete fix run on Cloud Run still needs

Cloning works in the container. **Running the tests does not**, and installing git does not
change that:

- The `docker` runner starts a container per execution. A Cloud Run service has no Docker daemon
  and must not get one (Docker-in-Docker, or a mounted socket, would give generated code the
  service's own privileges).
- The `local` runner executes the commands as the service itself, next to its credentials and
  with its network. It is refused outside development, and must stay refused.

The minimum safe integration (S11-02) is a third runner kind behind the existing `Runner`
interface (`describe`, `environment_names`, `run(argv, tree, timeout, log)`), which hands each
execution to a separate, short-lived sandbox and returns its exit code, report file and log:

- one execution per sandbox, started from the policy's image pinned by digest;
- no credentials, no service account that can reach VisionIQ's data, no outbound network;
- only the exported tree as input (it has no `.git`) and the report and log as output;
- the same time limit, and the result judged by the same code as today.

A Cloud Run job or a Cloud Build private pool without an external address can provide this. It is
not built. Until it is, a fix run in the cloud cannot get past its first test execution: with a
`docker` runner the container cannot be started, and the run ends as `BLOCKED` with
`reproduction_environment_error`; a `local` runner is refused outside development
(`runner_not_allowed`). The complete flow runs where a Docker daemon exists: a developer machine,
or a VM.

## How to try it locally

This needs Git and Python with the repository's requirements. No Docker, database, Azure DevOps
or model provider.

1. Create the demo repository (a Python project whose retry policy retries a captured payment):
   ```powershell
   python scripts/defect-demo/make_demo_repo.py --out $env:TEMP\kiosk-demo
   ```
   It prints the next command with the paths and the baseline SHA filled in.
2. Run the pipeline (from `src/VisionIQ.Intelligence`):
   ```powershell
   python -m app.defect.cli --policy <out>\defect-policy.json --repository kiosk-demo `
     --base-sha <sha> --ticket <out>\ticket.json `
     --offline-script <out>\offline-script.json --out <out>\evidence
   ```
3. Expect `State PUBLISHED, verdict VERIFIED`, with:
   - `Reproduction: REPRODUCED` and the failure `AssertionError: captured payment was retried`;
   - `patch 1: regression_failed` (the first prepared fix brings back old bug #101 and the old
     test catches it), then `patch 2: candidate_accepted`;
   - `Gates: NOT_GREEN`, because native CI is `NOT_RUN` and the review is open.
   Timing on a busy machine can make the performance gate `INCONCLUSIVE`; the verdict is then
   `INCONCLUSIVE` and the exit code 3. That is the pipeline being honest, not a fault.
4. Look at `<out>\evidence\<run id>`: `result.json`, `pr-description.md`, `candidate.patch`,
   `candidate.bundle`, the manifests `M*.json` and one log per execution `X*.log`.

The prepared answers stand in for the model only. The tests and the benchmark are really run.

### Automated tests

```powershell
python -m pytest tests/intelligence                       # 622 passed, 97 skipped
python -m ruff check . ; python -m ruff format --check .  # clean
dotnet build VisionIQ.sln                                 # 0 errors
dotnet test VisionIQ.sln                                  # 750 passed, 23 skipped
```

Run on 11 November 2026 with the .NET 10 SDK. The skips are the opt-in database tests. Two of
those files cover the fix pipeline on a disposable PostgreSQL:

```powershell
$env:VISIONIQ_DB_INTEGRATION = '1'
python -m pytest tests/intelligence/test_defect_pipeline_db.py                                        # 7 passed
$env:VISIONIQ_FIX_DB_INTEGRATION = '1'
dotnet test tests/VisionIQ.Api.Tests --filter FullyQualifiedName~FixDeliveryDatabaseIntegrationTests   # 3 passed
```

How to start that database, and the full pass criteria, are in
[TESTING-KICKOFF-TODAY.md](../TESTING-KICKOFF-TODAY.md), step 2C.

Python: six files, `tests/intelligence/test_defect_*.py`. .NET: `FixGatesTests.cs` and
`FixPullRequestTests.cs`. Both sides read the same two stored results in
`contracts/fixes/fixtures/`, which were written by real pipeline runs.

## Logging

Every stage of a fix run and every step of the pull-request delivery writes a structured log line
with ids, codes and counts. Ticket text, source code, the generated test, the patch, model text,
test output and tokens are never logged. The events are listed in
[LOGGING-AND-MONITORING.md](../OPERATIONS-AND-INFRASTRUCTURE/LOGGING-AND-MONITORING.md#fix-pipeline-events-def-01-to-def-06).

## What is not done

- **Azure DevOps for real.** `ado` mode was never run against an organization. The request
  shapes (pushes, pull requests, statuses, refs, commits; API version 7.1) follow the documented
  REST API and are tested against a fake HTTP handler only. `FIX_PR_MODE=ado` writes to a
  repository: the read-only rule in [TESTING-KICKOFF-TODAY.md](../TESTING-KICKOFF-TODAY.md) applies.
- **Native CI in the gates** (S11-10). Nothing reads the pipeline's result on the pull request,
  so `native_ci` never becomes `PASS` in VisionIQ's summary. The pull request's own checks in
  Azure DevOps show it.
- **HG-01** (S00-06, S11-15, S11-16). The bot's permissions and branch policy are ADO
  administration and were not set up or tested; the engineer's approval, change request or
  rejection is not recorded.
- **A pushed branch that is not opened.** If the tree check fails, the branch stays in the
  repository: the bot may not delete branches.
- **A real model.** Only scripted and prepared answers were used. Decision D04 is open.
- **The Docker runner on a real repository.** Its command line is unit-tested, and it was
  started once by hand against a local image: the root file system was read-only, the network was
  blocked, a timeout stopped the container, and a Git checkout was refused as a mount. No test
  suite of a real repository has run in it, and there is no automated test with a container.
- **A real repository and a .NET test project.** The example policy's `dotnet test` recipe is
  untested against a real repository. It writes TRX, which is read natively, so the test project
  needs no extra package.
- **The service container.** The Intelligence image now has `git`, and Docker Compose mounts the
  policy, so a repository can be cloned in the container (`python -m app.defect.clone_check`).
  It still has **no test runner**: a complete fix run cannot run in Docker Compose or Cloud Run
  yet. See [What a complete fix run on Cloud Run still needs](#what-a-complete-fix-run-on-cloud-run-still-needs)
  (S11-02).
- **The database, partly.** Not run against PostgreSQL: `FindOpenedAsync` and `MarkHeadAsync`
  for a pull request in `ado` mode, and `DeferAsync`. The history query
  (`sources.HISTORICAL_FIXES_SQL`) was run, but only against a scope with no history. Everything
  else is covered by the two opt-in test files above.
- **Thresholds.** The rerun counts, the patch budgets and the performance thresholds are starting
  values. The design asks for the owning team to review them (§8.6.3, §8.6.4, §8.7).
- **`.env.example`.** It does not list `DEFECT_ALLOW_LOCAL_RUNNER`, and it sets
  `FIX_PR_MODE=record` where the default is `off`.

## Review of PR #19: fixed and still open

The review (11 November 2026) found three blockers and a number of smaller points. This is where
each one stands. Tests named here are in `tests/intelligence/test_defect_hardening.py`,
`tests/VisionIQ.Api.Tests/FixPullRequestTests.cs` and `FixGatesTests.cs`.

### Fixed

| Finding | What changed |
| --- | --- |
| **Blocker.** The container could write into the `.git` directory the host uses, and so run commands on the host. | No command runs in a Git checkout any more. Every execution gets `Workspace.export`: the files of exactly one commit in a fresh directory without a Git directory. The Docker runner refuses to mount a directory that has one. Host Git runs with hooks and `fsmonitor` switched off. |
| **Blocker.** Runs shared one working tree, so a generated test could change later runs; the exit code was ignored. | Because of the exports, a run never sees what an earlier run left. The reproduction test is laid over the export and the baseline tree is never written to. `classify` gives `PASS` only when the exit code agrees with the report. |
| **Blocker.** On a retry an existing pull request was taken over without the tree check. | The head of a found pull request is compared with the tested tree. If it differs, every check is posted as pending and the delivery ends `failed` / `pull_request_head_not_tested`. Only an active pull request into the expected branch is taken over. |
| A result without `tree_sha` skipped the tree check. | Refused before anything is pushed (`tested_tree_missing`). |
| The predicted failure was matched against the whole traceback. | Matched against the failure's headline only (exception and message). |
| A bug in the patch (`NameError`, `PermissionError`) was reported as an environment error. | Only setup and collection errors and toolchain messages count as the environment. |
| A timeout stopped the client, not the container; output was unbounded. | The container is killed by name, or the process tree is stopped. Only the head and the tail of the output are kept. Docker also runs with a read-only root file system and, off Windows, as the service's user. |
| A run could outlive its lease; a run that killed the process was started again without end. | No execution is started that cannot end inside `DEFECT_MAX_SECONDS_PER_RUN`, and the lease is that budget plus 15 minutes. A request claimed more than three times is answered `BLOCKED` without running. |
| Deep requests stayed `pending` after the pipeline was switched off. | Answered `BLOCKED` / `deep_analysis_not_implemented` when the service starts with the pipeline off. |
| The local runner was refused only when `APP_ENV=production`. | It needs `DEFECT_ALLOW_LOCAL_RUNNER=true` and is never allowed in production. |
| A non-text file could be replaced unseen; a directory path crashed the run; dependency and secret gaps. | New `block` findings `file_not_text`, `path_not_a_file`, `path_case_conflict`. More dependency files by default. A secret replaced by another secret is found. The generated test is scanned. |
| Old tests were matched by file name across repositories. | Matched by full path. Tests left out by the limit are named in the result. |
| Gates: a pass without a commit was never stale; a missing gate posted nothing; a duplicate could overwrite a failure. | Always six statuses. A missing gate and a pass without a commit are `pending`. The worse of two results is shown. |
| The head commit came from the webhook body. | The head is read from Azure DevOps; the delivery is only a trigger. A late delivery cannot restore results. A head that is not a full commit id is refused. |
| An unreadable result left old green statuses standing. | Six pending statuses are posted. |
| A permanent failure in the hook was answered 503. | Answered 202 with the reason, so the subscription is not retried without end. |
| The push used the stored result alone. | The ticket, repository and baseline are compared with the API's own run (`fix_result_does_not_match_run`), the branch must be `visioniq/fix/...`, and `PICKUP_ALLOWED_WORK_ITEM_IDS` is applied again. |
| A work item id that is not a number gave an unlinked pull request. | Refused (`ticket_id_not_a_work_item`). |
| Ticket and model text were raw Markdown in the description; it cited a commit that is not in the repository. | That text is escaped. The API adds the commit it really pushed and its tree. |
| A 400 was retried eight times; a null file entry was retried without end. | 400, 413 and 422 are permanent. A null entry fails once. |

### Still open

- **Statuses are posted without an iteration.** In Azure DevOps they stay on the pull request
  after a new commit until the "pull request updated" hook is processed. If the subscription is
  missing or a delivery is lost, a green status stays on new content. Posting with `iterationId`
  needs a call that could not be tried against Azure DevOps here.
- **A status has no link to its result** (`targetUrl`), so the DEF-05 criterion "PR checks link
  to the validation results" is not met. The logs are on the runner's disk.
- **Native CI is not read back** (S11-10), so `native_ci` stays `NOT_RUN` in VisionIQ's summary.
- **The hook makes its calls on the request thread**, and `Retry-After` on a 429 is not honoured.
- **A fix run starts after a `PARTIAL` brief too**, and `FixRunStarter` reads the security scope
  from the outbox without an index.
- **A test can still write its own report.** The exit code must agree with it, but a test that
  both forges the report and exits with 0 inside its own process is not detected. The sandbox
  limits what such a test can reach, not what it can claim about itself.
- **Tests found by name only** (`inferred`) still give `PASS` on the regression gate; the detail
  and the limitations say that none is linked to a past fix.
- **Nothing was run against Azure DevOps or a real model.** Every Azure DevOps change above is
  tested against a fake HTTP handler only.
- The two shared fixtures in `contracts/fixes/fixtures/` were written before these changes; they
  are still valid results, but a new run's description differs slightly.
