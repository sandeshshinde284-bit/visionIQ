# R4-02: Link retraction — design + implementation

Status: **implemented on the review branch**, revised after a second review found six real gaps in
the first draft (most seriously, a transaction-ordering bug that could have reactivated a correctly
retracted link on an out-of-order replay). The corrected design below is what was actually built and
verified; §11 records the correction history and how each point was verified.

Decision made by the user before implementation: a previously **verified** link that a later,
complete, authoritative source revision no longer asserts is **hidden from current results**
(graph, retrieval, lessons) the same as a disputed or reverted one — never kept visible because of
the prior verification. It stays fully visible in `evidence.ticket_fix_links` itself, with its
review history intact, for investigation and revalidation.

## 0. The problem, restated precisely

`evidence.ticket_fix_links` (migration `20261030_0004`) records that ADO asserted a tie between a
ticket and a PR/commit. Every write goes through `PostgresTicketFixLinkStore.UpsertSourceAsync`,
whose `ON CONFLICT` clause only **adds**: `source_evidence_refs` and `revert_commit_shas` are unioned
with whatever was already stored, and `source_classification` only ever upgrades (never downgrades)
toward `explicit`. There is no code path, anywhere, that removes or marks stale a link once ADO stops
asserting it.

Confirmed while preparing this design: `source_evidence_refs` holds opaque strings
(`"evidence:<item_key>:<revision>"`) and is append-only, so **the current data cannot tell us,
retroactively, which revision last asserted which link, or whether a later revision dropped it.**
That fact drives the migration strategy in §9 — we cannot pretend to know something we don't.

## 1. Schema: a separate provenance table, plus two summary columns on `ticket_fix_links`

**Recommendation: a separate table, not new columns bolted onto `ticket_fix_links` alone.**

### Why a separate table

| | Columns on `ticket_fix_links` only | Separate provenance table (recommended) |
|---|---|---|
| Multiple independent sources for one association (§5) | Awkward — one row, so one "owner" source has to win if two sources ever produce the same key | Natural — one row per (association, source), retraction requires *every* known source to stop asserting it |
| Risk to existing, well-tested UPSERT SQL | New columns sit next to the append-only `source_evidence_refs`/`revert_commit_shas` merge logic; easy to accidentally couple them | None — the existing UPSERT is untouched; this is a new write alongside it |
| Keeps "historical audit" and "current state" concerns separate | Mixed in one row | Separated: `ticket_fix_links` stays the append-only historical record; the new table is explicitly a **current-state** table, allowed to be overwritten |
| Future-proofing | If a second source type starts asserting the same tuple later, requires a redesign | Already handles it |

The cost is one more table and a couple of extra writes per sync — small, and paid only on the write
path (ingestion), never on the read path (see §8: reads stay a single-table check).

### New table

```sql
CREATE TABLE evidence.ticket_fix_link_sources (
    association_key CHAR(64) NOT NULL
        REFERENCES evidence.ticket_fix_links(association_key) ON DELETE RESTRICT,
    -- The ADO entity (work item or PR) whose own relation/commit list asserted this association.
    source_item_key CHAR(64) NOT NULL,
    -- The exact revision of that entity that most recently asserted it. Compared against
    -- evidence.source_items.latest_revision_id to know whether this is still current information.
    source_revision_id TEXT NOT NULL,
    first_asserted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_asserted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    PRIMARY KEY (association_key, source_item_key)
);
CREATE INDEX ticket_fix_link_sources_item_idx
    ON evidence.ticket_fix_link_sources(source_item_key);
```

One row per (association, originating entity). Today exactly one source ever asserts a given
association (a work-item ArtifactLink and a PR's own WorkItemIds/CommitIds list produce *different*
`association_key`s even for "the same real-world fact", because the key includes `commit_sha`, which
only the PR side ever sets — confirmed by reading `TicketFixProjectionSink`). The table is still
designed for N sources per association from day one, since that is cheap to support now and expensive
to retrofit later.

### Two summary columns on `ticket_fix_links` (new migration, additive only)

```sql
ALTER TABLE evidence.ticket_fix_links
    ADD COLUMN retracted_at timestamptz NULL,
    ADD COLUMN retraction_reason text NULL
        CHECK (retraction_reason IS NULL OR retraction_reason IN ('missing_in_source')),
    ADD COLUMN provenance_status text NOT NULL DEFAULT 'unreconciled'
        CHECK (provenance_status IN ('unreconciled', 'reconciled')),
    ADD CONSTRAINT ticket_fix_links_retraction_consistent
        CHECK ((retracted_at IS NULL) = (retraction_reason IS NULL));
```

`retracted_at`/`retraction_reason` are a **materialized summary** of the provenance table (every write
to `ticket_fix_link_sources` that changes an association's retraction status updates these two columns
in the same transaction). This keeps every existing and new read query a single-table check, with no
join, matching how `revert_commit_shas` and `review_status` are already checked today (see §8). The
provenance table is the source of truth; these two columns are a cache of its conclusion.

`provenance_status` is explained in §9 — it is the "unknown/unreconciled" state the user asked for.

## 2. Source-specific provenance and exact revision tracking

Both ingestion points already process **the complete current state** of one entity at a time:

- `TicketFixProjectionSink.ProjectWorkItemAsync(WorkItemRecord item, ...)` — called with the full,
  current set of relations for one work-item revision.
- `TicketFixProjectionSink.ProjectPullRequestAsync(PullRequestRecord pr, ...)` — called with the full,
  current `WorkItemIds`/`CommitIds`/`MergeCommitId` for one PR revision.

For each, the design adds one step: after computing the current set of associations exactly as today,
**also** compute the source's own `item_key` (already available — it's `sourceKey` in both methods)
and its `provider_revision_id` (the work item's `Revision`, or the PR's content revision already
computed as `revision` in `ProjectPullRequestAsync`). Every claim written in this pass is paired with
an upsert into `ticket_fix_link_sources` for `(association_key, source_item_key) = (claim's key, this
entity's item_key)`, setting `source_revision_id` and bumping `last_asserted_at`.

## 3. Current-state comparison and safe retraction rule

After writing the current pass's claims and provenance rows, in the **same transaction**, retract
exactly the rows this same source previously asserted and no longer does:

```sql
-- Rows this entity previously asserted, that are not in the set just (re)asserted this pass.
UPDATE evidence.ticket_fix_links t
SET retracted_at = @observed, retraction_reason = 'missing_in_source'
FROM evidence.ticket_fix_link_sources s
WHERE s.association_key = t.association_key
  AND s.source_item_key = @this_source_item_key
  AND s.source_revision_id <> @this_source_revision_id   -- not re-asserted just now
  AND t.retracted_at IS NULL
  AND NOT EXISTS (                                        -- no *other* source still asserts it
      SELECT 1 FROM evidence.ticket_fix_link_sources other
      WHERE other.association_key = t.association_key
        AND other.source_item_key <> s.source_item_key
        AND other.source_revision_id = (
            SELECT latest_revision_id FROM evidence.source_items
            WHERE item_key = other.source_item_key)       -- that other source is itself current
  );
DELETE FROM evidence.ticket_fix_link_sources
WHERE source_item_key = @this_source_item_key
  AND source_revision_id <> @this_source_revision_id;     -- this source's stale provenance rows
```

**Safety rule (the one that matters most): retraction only ever fires as a side effect of
successfully parsing a complete, current revision of the *same* entity that originally asserted the
link.** It is never inferred from an entity's absence in a sync batch. Concretely:

- A quarantined item (`not_readable`), a dropped id (`errorPolicy=Omit`), or any `AdoSourceException`
  never reaches `ProjectWorkItemAsync`/`ProjectPullRequestAsync` at all (confirmed: these are filtered
  out in `SyncRunner` before `sink.WriteAsync` is called) — so nothing is ever retracted because of a
  transient failure.
- A PR or work item that simply has not changed is never re-fetched by a delta sync (this is the
  existing, separate R1-06 gap) — so nothing is retracted because "we didn't see it today." Retraction
  can only happen on a revision **we did** successfully read.
- Both the batch CLI path and the webhook path (`AdoPullRequestChangeHandler`,
  `AdoWorkItemChangeHandler`) funnel into the same `ICanonicalEvidenceSink.WriteAsync` for the
  `"pull-requests"`/`"work-items"` streams (confirmed by reading `WebhookHost.cs`), so this logic
  covers both automatically, with no separate LRN-05 change required.

## 4. Out-of-order revisions, duplicate replay, concurrent sync, re-linking

- **Duplicate replay** (the same revision processed twice): `source_revision_id` is unchanged, so the
  `<> @this_source_revision_id` guard in §3 matches nothing — no-op, safe.
- **Out-of-order arrival** (revision 5 processed, then revision 4 arrives late from a backfill): before
  writing, compare `@this_source_revision_id`'s own timestamp/ordinal against what
  `ticket_fix_link_sources.source_revision_id` already holds for this `source_item_key`, using the
  *same* tie-break LRN-03 already uses for "is this really newer" (source time, then numeric revision,
  then observed time — `EvidenceQueryService`'s existing latest-revision logic). If the incoming
  revision is **older** than what's already recorded for this source, skip the retraction diff
  entirely (still upsert the claim itself as evidence, as today, but do not treat its omissions as
  authoritative). This mirrors the existing rule that a stale observation never wins.
- **Concurrent sync** (two processes handling the same PR at once — a webhook and a batch run
  overlapping): take the existing advisory lock pattern already used for reverts
  (`LockRevertScopeAsync`), scoped to `(source_instance, source_item_key)`, around the write+diff
  transaction in §3, so the two writers serialize instead of racing.
- **Re-linking** (a retracted link reappears in a later revision): the normal claim upsert in step 2
  naturally re-asserts `ticket_fix_link_sources`; the retraction-summary update additionally clears it:
  `UPDATE ticket_fix_links SET retracted_at = NULL, retraction_reason = NULL WHERE association_key = @key
  AND retracted_at IS NOT NULL` whenever a fresh assertion is written for that key (mirrors
  `ClearTombstoneSql`'s existing restore pattern in `PostgresEvidenceStore`).

## 5. Multiple independent sources for the same association

Handled structurally by the provenance table: the `NOT EXISTS (... other source still current ...)`
clause in §3 means an association is retracted **only when every known source that ever asserted it is
both present and currently silent about it** (or has itself been superseded without reasserting it).
If, in the future, a second source type starts asserting the same `association_key` (for example, a
reviewed/declared link through a different connector), nothing here needs to change — it just becomes
another row in `ticket_fix_link_sources`.

## 6. Incomplete / unavailable / partially synchronized source data

Already covered by the safety rule in §3 (retraction only fires on a *successful, complete* read of
the same entity) — restated here as the explicit list the user asked for:

| Situation | Behavior |
|---|---|
| ADO call fails (timeout, 5xx, auth) | No write reaches the sink; nothing retracted. |
| Item quarantined (`not_readable`) | Same — quarantined items never reach `ProjectWorkItemAsync`. |
| Delta sync simply doesn't touch this PR/item | No write reaches the sink for it; nothing retracted. Still **visible** at its last known state — this is a pre-existing limitation (closed PRs aren't revisited under delta sync, R1-06), not something retraction can or should paper over. |
| `errorPolicy=Omit` silently drops an id from a batch response | That id's outcome is `null`/not-stored (existing quarantine path); not treated as "current state says no relations." |
| A full/reconcile run re-reads everything | Normal case — this *is* how stale links get caught, by re-establishing a fresh, complete, successful read per entity. |

## 7. Preserving historical evidence, human reviews, disputes, reverts

- `ticket_fix_links.source_evidence_refs` and `revert_commit_shas`: untouched by this design — still
  append-only, still the full historical record.
- `ticket_fix_reviews`: untouched, still immutable/append-only.
- **Precedence when more than one signal applies** (this is a judgment call — flagging it explicitly
  rather than deciding silently):
  - `review_status = 'disputed'` → hidden. (unchanged, existing behavior)
  - `revert_commit_shas` non-empty → hidden, **even if** `review_status = 'verified'` (this is the
    existing, intentional rule — a revert is strong code-level evidence and a prior human verification
    does not override it; see the existing `RecordRevertAsync` doc comment). Not changed here.
  - `retracted_at IS NOT NULL` and `review_status IS NULL` (never reviewed) → hidden. New.
  - `retracted_at IS NOT NULL` **and** `review_status = 'verified'` → **recommendation: stay visible**,
    because a human affirmatively reviewed this exact claim, which is a stronger and more deliberate
    signal than an automatic sync noticing the source-side link is gone (which could itself be a
    mistaken unlink, or a field ADO doesn't fully expose). The alternative (always hide once retracted,
    regardless of review) is defensible too — it would need the review to be re-affirmed when source
    data disagrees with it. **This is the one point in this design I'd like your explicit decision on**
    before I implement it; I can build either rule equally easily. Either way, `retracted_at` is always
    recorded and visible in the data for audit, whichever choice is made.

## 8. Changes required downstream

| Area | Change |
|---|---|
| LRN-04 write path (`TicketFixProjectionSink`) | New provenance writes + retraction diff, as in §2–4. `PostgresTicketFixLinkStore.UpsertSourceAsync` additionally clears `retracted_at`/`retraction_reason` on re-assertion (§4). New store methods for the provenance table. |
| LRN-05 (sync/webhooks) | No new wiring — both batch and webhook paths already funnel through the same sink methods (confirmed in §3). `--reconcile-days`/`--full` runs are what makes retraction reliable for rarely-changing PRs; no code change needed there either, since they already call the same `GetPullRequestsAsync`/`GetWorkItemsAsync` → `sink.WriteAsync` path. |
| LRN-07 read path (`PostgresRelationshipGraphStore.OutgoingAsync`) | One more condition alongside the R4-01 fix already shipped: exclude rows where `retracted_at IS NOT NULL` (and, per §7, `AND review_status IS DISTINCT FROM 'verified'` if that's the chosen precedence). Single extra column check, no join — the summary columns in §1 make this cheap. |
| `lesson_store.py` (`QUALIFYING_SQL`) | Same added condition. |
| `retrieval.py` (`CODE_PATH_SQL`) | Same added condition. |
| `TicketFixGraphMaterializer` | No change needed — it still projects every explicit claim into `graph_assertions`; retraction is enforced entirely at read time (§8 above), same pattern as the existing revert/dispute checks, which also aren't enforced at materialization time. |

## 9. Migration strategy for existing rows

Per your instruction, existing rows are **not** retroactively marked retracted — there is no reliable
way to know what they asserted historically (§0). The migration:

1. Adds the columns in §1 to `ticket_fix_links` (`retracted_at`/`retraction_reason` default `NULL`,
   `provenance_status` default `'unreconciled'`) and creates the new `ticket_fix_link_sources` table,
   empty.
2. Every existing row starts `provenance_status = 'unreconciled'`, `retracted_at = NULL` — behaves
   **exactly as it does today** (visible unless already disputed/reverted). No existing link is hidden
   by this migration.
3. The **first** time each association is reprocessed by a real sync after this migration ships, the
   new write path in §2 inserts its `ticket_fix_link_sources` row and flips `provenance_status` to
   `'reconciled'`. From that point on, that association is eligible for retraction detection the next
   time its source changes again.
4. `provenance_status = 'unreconciled'` is exposed in any API/report surface that lists link status (a
   small addition), so it is visible — not hidden — that a given link hasn't yet been through a
   retraction-aware sync. No consumer behavior changes based on it; it's informational, matching "use
   an explicit unknown/unreconciled state" rather than silently asserting freshness that isn't there.

## 10. Tests

**PostgreSQL integration tests** (new, alongside the existing opt-in `GraphDatabaseIntegrationTests`
pattern):

1. A link disappearing from a PR's `WorkItemIds` on its next revision is retracted.
2. A link disappearing from a work item's relations on its next revision is retracted.
3. Re-linking after retraction clears `retracted_at`.
4. **Negative:** a quarantined/failed read of the same PR does not retract anything.
5. **Negative:** a delta sync that never touches the PR again does not retract anything (its rows are
   simply never revisited).
6. **Negative:** an out-of-order (older) revision arriving after a newer one does not retract based on
   the older, stale relation list.
7. Two independent sources (a work-item ArtifactLink and the PR's own list) asserting associations
   that key to different rows (confirmed in §1) each retract independently and don't affect each other.
8. A disputed link stays hidden regardless of retraction state.
9. A reverted link stays hidden regardless of retraction or review state (existing precedent,
   unaffected — regression test that this design doesn't change it).
10. A verified link's behavior under retraction, per whichever precedence is chosen in §7.
11. Concurrent writers for the same `(source_instance, source_item_key)` serialize correctly (advisory
    lock) and don't produce a lost update.
12. Existing rows migrated with `provenance_status = 'unreconciled'` are unaffected in every existing
    passing test (regression: nothing that is visible today becomes invisible from the migration
    alone).

**End-to-end acceptance:** seed a ticket with a PR link, confirm it's visible through the graph API;
simulate ADO removing the link on the PR's next revision; re-run the sync; confirm the graph API no
longer returns that association for the ticket (but the historical row, its evidence refs, and its
revert/review history are all still present in the database for audit).

## 11. Second-pass corrections (applied before/during implementation)

A second review of this design, before coding, found six real problems with the first draft. All six
were fixed; none were dismissed. What changed, and how each was verified:

| # | Problem found | Fix | How verified |
|---|---|---|---|
| 1 | **Most serious.** The first draft upserted every claim (which unconditionally cleared `retracted_at`) *before* checking whether the incoming revision was actually newer. An out-of-order replay of an old revision could reactivate a correctly retracted link. | `UpsertCoreAsync` takes an explicit `clearRetraction` flag, decided *before* any claim is upserted, from the same freshness check that gates the retraction diff. A rejected (stale) revision still upserts its claims as historical evidence, but with `clearRetraction: false`. | Reproduced directly against PostgreSQL 16: the buggy (unconditional-clear) order left `retracted_at` NULL after a stale replay; the corrected order kept it set. Also covered by `A_stale_replayed_pr_revision_never_reactivates_an_already_retracted_link`. |
| 2 | The first draft hard-`DELETE`d a source's provenance row once it stopped asserting an association, losing the record that it ever did. | `ticket_fix_link_source_associations` gained `ended_at` (soft transition, never deleted). A re-assertion of the same (source, association) pair reopens the row via `ON CONFLICT ... DO UPDATE SET ended_at = NULL`, preserving the original `first_asserted_at`. | Verified directly against PostgreSQL: reopening an ended row keeps `first_asserted_at` unchanged and clears `ended_at`. |
| 3 | A lock scoped only to the *reconciling* source didn't protect two *different* sources from racing on the same association. | `ReplaceSourceAssociationsAsync` takes `SELECT ... FOR UPDATE` on the candidate `ticket_fix_links` rows, then re-checks "any other active source" *under that lock*, before retracting. | SQL syntax and plan verified directly against PostgreSQL; the re-check-under-lock pattern serializes concurrent callers by Postgres's own row-lock semantics (not independently load-tested). |
| 4 | "A successful HTTP response" was being treated as "a complete snapshot" without checking whether that's actually guaranteed. | Added an explicit `isCompleteSnapshot` parameter, with the burden on the caller to justify it. For work items: `AdoWorkItemReader.ParseWorkItem` is all-or-nothing, so always true. For pull requests: `WorkItemIds`/`CommitIds` are fetched outside `EnrichAsync`'s gap-tolerant `TryAsync` calls (confirmed by reading the code — only iterations/changed-files/threads/policies are gap-tolerant), so a failure throws rather than returning a partial list; documented as true with one **named, unverified residual risk**: the `/workitems` endpoint has no continuation-token handling in this codebase, so an undocumented ADO-side page cap would not be detected. | Confirmed by reading `AdoRepositorySource.EnrichAsync` line by line. The residual risk is explicitly flagged in code comments, not silently assumed away. |
| 5 | Nothing explicitly addressed a legacy `unreconciled` row whose next real snapshot asserts zero links. | No special case: a zero-claim `ReconcileSourceAsync` call still runs the full retraction diff (there is no early return on an empty current set). | `A_complete_snapshot_with_zero_links_still_retracts_everything_previously_asserted` proves this explicitly. |
| 6 | Unclear whether retraction is evaluated per exact row or per "logical" ticket+PR pair, given a work item's own link and a PR's own link produce different `association_key`s for what a person would call "the same" relationship. | Decided and documented: retraction is evaluated **per exact `association_key`**, independently. A work item's own claim and a PR's own claims for the same PR are separate, independently-sourced facts and are retracted independently. | Directly verified: in the PostgreSQL simulation, retracting the PR-sourced association left the work-item-sourced association (`a3`) untouched. |

Point 7 (detection time vs. source change time) and point 8 (consistent rule across LRN-07/
retrieval/lessons) were already satisfied by the original design (`retracted_at = now()` is always
distinct from `source_revision_time`; all three consumers now carry the identical
`retracted_at IS NULL` condition) and needed no change — confirmed by re-reading each after the fix.

## What was not independently verified

- The new opt-in PostgreSQL integration test (`Reconcile_retracts_a_dropped_link_and_rejects_a_stale_replay`)
  compiles but was not run against a live migrated database in this session (no `.env` credentials
  were read, by design, throughout this review).
- The `/workitems` pagination residual risk in point 4 above is not verified against a live ADO
  organization.
- No load/concurrency test was run against the `FOR UPDATE` locking in point 3; its correctness rests
  on Postgres's documented row-lock semantics, not on an observed race under load.

## Still open, unrelated to this design (per your last message, kept in the register)

- **R2-03** — Confluence identity normalization (re-keying existing item keys; needs its own migration
  plan since it changes existing keys).
- **R2-04** — Database role separation and immutable evidence permissions (the API role currently has
  UPDATE/DELETE on tables described as immutable; needs a role/grant redesign, tracked separately from
  this one).

Both remain required before final production-readiness sign-off, as you noted.
