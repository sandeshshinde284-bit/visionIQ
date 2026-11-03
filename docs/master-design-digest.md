# Master Design v1.11 — working digest (LEARN phase)

Fast reference for developers and both AIs. **The PDF "VisionIQ Master Engineering & System Design v1.11"
(26 Oct 2026, 79 pages) is authoritative**; this digest only points to it. Section numbers (§) refer to the PDF.
Update this file when the master document changes.

## Technology decisions (§10.1, §10.4, ADR table)

| Technology | Decision | Status | Where it lands |
|---|---|---|---|
| **Hybrid RAG** | **Core pattern.** Retrieve grounded, authorized evidence and return a bounded, immutable **EvidenceBundle**. RAG cannot establish causal truth | Core | LRN-08 (index), LRN-09 (retrieve) |
| **PostgreSQL full-text + pgvector** (Cloud SQL) | Lexical + vector search | **SELECTED** (Vertex Vector Search is only an alternative) | LRN-08 |
| **Gemini on Vertex AI / Claude API** | Model providers; choose by equal-data quality/cost evaluation | Available | LRN-08 embeddings (D08), later phases |
| **OKF** (Open Knowledge Format) | **Optional** Markdown + YAML export for *curated, reviewed* lessons and rules; **not** a database, vector engine or policy engine. MVP: a DB-backed lesson object is sufficient | Optional, curated export only | LRN-10 schema must be OKF-compatible |
| **Google ADK** | **Conditional.** "Explicit Python state machine first; ADK optional, only for proven benefits." Must beat an ordinary orchestrator in a side-by-side test (retry, timeout, restart). No version is specified in the document | Not used in LEARN | BRIEF/DEFECT orchestration, after evaluation + ADR |
| Cloud Run, Pub/Sub, Jobs, Scheduler | Durable queue/workers; webhook acceptance never waits for AI processing | Selected | LRN-05/12 hosting (FND-06) |
| Backend | ASP.NET Core **or** Python/FastAPI | Both allowed | .NET API + Python scanner (as built) |

Key boundary (§10.1): AI may propose; only authorized execution services and Git/CI policies decide test
execution, PR eligibility and approval state. Engineer feedback is evidence of usefulness, not proof of
correctness.

## Storage design to follow (§10.10, conceptual)

- PostgreSQL: `source_object`, `source_revision`, `principal_acl`, `work_item`, `pr_iteration`, `commit`,
  `file_revision`, `symbol_revision`, `graph_node`, `graph_edge`, `evidence_claim`, `candidate_pattern`,
  `canonical_lesson`, `lesson_revision`, `advice_record`, `advice_outcome`,
  **`embedding_chunk(vector, model_id, dimension, snapshot_id)`**, `ingestion_cursor`, `webhook_event`,
  `transactional_outbox`, `job_checkpoint`.
- Cloud Storage: `raw-revisions/…`, `snapshots/{id}/manifest.json`, `code-index/…`, `evidence-runs/…`,
  `okf/lessons/{pattern-id}/lesson.md`.
- Search: PostgreSQL full-text/B-tree + pgvector ANN **where benchmarked**; in-process graph traversal over
  authorization-filtered edges. OKF is never scanned at request time (verified lessons are mirrored into SQL).

## Retrieval algorithm (§10.10, §7.1) — LRN-09

1. Caller-scoped permissions and ticket version.
2. Detect exact signals (stack trace, code symbol, service, trace ID).
3. **Parallel** lexical, semantic/vector, graph-neighbor, code-path and cross-project dependency searches.
4. Merge candidate IDs **without discarding cross-project results** (no hard project/module pre-filter).
5. Rerank: relevance, version compatibility, structural proximity, explicit linkage, fix/revert/test outcome,
   freshness; **penalize** unsupported/contradictory claims rather than hiding them.
6. Fetch authorized evidence spans and curated (OKF) lessons; scope re-checked at hydration/citation time.
7. Validate chronology and evidence support.
8. Assemble an immutable EvidenceBundle with uncertainty, provenance and version manifest.

Weak evidence → partial result, unknowns and discriminating checks; never invented certainty. No arbitrary
cosine cutoffs (e.g. 0.85) as accuracy guarantees; no whole commit histories sent to a model.
**Measure** (§10.4, §6.16): Recall@10, Precision@5, cross-project recall, p95 latency, **ACL leakage rate**,
abstention behaviour; labelled set incl. same-symptom/different-root-cause negatives. Do not claim
"95% confidence" or "<1 s at 10k tickets" until measured.

## Lessons (§6.10, §8.9, §6.20) — LRN-10, LRN-13

- Knowledge unit ≠ ticket: many tickets can support one lesson; one ticket can support several hypotheses.
  Never collapse contradictory fixes because titles look alike.
- Consolidation: change event → canonical revision → candidate patterns (hybrid retrieval) → compare
  mechanism, symbols, release context, evidence → **LINK_EXISTING / REVISE_EXISTING / FORK_BY_SCOPE /
  CREATE_NEW / HUMAN_REVIEW**, with persisted rationale. Pattern revisions append-only.
- Service contract (§8.9): `ingest_observation(source_event_id, artifact_revision_id)` (store once),
  `resolve_pattern(observation_id)`, `publish_lesson_revision(pattern_id, approved_evidence_ids)` (new version
  only on material evidence change or owner-approved correction).
- Refresh: material-change hashes; recompute only affected chunks/embeddings/edges; deletion/revocation
  propagates to **all** derived indexes; past briefs keep their as-of snapshot.
- OKF-compatible fields: type, title, resource URN, tags, `status` (candidate / current / stale),
  **generated vs verified kept separate**, sources, owner, review date; store `lesson_id ↔ OKF path/revision`.
- **Never** set verified because an LLM generated it, a PR merged, or an engineer clicked Accept.
- recurrence / last_seen are not confidence.

## PR events (§8.12) — LRN-12, LRN-13

Every meaningful PR event updates evidence; **no PR event establishes a verified root cause or creates a
canonical lesson**. Two tiers: (A) fast, idempotent, append-only evidence sync; (B) asynchronous, proof-gated
knowledge reconciliation.

| Event | Immediate evidence (§8.12) | Built in LRN-12? |
|---|---|---|
| created | PR ID, source/target, **source SHA**, work-item links, **initial changed files**, patch intent | Yes: IDs, branches, source/target SHA, work items, commits, changed files |
| updated / push | **New PR_ITERATION, source SHA, file diff**, comments/votes, obsolete prior diff pointers | Yes: every iteration (source/target commit), cumulative changed files, review threads, votes. Per-iteration diffs: not stored |
| abandoned / rejected | **Review rationale, abandoned patch fingerprint** | Yes: review threads (no system messages) and a file-level change-set fingerprint |
| completed / merged | Merge commit, **final patch fingerprint, required human approval and branch-policy evidence** | Yes: merge commit, fingerprint, required reviewer votes, branch-policy evaluations |
| build / test result | Runner manifest, test IDs, pass/fail/inconclusive | Build links (LRN-07) |
| deployment, revert, reopen | Release SHA, environment, rollout; canonical links | LRN-14 (after demo) |

LRN-12 follows §8.12 for the PR itself. Notes: the patch fingerprint is file-level (paths + change types), not
a content diff; parts ADO will not return (e.g. no permission) are listed in `enrichmentGaps` and left null
(unavailable, never "none"); review text goes only to the restricted raw archive (indexed metadata is the
allowlist: status, isDraft, lifecycle).

## Post-merge observation (§3.6, §8.8) — LRN-14

States: APPROVED_AWAITING_MERGE, MERGED_NOT_DEPLOYED, DEPLOYED_OBSERVING; outcomes VERIFIED_EFFECTIVE,
REGRESSED_OR_REVERTED, DISPUTED, UNKNOWN_OR_UNOBSERVED, OBSERVING. Never correlate by timestamps alone; a
quiet window is UNKNOWN, not success; never auto-deploy, auto-revert or auto-merge.

## Knowledge-base exit gate (§6.16)

KB-0 source mapping · KB-1 replayable import · KB-2 typed relationships · **KB-3 hybrid search** (labelled
relevant and hard-negative matches; metrics and latency) · **KB-4 lesson lifecycle** (one pattern, many
observations; a revert disputes/supersedes; no duplicate lessons) · **KB-5 safety and recovery** (revocation,
malicious ticket text, connector outage, forced reindex rollback; no cross-scope leakage).
