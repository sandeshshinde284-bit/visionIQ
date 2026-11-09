# Phase 1 — Learn: integration and acceptance plan

**Status (9 Nov 2026):** LRN-01–07 implementation is present, but some real-source,
database-gated and cloud acceptance checks remain open. Begin LRN-08–13
without treating all LRN-01–07 capabilities as fully accepted.

**Historical branch plan:** `feature/learn` with individual feature PRs
targeting that branch. Several features have since been merged into `main`;
use the actual Git history and review the next PR before merging. Do not
make an untested release or cloud deployment solely because code exists.

The backlog contains LRN-01–14. LRN-14 is explicitly **after demo**. This
plan records the agreed execution/acceptance sequence, not a claim that
every listed validation has passed.

## Agreed execution sequence — move to LRN-08–13 now

| Order | Wave / action | Exit evidence |
| --- | --- | --- |
| 0 | **Review the incoming configuration cleanup** once pushed. Ensure that removing dormant `VISIONIQ_AUTH_*` configuration does not break Graph/LEARN services or tests. | Diff reviewed; relevant build/tests pass; deferred API work remains separately tracked. |
| 1 | **Checkpoint A: LRN-01–07** — use the existing code and recorded tests, then close only missing local canonical / real-ADO / Confluence / commit-pinned scan / graph proof. Do **not** redo completed engineering work. | Evidence-backed ticket → PR → commit → file → symbol/test path; rename/revert/replay tests; actual PASS/FAIL/SKIP/BLOCKED report. |
| 2 | **Checkpoint B: LRN-08–13** — implement/verify the index, Vertex embeddings, retrieval, evidence-backed lessons, readiness, PR events and proof-driven updates. | Each story tested in **local canonical** PostgreSQL + approved GCS, with genuine embeddings when validating semantic quality. |
| 3 | **Combined regression: LRN-01–13** — run the one LEARN E2E entry point for the full scope. | Full pipeline uses the same canonical evidence; no invented links, duplicate data or unsupported lesson claims; unresolved checks called out. |
| 4 | **Manual Google Cloud deployment and validation** after local readiness. | Approved Artifact Registry images and Cloud Run services/jobs, Cloud SQL/GCS access, in-network health and functional checks; no Cloud Build dependency. |
| 5 | **Final LEARN acceptance and merge/closure gate** — reconcile CI, local, real-source and cloud evidence. | Only verified behavior is marked accepted; outstanding and deferred items remain open with linked follow-ups. |
| Later | **LRN-14 after demo.** | Separate scope/PR; never blocks the hackathon Learn slice. |

**Single local runner:** `./scripts/learn-e2e.ps1` (PowerShell:
`.\\scripts\\learn-e2e.ps1`). The interactive menu offers
LRN-01–07, LRN-08–13, LRN-01–13, LRN-14 and an individual story.
Use JSONL only for ingestion-only checks; **LRN-08–13 needs canonical
PostgreSQL and GCS**. Record the actual run log and any skipped/blocked
items; a green unit suite is not proof of live E2E.

**Hackathon scope:** Microsoft Entra IDs and per-user graph authorization
are deferred; the public Graph HTTP API stays disabled. Keep graph creation,
typed provenance, local/internal inspection and basic source/project
boundaries. Never expose an unauthenticated endpoint or claim that GCP
workload IAM grants per-user ADO/Confluence permissions. Authentication
follow-ups: [internal Cloud IAM Graph API](https://trello.com/c/LctSCimm/58-deferred-integration-lrn-07-authenticated-internal-graph-api-via-google-cloud-iam)
and [enterprise Entra ID](https://trello.com/c/NOwMYjsM/56-deferred-auth-visioniq-enterprise-user-authentication-microsoft-entra-id-api-tokens).
Confluence per-user permission functionality is also a
[tracked deferred rebuild](https://trello.com/c/Ws9wyOpP/55-learn-follow-up-lrn-02-confluence-access-control-adr-live-auth-restricted-page-acl-removed-from-code-needs-rebuild).
Do not include restricted provider data in a demo/test corpus unless its
handling is approved and safe.

## Checkpoint A — LRN-01 to LRN-07 (trustworthy history and relationships)

| Story | Feature | Needed before sign-off |
| --- | --- | --- |
| LRN-01 | History sources | Multi-project work items/PRs/commits/CI, checkpoint resume, NOT_SYNCED |
| LRN-02 | Confluence page ingestion | Versions and approved source scope; live ACL and ADR-specific discovery deferred |
| LRN-03 | Canonical evidence | Identity, immutable revisions, ACL, redaction, idempotent upsert |
| LRN-04 | Ticket-to-fix links | Explicit evidence, disputed and reverted links |
| LRN-05 | Backfill/currentness | Full/delta counts, quarantine/retry; needs FND-06 |
| LRN-06 | Code understanding | Repo files/symbols/deps, fallback and durable parser-failure recovery |
| LRN-07 | Relationship graph | Typed, source-backed versioned edges; trusted local traversal and real-data proof; public Graph API deferred |

Build/test ordering: LRN-01 interface + LRN-03 evidence contract first, then
LRN-04/LRN-06; LRN-05 backfill and LRN-07 graph when upstream evidence is
available. LRN-02 can ingest approved pages independently; its removed live per-user\nACL checks must not be described as implemented.

Checkpoint A test: approved ADO defect -> explicitly linked PR -> exact commit
-> changed file and symbol/test -> trusted local canonical graph query; unknown
sources NOT_SYNCED, replay no duplicates; revoked/reverted assertions not
traversable; parser crash remains PARTIAL with last good result. A declared
PR does **not** prove a reviewed/merged fix. Run tests on real approved sample
data as well as fixtures. Do not claim live per-user graph/Confluence ACL
checks are passing while their implementation is deferred.

Do not say Checkpoint A is *fully accepted* when the FND-04 or FND-06 cloud
acceptance dependencies remain open. Use **code-integrated / cloud-pending**
when appropriate.

## Checkpoint B — LRN-08 to LRN-13 (retrieval and continuing learning)

| Story | Capability |
| --- | --- |
| LRN-08 | Index history for keyword and semantic search |
| LRN-09 | Retrieve the right history for a new ticket |
| LRN-10 | Turn recurring failures into evidence-backed lessons |
| LRN-11 | Demonstrate knowledge trust/qualification |
| LRN-12 | Update knowledge from PR events |
| LRN-13 | Change lessons only when supported by proof |

Checkpoint B test: source/project-scoped retrieval on approved history,
reproducible matches with provenance, duplicate PR-event replay,
revert/dispute handling, and no unproven lesson promotion. Live end-user
authorization is a deferred acceptance gap, **not** an implicit PASS.
LRN-08 needs configured Vertex embeddings and approved GCS-backed canonical
evidence; LRN-12's live event delivery depends on its running webhook/event
infrastructure, which must be tested separately from synthetic event tests.

## LRN-14 — After-demo extension

The official backlog labels LRN-14 **after demo**. Do not block the
hackathon's stable Learn slice or its merge to `main` on LRN-14.
Implement/validate LRN-14 in a later reviewed PR once the preceding stories
and after-merge outcome feedback exist.

## Final integration acceptance (before declaring LEARN complete)

1. All required story PRs reviewed; build/test/lint/container checks green.
2. One approved source history chain can be traced end-to-end with
   provider IDs, commit SHAs, revision timestamps and provenance.
3. Duplicate imports and duplicate event deliveries do not duplicate
   evidence, graph edges or lessons.
4. Unknown/unreachable sources are explicitly NOT_SYNCED, not empty.
5. Test configured source/project boundaries, and document any missing\n   end-user ACL checks as **deferred** with the linked Trello follow-ups;\n   never claim deferred user authorization is implemented.
6. Controlled parser failure and persistence recovery with last-good state.
7. Cross-project links require explicit, cited evidence; uncertain links
   are marked inferred or unknown, never proven.
8. Retrieval quality and lesson trust demonstrated on labelled cases.
9. FND-04 deployment and FND-05/FND-06 dependencies tracked separately:
   do not falsely close an upstream story when its end-to-end cloud
   acceptance remains blocked.
10. On the actual integration branch, run the complete local suite and\n    an approved source-backed integration test before merging or sign-off.\n    If already on `main`, do not mistake a merged commit for acceptance.

**Integration hygiene:** Prefer small reviewed PRs, sync with `main`,\nrun relevant CI, and freeze the integration baseline during the final\ncross-story test. A single giant PR with all stories unreviewed is discouraged.
