# LRN-09–13 acceptance audit — 2026-11-10

**Status: source-inspected, NOT fully acceptance-tested.** This report records verified code/test inventory against the five original Trello stories. Any test result not actually executed in the operator's environment is **PENDING**. LRN-08's published Genoa Kiosk evidence (135 ADO items, 554 Gemini chunks) is available for read-only demo queries. Do not run synthetic DB tests against that database.

| Story | Existing implementation | Existing verification | Open acceptance |
| --- | --- | --- | --- |
| LRN-09 Hybrid retrieval | `app.knowledge.retrieval.retrieve`: keyword + Gemini vector + code-path + lesson candidates, reciprocal-rank fusion, current-record ACL post-filter, required `live_check`, cited snippets, abstention | `test_retrieval.py` unit; `test_retrieval_db.py` snapshot, revocation, live-check, benchmark fixtures; `learn_e2e_stages.py` real canonical probe | Run real 135-item ranking/citation probe with representative query and labelled quality benchmark. Scope alone and a permissive callback do not establish real delegated-user authorization. Cache S07-12 is post-demo. |
| LRN-10 Evidence-backed lessons | `lessons.py` candidate grouping, `lesson_store.py` atomic persistence, human review/fork, history | `test_lessons.py`, `test_lessons_db.py` including human fork and revert | Confirm isolated DB suites. Missing `import uuid` in `lesson_store.fork` fixed in this branch; original code would raise `NameError` when a valid split reaches UUID creation. |
| LRN-11 KB readiness | Canonical evidence revocation, snapshots and consistency validation | `test_kb_readiness_db.py` writes per-case evidence; stage runner verifies artifact | Run isolated suite, retain per-case fixture, assertions and snapshot IDs. Real provider outages/revocations remain unverified; do not claim those acceptance checks passed. |
| LRN-12 PR service hook | ADO PR event parser and authenticated endpoint; handler refetches canonical current PR state for idempotency/out-of-order updates | `PullRequestEventTests.cs`, `WebhookHttpTests.cs` | Run .NET tests; live authenticated PR created/reviewed/abandoned/merged ADO delivery is still unverified and must not be described as tested. |
| LRN-13 Proof-only update | Targeted reindex and reused embeddings; lesson consolidator and append-only change events; timeline CLI | `test_lrn13_db.py` tests touched chunks, deletion and synthetic fix/revert | Run isolated suite; a real historical PR fix-then-revert demo is pending. `knowledge.change_events` is a database outbox: Pub/Sub publisher/delivery is a distinct infrastructure dependency, not proven by outbox writes. |

## Fast regression gate — no test-framework rewrite

**Environment:** repository root, Python venv, local PostgreSQL admin able to create/drop a new *uniquely named* database, migrations available. Existing `scripts/learn_e2e.py isolated-pytest` calls `scripts/learn_e2e_stages.py` to create, migrate, use and drop a separate database **for each test suite**. Its guard requires `DB_TARGET=local`, loopback `DB_HOST` and `DB_LOCAL_USER`/`DB_LOCAL_PASSWORD`. Do not bypass this guard or use the published demo DB as a fixture.

```powershell
$env:PYTHONPATH = "src/VisionIQ.Intelligence"
.\.venv\Scripts\python.exe -m pytest -q tests/intelligence/test_retrieval.py tests/intelligence/test_lessons.py
dotnet test tests/VisionIQ.Ingestor.Tests/VisionIQ.Ingestor.Tests.csproj --filter "FullyQualifiedName~PullRequestEventTests|FullyQualifiedName~WebhookHttpTests"
.\.venv\Scripts\python.exe -c "from dotenv import load_dotenv; import runpy,sys; load_dotenv('.env'); sys.argv=['scripts/learn_e2e_stages.py','isolated-pytest','tests/intelligence/test_retrieval_db.py','tests/intelligence/test_lessons_db.py','tests/intelligence/test_kb_readiness_db.py','tests/intelligence/test_lrn13_db.py']; runpy.run_path('scripts/learn_e2e_stages.py', run_name='__main__')"
```

**Result interpretation:** A successful run with nonzero passing assertions and zero failures/skips is evidence for the *fixture-level* criteria. It does **not** prove real ADO webhooks, delegated user permissions, Cloud Run deployment, Pub/Sub or labelled real-data retrieval quality. The runner writes logs under `artifacts/learn-e2e/isolated-pytest/` and removes its own disposable database when finished. If admin credentials are missing, stop and configure local development access; never relax the isolation guard.

## Follow-up scope

- The original Trello cards remain **To be Tested** until evidence is recorded for each acceptance criterion.
- Enterprise Confluence page-level authorization is separately deferred on existing LRN-02 follow-up Trello #55, not silently accepted as passed for LRN-09.
- The 14 Payments `mem://` canonical candidates are excluded from production indexing; they are not evidence of LRN-09–13 acceptance.
