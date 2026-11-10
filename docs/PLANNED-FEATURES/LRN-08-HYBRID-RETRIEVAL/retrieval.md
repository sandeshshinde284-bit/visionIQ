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
   if the caller can read **every** ticket behind it **and every supporting ticket also passes the required live_check callback**.
7. **Evidence-based reranking**: start from fused rank, add a bounded bonus for matching changed files and explicit ticket-fix links, penalise reverted fixes and stale revisions; record individual ranking reasons. These are heuristic weights, not an AI confidence percentage.
8. **Grounded snippets**: a verbatim window of the redacted chunk with its citation
   (item, revision, section, chunk, URL).
9. **Abstention**: `insufficient_query` when the query has too few specific terms;
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

## LRN-09 hackathon MVP (2026-11-10)

Full graph-neighbor and cross-project dependency traversal is tracked separately in Trello,
as are Microsoft Entra enterprise identity integration and live Confluence delegated
permission checks. The existing code-path lane uses real file/symbol and ticket-fix links
and does **not** assert arbitrary cross-project dependencies. Retrieval caching remains
post-demo.

The local operator-only probe is **read-only**, uses the approved published ADO index, and
does not serve HTTP requests. It is not an authenticated end-user feature. It checks stored
ACL scopes and source currency plus the engine's post-check, **not** live source-provider
user membership. Never expose this CLI behind an unauthenticated web endpoint or treat
its stored-scope callback as enterprise authorization.

From the repository root, with local `.env` credentials and Vertex ADC configured,
run these after updating your checkout to the LRN-09 branch:

```powershell
$env:PYTHONPATH = "src/VisionIQ.Intelligence"
.\.venv\Scripts\python.exe -c "from dotenv import load_dotenv; from app.knowledge.lrn09_demo import main; load_dotenv('.env'); raise SystemExit(main(['--project','Genoa Kiosk','--query','kiosk ticket barcode printing failure after payment retry']))"
.\.venv\Scripts\python.exe -c "from dotenv import load_dotenv; from app.knowledge.lrn09_demo import main; load_dotenv('.env'); raise SystemExit(main(['--project','Genoa Kiosk','--query','it does not work']))"
```

Expected: ranked, sourced results (only if real history matches), explicit weak or
insufficient status when evidence is absent, and no fabricated cause. Source URLs are
emitted as citations; private evidence snippets are not printed.

The labelled benchmark requires **at least three** reviewed real defect queries and
their expected canonical `item_key` strings. Labels cannot be invented by a test;
an independent reviewer must confirm them. Create your own non-sensitive file,
`artifacts/lrn09-cases.json`, in this format:

```json
{
  "cases": [
    {"query": "non-sensitive example issue A", "expected_item_keys": ["actual-item-key-A"], "confirmed_by_human": true},
    {"query": "non-sensitive example issue B", "expected_item_keys": ["actual-item-key-B"], "confirmed_by_human": true},
    {"query": "non-sensitive example issue C", "expected_item_keys": ["actual-item-key-C"], "confirmed_by_human": true}
  ]
}
```

Run:

```powershell
.\.venv\Scripts\python.exe -c "from dotenv import load_dotenv; from app.knowledge.lrn09_benchmark import main; load_dotenv('.env'); raise SystemExit(main(['--project','Genoa Kiosk','--cases','artifacts/lrn09-cases.json','--output','artifacts/lrn09-benchmark.json']))"
```

The report measures real Gemini hybrid Recall@10, Precision@5, MRR, p95 query
latency and scope-leak count. An unlabelled or entirely synthetic corpus cannot
satisfy S07-13. No retrieval-quality threshold is claimed without the results.

### Regression tests (disposable database only)

```powershell
$env:PYTHONPATH = "src/VisionIQ.Intelligence"
.\.venv\Scripts\python.exe -m pytest -q tests/intelligence/test_retrieval.py
.\.venv\Scripts\python.exe -m ruff check src/VisionIQ.Intelligence/app/knowledge/retrieval.py src/VisionIQ.Intelligence/app/knowledge/lrn09_demo.py src/VisionIQ.Intelligence/app/knowledge/lrn09_benchmark.py tests/intelligence/test_retrieval.py tests/intelligence/test_retrieval_db.py
.\.venv\Scripts\python.exe -m ruff format --check src/VisionIQ.Intelligence/app/knowledge/retrieval.py src/VisionIQ.Intelligence/app/knowledge/lrn09_demo.py src/VisionIQ.Intelligence/app/knowledge/lrn09_benchmark.py tests/intelligence/test_retrieval.py tests/intelligence/test_retrieval_db.py
.\.venv\Scripts\python.exe -c "from dotenv import load_dotenv; import runpy,sys; load_dotenv('.env'); sys.argv=['scripts/learn_e2e.py','isolated-pytest','tests/intelligence/test_retrieval_db.py']; runpy.run_path('scripts/learn_e2e.py',run_name='__main__')"
```

The isolated runner creates, migrates, uses and drops its **own** local database.
It requires local-admin DB settings and rejects non-loopback hosts. Do not run
`test_retrieval_db.py` against the published Genoa Kiosk demo database.
