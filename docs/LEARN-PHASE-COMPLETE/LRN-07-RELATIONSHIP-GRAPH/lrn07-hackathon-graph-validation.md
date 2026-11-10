# LRN-07 — Hackathon graph: trusted local inspection and acceptance

**Scope:** Make an accurate, source-evidence-backed graph. Enterprise Entra
authentication and live per-user ACL authorization are deferred; the public
graph HTTP route is **disabled**. Never expose the internal traversal through
an anonymous endpoint or broaden permissions for demo convenience.

## What builds the graph

- ADO ingestion projects source-declared **work item → PR → commit** edges and
  declared/uniquely mapped test associations through canonical PostgreSQL.
- LRN-06 commit-pinned scans project **commit → file → symbol/test** and
  explicitly declared file/repository dependency edges.
- Changed-file evidence can also project **PR/commit → changed file** without
  pretending an open PR is an accepted fix.
- Every node is anchored to a canonical source item, and every edge has a
  source item + exact revision. Reimporting the same revision is idempotent.
- Latest retractions, disputes, reverts, superseding scans and deleted sources
  must not be reported as currently usable graph paths. Immutable history
  remains in PostgreSQL.
- A declared test association is **not** a successful test run or evidence of
  actual test coverage.

## Recommended real validation: existing interactive runner

From a repository checkout on a computer with the approved ADO test data,
configured local PostgreSQL and GCS evidence:

```powershell
.\scripts\learn-e2e.ps1
```

Choose **individual story → LRN-07** (or LRN-01–07 for full ingestion). Use
canonical evidence mode and an existing approved ADO Task/Bug, PR and Git
commit. If the scanner evidence is missing, run LRN-06 for that source before
retrying standalone LRN-07.

The runner checks the real canonical paths, using the latest active
source-provenanced assertions and the exact selected fixture PR/commit:

1. Ticket → PR → changed file → symbol/test (shortcut).
2. Ticket → PR → commit → file → symbol/test (full chain).
3. File rename history and persisted graph assertions.

The runner must report **FAIL**, not PASS, when a required path is missing.
It no longer pretends deferred enterprise-token verification is a necessary
LRN-07 MVP acceptance condition.

## Inspect the canonical graph without an HTTP endpoint

Only on the authorized **local machine**, first set these standard runtime
environment variables with your approved local development configuration:

- `DB_TARGET=local` and `DB_HOST=localhost` (or `127.0.0.1`)
- `DB_NAME`, `DB_API_USER`, `DB_API_PASSWORD` (from your local secret store)
- `DB_PORT` if not the default PostgreSQL port.

Example PowerShell invocation (values below are entered at runtime; no
provider-specific values are embedded in application code):

```powershell
python .\scripts\inspect_graph.py `
  --source-instance $env:ADO_ORG_URL `
  --project $env:ADO_PROJECT `
  --work-item-id $env:ADO_TASK_ID `
  --depth 4
```

To include an explicitly approved second project, append
`--allow-project <other-approved-project-id>`. Do not supply passwords in
command arguments. The command refuses remote/cloud PostgreSQL targets,
uses a read-only database transaction and prints only structural metadata,
relation types and provenance keys, **never raw code or source bodies**.

**Not an authorization control:** This CLI trusts the approved operator's
local database permissions. Treat output as engineering evidence, not
permission-filtered user content. It is not exposed over HTTP.

## Acceptance gate (not yet automatically satisfied)

- Full .NET build and LRN-07 test suite pass; no dangling ACL test symbols.
- Canonical PostgreSQL stores correct ticket/PR/commit/file/test links.
- At least one full real path and a known-negative/missing-link example.
- Rename, revert, retraction, tombstone and replay scenarios verified.
- Actual cross-repository dependency accepted only from approved, declared
  evidence, with ambiguous/unapproved targets left unresolved.
- No hardcoded organizations, repositories, projects, source URLs, or IDs.
- Graph access remains internal until the deferred authorization work is built.

Focused CI and fixture-based unit tests are useful **but not substitutes** for
the real-source and persisted-database checks above.
