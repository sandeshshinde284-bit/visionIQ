# Blocking review fixes (B-1 to B-17)

Fixes for the 17 blocking findings from the LRN-01..07 review. Each fix has
tests that fail without it (except where noted in the commit message).

## Decisions taken (change these deliberately, not by accident)

| Topic | Decision | Where |
|---|---|---|
| ADO evidence access (B-5/B-6) | **Project level.** A record is readable by people who are currently members of any team in its ADO project. Area-path and repository-level permissions are **not** modelled yet. | `AdoProjectAccess.cs`, `AdoProjectMembership.cs` |
| Requester identity | A trusted server-side caller (e.g. the Impact Agent integration) must supply the person's ADO identity id or unique name (email) as `EvidenceRequester.PrincipalId`, and the project scopes (`ado-project:{org}/{project}`) as group principals. Membership is re-checked live, so claiming a scope grants nothing on its own. | `EvidenceQueryService` |
| Confluence evidence | **Hidden** until a Confluence read path exists: ancestor-level AND restrictions cannot be flattened into principals, and space permission must be verified (B-7, `IConfluenceSpaceAccess`, deny by default). | `CanonicalEvidenceSink`, `ConfluenceCurrentAccessVerifier` |
| Scanner database login (B-15) | The Python scanner uses `DB_INTELLIGENCE_*`. Row-level security limits its writes to git `code_scan` evidence and the graph rows it proves. | `db/manage.py` |
| Content revisions (B-9) | PR, commit, build, link and Confluence page/decision evidence use `native#content-hash` revisions. The **first sync after this change creates one new revision per existing item** (expected, once). | `CanonicalEvidenceSink.ContentRevision` |
| Backfill counts (B-17) | An adapter that reports `expected_count` must also set `count_through_cursor` to the last returned record's cursor. | `app/backfill/reconcile.py` |

## Configuration

- API: set `ADO_ORG_URL`, `ADO_PROJECTS`, `ADO_READ_PAT` to enable ADO evidence access; without them the API stays deny-all.
- Scanner CLI: needs `DB_INTELLIGENCE_USER` / `DB_INTELLIGENCE_PASSWORD` (no longer `DB_API_*`). Re-run `python -m db.manage all` (or the cloud migration job) once so the new grants and row-level security policies exist.
- `tree-sitter` is pinned to `0.25.2` (0.26.0 crashes on real C# files).

## Still open (not blocking)

Medium/minor findings M-1..M-16 and m-1..m-12 from the review, and process items P-2..P-8.
CI (`.github/workflows/pr-validation.yml`) runs every test; its fresh-PostgreSQL job runs the new
B-8/B-9/B-15 database tests and the FND-03 role-isolation test with `VISIONIQ_DB_INTEGRATION=1`.
Outside CI, the **.NET DB integration tests still report "passed" when no database is configured**
(M-10).
