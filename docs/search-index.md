# Search index (LRN-08)

Keyword (PostgreSQL full-text) and meaning-based (pgvector) search over engineering history, the first
half of the hybrid RAG pattern (master design §10.1, §10.10). LRN-09 merges these candidates with the
graph and re-checks access live.

Run one indexing pass (scheduled job):

    PYTHONPATH=src/VisionIQ.Intelligence python -m app.knowledge.index_cli

| Setting | Default | Meaning |
|---|---|---|
| `EMBEDDING_PROVIDER` | `vertex` | `vertex` (Gemini on Vertex AI) or `test` (offline hashing embedder; never production) |
| `VERTEX_PROJECT` / `VERTEX_LOCATION` | — / `europe-west1` | Google Cloud project and region with Vertex AI enabled |
| `EMBEDDING_MODEL` | `gemini-embedding-001` | D08; stored as `vertex:gemini-embedding-001@768` |
| `EMBEDDING_BATCH_SIZE` | `1` | Texts per Vertex request |
| `INDEX_BATCH_LIMIT` | `500` | Items per run |

Uses the intelligence database login (reads evidence, writes the `knowledge` schema) and Application
Default Credentials for Vertex AI and the raw evidence bucket.

## What is indexed

The latest revision of each work item (title, description, **repro steps, system info**, acceptance
criteria, area, tags, state), pull request (title, description, review comments), commit (message),
Confluence page (sections) and design decision, only when the item is **not deleted** and its access
list is **verified**.

## Guarantees

| Guarantee | How |
|---|---|
| Source, revision and access list on every chunk | Copied from the evidence revision (LRN-08 criterion 1) |
| Pinned model and dimension on every vector | `model_id` + `dimension` per vector; the column is `vector(768)` and the database rejects any other size; vectors of different models are never mixed (criterion 2) |
| No secrets or personal data in the index or sent to Gemini | Text (and search queries) are redacted first (`text-redaction-v1`) |
| Freshness | An item is re-indexed when its content, access list, redaction policy, chunker or model changes; nothing else is touched |
| Deletion propagates | Deleted or no-longer-verified items are removed from the index |
| Isolation | Each item is replaced atomically; a failing item is logged and retried next run without stopping others |
| Scope | Both searches return only chunks whose access list contains one of the caller's scopes |

## Database access

pgvector's types and operators live in the `public` schema. The service logins have `USAGE` there (to
use `vector` and `<=>`) but never `CREATE`; a test enforces this.

## Not yet measured

Retrieval quality (Recall@10, Precision@5) and p95 latency are measured in LRN-09 (S07-13) on a labelled
set; until then no quality or speed claim is made (master design §6.16).
