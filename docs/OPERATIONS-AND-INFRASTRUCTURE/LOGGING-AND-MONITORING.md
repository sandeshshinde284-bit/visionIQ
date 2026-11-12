# Logging

All services write structured JSON logs (one object per line) that Google Cloud Logging parses.

## Levels

| Level | Used for |
|---|---|
| Information | Lifecycle: requests, sync runs and streams, webhook outcomes, graph-query audit |
| Warning | Something degraded: an access check failed (evidence hidden, fail closed), items quarantined |
| Error | A failure that stopped a unit of work |
| Debug | Per-item detail, e.g. why each piece of evidence was hidden. Enable with `LOG_LEVEL=Debug` |

## Correlation

| ID | Where |
|---|---|
| `requestId` | Every API request; taken from a well-formed incoming `X-Request-Id` (8-64 of `[A-Za-z0-9_-]`) or generated, and returned in the `X-Request-Id` response header. Quote it when reporting a problem |
| `user` | Graph API audit: a keyed pseudonym (`u-` + 16 hex, HMAC-SHA256 with `LOG_PSEUDONYM_KEY`). The same person always gets the same pseudonym; only key holders can link it to a person |

## Graph API events

| Event | Level | Content |
|---|---|---|
| `HTTP {Method} {Route} responded {StatusCode} in {ElapsedMs} ms` | Information | Route template only (no query string, headers or body) |
| `Graph API caller denied: {Reason}` | Warning | Reason code and calling app ID; never the token |
| `Graph query {Project}/{WorkItemId} ... by {User}: {Outcome} ({Nodes} nodes, {Edges} edges)` | Information | Audit line; counts only |
| `Evidence {ItemKey} hidden: {Reason}` / `{Provider} {Kind} access denied: {Reason}` | Debug | Why an item was not shown (opaque item key) |
| `{Provider} access check failed ({ErrorType})` | Warning | A source outage hid evidence from everyone; error type only |

## Ingestor events (sync CLI and webhook host)

`LOG_FORMAT=text` gives single-line readable logs for local runs (default: JSON). Every line of a sync carries
`runId` (also written to `report.json` as `runId`); webhook lines carry `eventId`.

| Event | Level | Content |
|---|---|---|
| `{Source} {Method} {Path} responded {StatusCode} in {ElapsedMs} ms (attempt {Attempt})` | Debug | Every ADO/Confluence call; path only |
| `{Source} {Method} {Path} attempt {Attempt} failed ({Cause}); retrying in {DelayMs} ms` | Warning | Timeout, network error or HTTP status |
| `{Source} {Method} {Path} failed after {Attempts} attempt(s): {Cause}` | Error | The call gave up |
| `Sync run {RunId} started / finished` | Information | Sources, mode (delta/full/reconcile), since; duration, all synced |
| `Stream {Stream} {Scope}: {Status}, ... stored, ... not stored, ... quarantined` | Information / Warning | One line per stream; Warning (with reason) when not fully synced; notes follow |
| `Checkpoint {Scope}: loaded / saved / ignored for this run` | Debug | Cursor value |
| `Quarantined {Kind} {NativeId} in {Scope}: {ReasonCode}` / `Quarantine resolved ...` | Warning / Information | G-1 quarantine list |
| `Recycle Bin for {Project}: {Count} deleted work item(s) recorded` | Information | N-2 |
| `Graph projection {Kind}: {Count} assertion(s) processed` / `... failed for {Item}` / `Automated test unresolved` | Information / Warning | Ticket-fix, test-link and build-link projections |
| `Webhook event {EventId} {Kind} {Project}/{WorkItemId} rev {Revision}: {Outcome}, HTTP {StatusCode}` | Information | Every processed event |
| `Webhook event ... ignored` / `Webhook request rejected: {Reason}` | Information / Warning | Never the presented credentials |

## Code scanner events (Python, `ingest_cli`)

Every line carries `scan_id`, `project_id` and `repository_id`. `LOG_LEVEL` and `LOG_FORMAT=text` work as for
the ingestor.

| Event | Level | Content |
|---|---|---|
| `scan.started` | info | |
| `scan.grammar_gap` | warning | File path and the first problem line (N-16); never the code |
| `scan.completed` | info | Status, commit, files, parsed, file-level only, failed, symbols, grammar gaps, duration |
| `scan.partial` | warning | Reasons (path, parser, error type); the last good scan is kept |
| `scan.previous_commit` | debug | For rename detection (M-16) |
| `evidence.stored` | info | State, item key, revision, whether access is verified (N-17) |
| `graph.persisted` | info | Edges and declared dependency links |
| `scan.finished` / `scan.failed` | info / error | Duration / error type only (never the message) |

## Fix pipeline events (DEF-01 to DEF-06)

### Intelligence (Python, `app/defect`)

Every line of a fix run carries `run_id`; under the worker it also carries `trace_id`,
`correlation_id` and `event_id`, as a brief does. `python -m app.defect.cli` reads `LOG_LEVEL` and
`LOG_FORMAT=text` like the other command-line tools. The model calls of the run are the existing
`llm_call` lines (tasks `reproduction_test` and `code_fix`).

| Event | Level | Content |
|---|---|---|
| `fix_claimed` | info | Attempt, repository, time waited in the queue |
| `fix_started` | info | Project, ticket id and revision, repository, baseline SHA, policy revision |
| `fix_not_started` | warning | Why the run could not begin: a reason code such as `repository_not_configured`, `baseline_not_pinned`, `ticket_not_readable` |
| `fix_workspace_ready` | info | Runner kind, image and network, number of baseline files, paths of the files shown to the model |
| `fix_manifest_written` | info | Manifest id and hash, candidate SHA, expected test ids |
| `fix_execution` | debug; warning for `ENVIRONMENT_ERROR` and `TIMEOUT` | One line per runner execution: execution and manifest id, purpose, baseline or candidate, commit, recipe, test path, outcome, exit code, duration, log hash |
| `fix_reproduction_test_declined` | warning | Attempt and reason code (a rejected test, or no model available) |
| `fix_reproduction_attempt` | info when reproduced, else warning | Test path and id, outcome, first outcome, execution ids |
| `fix_not_reproduced` | warning | Reproducibility and reason codes |
| `fix_patch_declined` | warning | Iteration and reason code (no model, budget spent) |
| `fix_patch_checked` | info when inside the policy, else warning | Iteration, policy status, changed paths, changed lines, each finding's rule code, severity and path |
| `fix_candidate_committed` | info | Iteration, candidate SHA, tree SHA, branch, bundle hash |
| `fix_candidate_tested` | info on `PASS`, else warning | Iteration, test id, outcome, first outcome, execution ids |
| `fix_regression_replayed` | info on `PASS` or `NOT_FOUND`, else warning | Status, whether a historical test was found, counts, paths of tests that did not pass |
| `fix_suite_run` | info on `PASS`, else warning | Outcome, first outcome, execution ids |
| `fix_performance_measured` | info; warning for `REGRESSION` and `INCONCLUSIVE` | Status, pairs, metric name, change, noise and threshold in percent |
| `fix_iteration_ended` | info when accepted, else warning | Iteration, result code, candidate SHA |
| `fix_budget_exhausted` | warning | Iterations used |
| `fix_built` | info | State, verdict, reason codes, reproducibility, iterations, executions, overall gate status, whether a draft PR was prepared, duration |
| `fix_workspace_removed` | debug | |
| `fix_answered` / `fix_already_answered` | info | Fix id, state, verdict, reason codes, executions, model calls |
| `fix_deferred` / `fix_store_failed` | warning | Error type, attempt, retry delay |
| `fix_gave_up` / `fix_worker_failed` | error | Error type only |
| `fix_time_budget_exhausted` | warning | The execution that was not started any more |
| `fix_stranded_answered` / `fix_stranded_check_failed` | warning | Deep requests answered `BLOCKED` at start-up with the pipeline off (count), or the error type when that check failed |

The failure text of a test stays in the run's own log file (`X<n>.log`, scrubbed of secret-looking
text) and in the stored result. It is test output, so it is never copied into the service log.

### API (.NET, `Features/Fixes`)

| Event | Level | Content |
|---|---|---|
| `Fix pull requests on (mode {Mode})` | Information | Start-up |
| `Fix run {FixRunId} requested after the brief of run {BriefRunId}` | Information | `FIX_AUTO_START` |
| `No fix run after the brief of run {BriefRunId}: ...` | Debug | Not a picked-up defect, or one already exists |
| `Fix result {ResultId} of run {RunId} ({ResultState}) queued for a draft pull request` | Information | |
| `Delivering the fix result of run {RunId} ({ProjectId} #{TicketId}), attempt {Attempt}` | Debug | |
| `Branch {Branch} pushed to {RepositoryId} from {BaseSha}: commit {HeadSha}, {FileCount} file(s)` | Information | Never the file content |
| `Retry found pull request ... already open` / `Retry found branch ... already pushed` | Information | Why a retry made no second commit or pull request |
| `Pushed commit {HeadSha} has the tested tree {TreeSha}` | Debug | |
| `Pushed commit {HeadSha} does not have the tested tree {TreeSha}; no pull request is opened` | Error | |
| `Draft pull request {PullRequestId} created in {RepositoryId} for work item {WorkItemId}` | Information | |
| `Pull request {PullRequestId} status {CheckName}: {State}` | Debug | One line per posted check |
| `Azure DevOps {Method} {Operation} failed: {Reason}` | Warning | Operation is the last path segment; reason is a fixed code, never ADO's message |
| `Draft pull request {PullRequestId} for run {RunId} ... opened via {Mode} with {CheckCount} checks` | Information | The delivery is complete |
| `No pull request for run {RunId}: the fix result is {Verdict}` | Information | Skipped |
| `Pull request for run {RunId} not opened ({Reason}), attempt {Attempt}; retrying in {DelaySeconds} s` | Warning | |
| `Pull request for run {RunId} could not be opened: {Reason}` | Error | |
| `Pull request {PullRequestId} of run {RunId} has a new head: {Outcome}` | Information | `checks_invalidated` or `checks_restored` |
| `Pull request {PullRequestId} update ignored: {Outcome}` | Debug | Not a VisionIQ pull request, or the head did not change |
| `Pull request update could not be applied ({ErrorType}); ADO will retry` | Error | |
| `Fix pull request worker failed ({ErrorType})` | Error | |

## Never logged

Tokens, PATs, passwords, `Authorization` headers, webhook secrets, connection strings, query strings, request
bodies, evidence content (titles, descriptions, page text, code, raw payloads), exception messages from sources,
and user identities in clear. Tests feed secrets into the graph API, the ingestor (ADO PAT, Confluence
token, webhook secret, work-item titles) and the code scanner (database password, bucket name, a secret planted
in source code, an exception message containing the password) and fail if any appears in the log; a deliberate
leak was used to confirm the tests catch it.

The fix pipeline adds to this list: the source files shown to a model, the generated test, the proposed
patch, the model's cause and summary, test output and failure messages, the pull request description and the
bot's token. Tests on both sides run a fix and fail if the ticket text, the patch, the test, the model's
text, a failure message or the token appears in the log.
