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

## Never logged

Tokens, PATs, passwords, `Authorization` headers, webhook secrets, connection strings, query strings, request
bodies, evidence content (titles, descriptions, page text, code, raw payloads), exception messages from sources,
and user identities in clear. Tests feed secrets into each component and fail if any appears in the log.
