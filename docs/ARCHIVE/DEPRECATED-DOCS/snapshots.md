# Knowledge snapshots (LRN-15)

Briefs and searches read a **published, checked snapshot** of the knowledge base, never an index that
is half-way through an update (master design §6.6 stage 9, §6.11, §6.15, §6.16 KB-5).

    PYTHONPATH=src/VisionIQ.Intelligence python -m app.knowledge.index_cli            # index + publish
    PYTHONPATH=src/VisionIQ.Intelligence python -m app.knowledge.index_cli status     # what is served
    PYTHONPATH=src/VisionIQ.Intelligence python -m app.knowledge.index_cli rollback   # last good

## How it works

- The indexer writes **immutable entries**: one per item revision under one access list, redaction
  policy, chunker and embedding model. Unchanged chunk text reuses its vector.
- A **candidate snapshot** takes, for every item readable now, the entry for its current revision, or
  else its last complete one (served, labelled outdated). It never takes an entry with an out-of-date
  access list, an older redaction policy, another model, or a missing chunk or vector.
- The candidate is **published only if every check passes**, by moving one pointer:

| Check | Fails when |
|---|---|
| Consistency | A member's chunk count is wrong or a chunk lacks a vector for the snapshot's model |
| Access | A member is deleted, unverified or its stored access list differs from the source's |
| Coverage | More than 20% of the previous snapshot's items that are **still readable** went missing (e.g. an unfinished model change). Deleted or re-scoped items always leave at once and never count, so a check can never delay a revocation. `force` overrides for a deliberate change |

- A failing candidate is **rejected** with its reasons; the previous snapshot keeps serving.
- **Rollback** moves the pointer back to the last good snapshot in one step.
- **Self-healing**: an incomplete entry is never served and is rebuilt on the next run.
- **Cleanup** keeps the newest 5 snapshots for rollback and never deletes entries still waiting to be
  published.
- Every answer (`EvidenceBundle`) records `snapshot_id` and `snapshot_age_seconds`; all search lanes of
  one request read the same snapshot. Without a published snapshot nothing is searched.
- Events: `snapshot.published`, `snapshot.rejected`, `snapshot.rolled_back`, and `index.item_removed`
  for items that left the served snapshot.

The scheduled job exits non-zero when items failed or the candidate was rejected, so monitoring sees it.
Lessons and code-path matches are read from current evidence and post-checked per request; they are
not part of the index snapshot.
