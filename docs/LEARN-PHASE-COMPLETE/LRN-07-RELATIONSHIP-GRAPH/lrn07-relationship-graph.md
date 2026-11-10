# VisionIQ — LRN-07 Relationship Graph

**Branch:** `feature/lrn07-relationship-graph` (PR #9 into `feature/learn`).  
**Runtime:** .NET 10 / Python 3.12 / PostgreSQL 16 with Alembic `20261031_0006`.  
**Scope:** code integration and CI tests. Real approved source and cloud acceptance are separate.

## Architectural contracts

LRN-07 builds on LRN-03's immutable `evidence.source_items` and
`evidence.source_revisions`. It introduces two PostgreSQL tables:

- `evidence.graph_nodes`: typed identities, SHA-256 keys calculated from the
  canonical source item, type and native ID, and a mandatory source-item FK
- `evidence.graph_assertions`: immutable version-specific relationships,
  source revision FK, proof strength (`explicit/inferred/verified`), assertion
  state (`active/disputed/retracted`), source timestamp and optional link to
  the existing LRN-04 ticket/fix association

Reimporting a previously saved assertion is idempotent. Reusing its identity
with altered content is rejected, not silently overwritten. New source revisions
can supply disputed/retracted assertions without rewriting historical claims.

## Source-integrated edges

| Source | Supported relationship | Requirements |
|---|---|---|
| LRN-04 ADO evidence | Work item → PR | Explicit ticket/PR association and both canonical items archived |
| LRN-04 ADO evidence | PR → commit | Source-observed PR/commit relationship in same organization/project/repo |
| LRN-04 ADO evidence | Work item → commit | Explicit source association without a PR |
| LRN-06 code scan | Commit → committed file | COMPLETE commit-pinned scan with immutable canonical revision |
| LRN-06 code scan | File → symbol | Structural parser declared the symbol; no fallback guess |
| LRN-06 code scan | File → file dependency | Resolved Python import or .NET ProjectReference to committed file |
| LRN-06 approved target | File → repository dependency | Declared Git URL, matched to an explicitly supplied approved target repository's canonical scan |

ADO → Git scan commit bridging is only performed with a configured exact
`--ado-source-instance`, matching native project/repository IDs and full Git
SHA. No fuzzy source names or timestamps create cross-project links. Cross-repo
dependency projection exposes a typed `approved_targets` input and deliberately
does not invent or auto-discover approved repository identities.

The ADO ingestor runs `TicketFixGraphMaterializer` after a successful
canonical sync attempt. Missing endpoints are deferred until the next
reconciliation. The code scanner writes source evidence **before** its graph
nodes/edges. Any graph-write failure is an error, and rerunning the ingestion
is safe.

## Access control — hard deny until source verification

There is **no public graph endpoint**. A future authenticated agent or worker
may invoke the internal `AuthorizedGraphTraversal`, which requires:

1. The starting node's anchor has verified canonical ACL metadata and the
   authenticated requester's current source provider access.
2. Every neighbor node's anchor passes the same current check.
3. Every relationship's proof item passes the current source check **and**
   its exact historical evidence revision's stored ACL admits the requester.
4. Only current, active `explicit/verified` edges appear. Inferred,
   disputed/retracted links, reverted LRN-04 fixes and old code-scan revisions
   are excluded from the default traversal.
5. Traversal uses bounded depth 1–3, a maximum 100 nodes and opaque IDs. No
   source bodies, file content, raw archive links or ACL principal lists are
   returned.

The LRN-01/02/06 ingestion currently records `acl_verified=false` unless an
independently approved provider-to-principal mapping exists. **A populated
database must not be confused with permission to expose graph results.**

## Local test commands (repo root)

```powershell
git checkout feature/learn
docker compose up -d postgres
docker compose run --build --rm db-migrate

dotnet test VisionIQ.sln --configuration Release

$env:PYTHONPATH = "src/VisionIQ.Intelligence"
python -m ruff check .
python -m ruff format --check .
python -m pytest -q
```

The PR's CI runs a fresh-volume migration and rerun, .NET unit tests for
three-hop permission-aware traversal, and real disposable PostgreSQL tests
for source FK integrity, idempotent replay, retractions, LRN-04 dispute
suppression, historical ACLs and structural code graph persistence.

Run a controlled, **approved** canonical ADO and Git scan only after
configuring private database/GCS credentials and source read-only identities.

## Checkpoint A — still requires real-source acceptance

A real approved sample must trace
ticket → PR → commit → file → symbol, then an explicitly declared
cross-repository dependency when available. It must show an inaccessible
Confluence/source item is hidden, a revoked permission suppresses all
descendant graph edges, unknown sources are NOT_SYNCED, repeated ingestion
does not duplicate relationships, and a parser failure preserves last-good
evidence. FND-06 authenticated event delivery remains separately pending.

No cloud permissions, users, owners, priorities or UI implementation are
required to run the repository-local tests. Do not mark LRN-07 or Checkpoint A
Accepted solely because fixture CI is green.
