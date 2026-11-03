# Retrieval (LRN-09)

`app.knowledge.retrieval.retrieve(connect, embedder, query, scopes, ...)` finds the history most like a
defect and returns an **EvidenceBundle** (master design §7.1, §10.10).

Every request reads one **published snapshot** for all lanes and records its ID and age in the
EvidenceBundle; see [snapshots.md](snapshots.md) (LRN-15).

## Algorithm

1. **Redact** the query (people paste logs; the query is sent to the embedding model).
2. **Exact signals**: file names (relative, absolute and Windows paths from stack traces), stack-frame
   methods, code symbols, error constants, informative terms.
3. **Four lanes in parallel**, each pre-filtered by the caller's access scopes:
   keyword (PostgreSQL full-text, any-term with density ranking), vector (pgvector, same model only),
   **code path** (past tickets whose fixes *changed* the named files, or files declaring the named
   symbols via graph `file_symbol` edges) and **lessons** (LRN-10 lessons covering those files).
4. **Merge without discarding** using reciprocal rank fusion (k = 60; code path weighted 1.5). No
   similarity cutoffs, no project/module pre-filter.
5. **Counterevidence is shown, not hidden**: a reverted fix, or a source that changed since indexing.
6. **Access re-checked after retrieval** against the current evidence (not deleted, access list still
   verified and still matching), plus an optional `live_check(item_key)` hook. A lesson is returned only
   if the caller can read **every** ticket behind it.
7. **Grounded snippets**: a verbatim window of the redacted chunk with its citation
   (item, revision, section, chunk, URL).
8. **Abstention**: `insufficient_query` when the query has too few specific terms;
   `weak_evidence` when no result is supported by two independent lanes or a code-path match. Both
   come with reasons and what to add, never a guess.

## Access: important boundary

The retriever enforces scopes before and after retrieval using the stored evidence. The **live**
provider checks (current ADO project membership, Confluence's own page permission check) live in the
.NET API (B-5/B-6, LRN-02). User-facing delivery must pass results through that API (as the graph API
does) or supply `live_check`; otherwise access changes in the source become effective only after the
next sync.

## Benchmark (S07-13)

`tests/intelligence/test_retrieval_db.py::test_benchmark_...` runs labelled queries over a demo
history (including a same-symptom, different-root-cause negative and another team's history) and writes
`retrieval-benchmark.json` (`VISIONIQ_BENCHMARK_DIR`). On the demo fixture with the offline hashing
embedder: Recall@10 = 1.0, MRR ≥ 0.75 (asserted), access-leak rate = 0 (asserted). These numbers
validate the pipeline, not the product's quality: re-run with Gemini embeddings on a labelled sample of
real history before making any quality or latency claim (master design §6.16).

## Not done yet

S07-12 (retrieval cache and invalidation) is scheduled after the demo, as the card says.
