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

## Never logged

Tokens, PATs, passwords, `Authorization` headers, webhook secrets, connection strings, query strings, request
bodies, evidence content (titles, descriptions, page text, code, raw payloads), exception messages from sources,
and user identities in clear. Tests feed secrets into the graph API, the ingestor (ADO PAT, Confluence
token, webhook secret, work-item titles) and the code scanner (database password, bucket name, a secret planted
in source code, an exception message containing the password) and fail if any appears in the log; a deliberate
leak was used to confirm the tests catch it.
