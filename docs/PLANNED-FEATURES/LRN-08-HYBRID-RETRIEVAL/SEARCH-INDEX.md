# Search index (LRN-08)

Keyword (PostgreSQL full-text) and meaning-based (pgvector) search over engineering history, the first
half of the hybrid RAG pattern (master design §10.1, §10.10). LRN-09 merges these candidates with the
graph and re-checks access live.

Run one indexing pass (scheduled job):

    PYTHONPATH=src/VisionIQ.Intelligence python -m app.knowledge.index_cli

| Setting | Default | Meaning |
|---|---|---|
| `EMBEDDING_PROVIDER` | `vertex` | `vertex` (Gemini on Vertex AI) or `test` (offline hashing embedder; never production) |
| `GCP_PROJECT_ID` (or `GCP_PROJECT_ID`) / `VERTEX_LOCATION` | — / `europe-west1` | Google Cloud project and region with Vertex AI enabled |
| `EMBEDDING_MODEL` | `gemini-embedding-001` | D08; stored as `vertex:gemini-embedding-001@768` |
| `EMBEDDING_BATCH_SIZE` | `1` | Texts per Vertex request |
| `INDEX_BATCH_LIMIT` | `500` | Items per run |

Uses the intelligence database login (reads evidence, writes the `knowledge` schema) and Application
Default Credentials for Vertex AI and the raw evidence bucket.

When you connect Vertex AI, follow [vertex-ai-setup.md](vertex-ai-setup.md) and run
`python -m app.knowledge.vertex_check` to confirm everything is ready.

Searches read only the **published snapshot**; see [snapshots.md](snapshots.md) (LRN-15).

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

## Targeted re-index (LRN-13)

`indexer.run(..., only=[item_key])` re-indexes just the items a PR event named. Within a changed item,
chunks whose text is unchanged keep their existing vector; only touched chunks are sent to the
embedding model (`report.embedded` / `report.reused`). Code files are re-parsed only when their content
changed (the scanner's blob-hash cache).

## Database access

pgvector's types and operators live in the `public` schema. The service logins have `USAGE` there (to
use `vector` and `<=>`) but never `CREATE`; a test enforces this.

## Not yet measured

Retrieval quality (Recall@10, Precision@5) and p95 latency are measured in LRN-09 (S07-13) on a labelled
set; until then no quality or speed claim is made (master design §6.16).

## Hackathon LRN-08 acceptance (November 10, 2026)

The indexed Genoa Kiosk ADO demo corpus has **135 items** (125 work items, 8 commits,
2 pull requests) with GCS-backed evidence. A separate 14 Payments records have
`mem://` archive references and must not be treated as production evidence.
The 147 ERK Confluence pages are deliberately **not** enabled for the hackathon
search until live page authorization is restored (deferred LRN-02 follow-up).
Never manufacture GCS paths, disable authorization, or present this as 100% corpus coverage.

From the repository root on Windows, with the usual local `.env` and Application
Default Credentials for Vertex AI already configured:

```powershell
$env:PYTHONPATH = "src/VisionIQ.Intelligence"
.\.venv\Scripts\python.exe -c "from dotenv import load_dotenv; load_dotenv('.env')"
# Environment variables from the command above do not persist to this shell.
# Use your existing environment loader, or run the acceptance module in one process:
.\.venv\Scripts\python.exe -c "from dotenv import load_dotenv; import sys; from app.knowledge.lrn08_acceptance import main; load_dotenv('.env'); sys.exit(main(['--project','Genoa Kiosk','--query','kiosk payment ticket']))"
```

The command is **read-only** and returns a JSON PASS/FAIL summary. It validates
the published snapshot's ADO chunk provenance (source, current revision and ACL),
the pinned model and 768-dimensional vectors, and non-empty keyword **and**
live Gemini-vector search results for the given query. It prints counts, not
source content. Change the query to words known to occur in your approved
Genoa Kiosk demo records if keyword results are empty. This acceptance command
does **not** prove semantic relevance, production latency, permission revocation,
Confluence search, or complete source-ingestion coverage.

Run targeted unit tests and lint:

```powershell
$env:PYTHONPATH = "src/VisionIQ.Intelligence"
.\.venv\Scripts\python.exe -m pytest tests/intelligence/test_indexing_failures.py -q
.\.venv\Scripts\python.exe -m ruff check src/VisionIQ.Intelligence/app/knowledge/lrn08_acceptance.py src/VisionIQ.Intelligence/app/knowledge/indexer.py src/VisionIQ.Intelligence/app/knowledge/index_cli.py tests/intelligence/test_indexing_failures.py
.\.venv\Scripts\python.exe -m ruff format --check src/VisionIQ.Intelligence/app/knowledge/lrn08_acceptance.py src/VisionIQ.Intelligence/app/knowledge/indexer.py src/VisionIQ.Intelligence/app/knowledge/index_cli.py tests/intelligence/test_indexing_failures.py
```

PostgreSQL DB tests (`tests/intelligence/test_search_index_db.py`) require a
**disposable, isolated** database and `VISIONIQ_DB_INTEGRATION=1`; do **not**
run these fixtures against the published 135-item demo database because they
modify evidence and snapshot state. The production GCS reader rejects `mem://`
while the injected integration-test reader deliberately supports it.
