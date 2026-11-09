# LRN-08 — Index history for search

**Story:** [LRN-08 Trello](https://trello.com/c/eKiuV1Pl/14-learn-lrn-08-index-history-for-search)  
**Scope:** Searchable approved canonical history; no standalone UI  
**Status:** Implemented index and search components; **actual live Vertex/approved-source acceptance pending**.

## 1. What it is

LRN-08 is VisionIQ's history indexing layer. It makes approved, versioned Azure DevOps and Confluence evidence searchable by **words** (PostgreSQL full-text) and by **meaning** (Gemini embeddings on Vertex AI stored in pgvector). It is *not* the final Engineering Brief or an AI answer generator.

## 2. What it does

- Reads latest supported canonical source revisions in PostgreSQL and archived raw evidence from private GCS.
- Redacts sensitive text, extracts sections, splits them into repeatable chunks.
- Writes chunk text, item identity, source revision, provenance and source access-scope metadata.
- Computes Vertex AI `gemini-embedding-001` vectors at **768 dimensions**, pins model/dimension and reuses unchanged embeddings.
- Maintains full-text and pgvector indexes; publishes a checked snapshot to keep incomplete/index-in-progress rows out of search.
- Skips deleted/unverified-scope items, and supports incremental re-indexing.

**Boundary:** Indexed access-scope metadata is not a replacement for live end-user permission checks. That integration is deferred. Do not use an unapproved sensitive corpus or expose this data in a public API.

## 3. Pending / not tested

- **Live approved-history demonstration:** one real work item should be returned by both keyword and Vertex-vector search.
- Vertex AI model access, latency and cost must be measured from a configured developer account. Offline hashing is a functional fixture only, not a semantic acceptance result.
- Real source scope, indexing throughput, reindex/replay, stale snapshot and missing-GCS/error cases need end-to-end evidence.
- Scheduled Cloud Run indexing jobs and operational alerting are separate cloud deployment tasks.
- LRN-09 handles candidate union, graph ranking, citations and abstention; **do not claim** LRN-08 delivers the full Engineering Brief.

## 4. How it fits into VisionIQ

```text
LRN-01/02 source history
       |
LRN-03 canonical revisions + private GCS
       |
LRN-05 keeps records current
       |
LRN-08 redacted chunking + keyword index + Vertex vectors
       |
LRN-09 keyword/vector/graph retrieval
       |
Evidence-backed Engineering Brief and lessons
```

## 5. Testing

**Configuration in private `.env`:**

- Canonical local PostgreSQL: `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_INTELLIGENCE_USER` and `DB_INTELLIGENCE_PASSWORD`.
- Private raw archive bucket: `GCS_EVIDENCE_BUCKET` and approved Application Default Credentials.
- Vertex: `EMBEDDING_PROVIDER=vertex`, `GCP_PROJECT_ID` or `VERTEX_PROJECT`, `VERTEX_LOCATION` (model-supported location), `EMBEDDING_MODEL=gemini-embedding-001`, `EMBEDDING_DIMENSION=768`.
- The LEARN runner needs `INGESTION_OUTPUT_MODE=canonical` and an explicit confirmation before knowledge writes.

Run from the repository root:

```powershell
# Validate approved ADC, configured model, 768-vector dimension and simple semantics.
$env:PYTHONPATH = 'src/VisionIQ.Intelligence'
python -m app.knowledge.vertex_check

# The single interactive canonical E2E runner:
.\scripts\learn-e2e.ps1
```

Choose **individual LRN-08** or **LRN-08–13** with an approved ADO item and a canonical test DB/GCS. The runner executes the real index CLI, checks a published current revision with nonzero indexed chunks/vectors and now also runs `probe-index` to check **both** search lanes return the selected item. The probe derives its query from an **already redacted indexed chunk** and does not write query/source text to the evidence report. A wrong-scope negative check must return no selected item.

The search probe uses the configured embedding provider. A hashing-provider PASS is **not** a real Vertex acceptance PASS; the runner reports that distinction. Because its query is copied from the indexed item, this is an **index-path sanity check**, not a labelled Recall@10/Precision@5 quality benchmark.

**Direct read-only probe for troubleshooting** (requires an already-published snapshot):

```powershell
$env:PYTHONPATH = 'src/VisionIQ.Intelligence'
python scripts/learn_e2e_stages.py probe-index --item-key '<canonical-item-key>' --output '<gitignored-test-result>.json'
```

The probe reports boolean keyword/vector/scope checks, hit counts and the model identifier. It does not print or persist raw source material. Never pass secrets as CLI arguments.

**Acceptance:** keep LRN-08 **To be Tested** until real Vertex + canonical PostgreSQL/GCS and an approved source-grounded query pass, with logs and an explicit negative scope test.

## 6. Move to LRN-09

LRN-09 consumes the **published** keyword and vector candidates, adds graph candidates, reranking, evidence citations and a weak-evidence/abstention policy. Its live end-user authorization remains deferred and must not be marked passed by the LRN-08 internal probe.

**Related docs:** [Index design](../../search-index.md) · [Vertex AI setup](../../vertex-ai-setup.md) · [Overall LEARN plan](../../phase1-learn-integration-plan.md).
