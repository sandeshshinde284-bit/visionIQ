# Phase 1 — Learn: integration and acceptance plan

**Integration branch:** `feature/learn` (forked from reviewed `main`).
**Story branches:** `feature/lrn01-...` through `feature/lrn14-...`, each PR targeting
`feature/learn` rather than `main`.
**Merge policy:** No automatic merges; story PR review and green CI required.
No credentials, cloud access or production environment changes in these branches.

The official backlog defines 14 Learn stories. Implement vertically, using
dependency order, not a literal assumption that every numeric story can be
closed in one batch. All readiness and acceptance gates remain in force.

## Checkpoint A — LRN-01 to LRN-07 (trustworthy history and relationships)

| Story | Feature | Needed before sign-off |
| --- | --- | --- |
| LRN-01 | History sources | Multi-project work items/PRs/commits/CI, checkpoint resume, NOT_SYNCED |
| LRN-02 | Confluence decisions | Source version and restricted-page visibility |
| LRN-03 | Canonical evidence | Identity, immutable revisions, ACL, redaction, idempotent upsert |
| LRN-04 | Ticket-to-fix links | Explicit evidence, disputed and reverted links |
| LRN-05 | Backfill/currentness | Full/delta counts, quarantine/retry; needs FND-06 |
| LRN-06 | Code understanding | Repo files/symbols/deps, fallback and durable parser-failure recovery |
| LRN-07 | Relationship graph | Typed versioned edges, scoped ACL traversal |

Build/test ordering: LRN-01 interface + LRN-03 evidence contract first, then
LRN-04/LRN-06; LRN-05 backfill and LRN-07 graph when upstream evidence is
available. LRN-02 can implement independently but must integrate source ACLs.

Checkpoint A test: sample defect -> approved ADO work item -> reviewed PR ->
immutable commit -> changed file and symbol -> allowed graph query; unknown
sources NOT_SYNCED, replay no duplicates; restricted Confluence/code data
never leaks; parser crash remains PARTIAL with last good result. Run tests on
real approved sample data as well as fixtures.

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

Checkpoint B test: retrieval on approved history, ACL enforcement on every
chunk and search result, reproducible matching with provenance, duplicate
PR-event replay, revert/dispute handling, and no unproven lesson promotion.
LRN-08 requires an approved embedding model decision and Vertex access;
LRN-12 requires FND-05 event delivery.

## LRN-14 — After-demo extension

The official backlog labels LRN-14 **after demo**. Do not block the
hackathon's stable Learn slice or its merge to `main` on LRN-14.
Implement/validate LRN-14 in a later reviewed PR once the preceding stories
and after-merge outcome feedback exist.

## Final integration acceptance (before `feature/learn` -> `main`)

1. All required story PRs reviewed; build/test/lint/container checks green.
2. One approved source history chain can be traced end-to-end with
   provider IDs, commit SHAs, revision timestamps and provenance.
3. Duplicate imports and duplicate event deliveries do not duplicate
   evidence, graph edges or lessons.
4. Unknown/unreachable sources are explicitly NOT_SYNCED, not empty.
5. Strict ACL tests across ADO, Confluence, source code and search.
6. Controlled parser failure and persistence recovery with last-good state.
7. Cross-project links require explicit, cited evidence; uncertain links
   are marked inferred or unknown, never proven.
8. Retrieval quality and lesson trust demonstrated on labelled cases.
9. FND-04 deployment and FND-05/FND-06 dependencies tracked separately:
   do not falsely close an upstream story when its end-to-end cloud
   acceptance remains blocked.
10. After integrating `main` into `feature/learn`, run the complete
    local suite and an approved source-backed integration test before PR
    to `main`.

**Integration hygiene:** Merge small reviewed PRs into `feature/learn`;
sync `main` frequently; use required checks; never push unreviewed work
directly to `feature/learn`; freeze feature integration during the final
cross-story test. A single giant PR with 14 unreviewed changes is discouraged.
