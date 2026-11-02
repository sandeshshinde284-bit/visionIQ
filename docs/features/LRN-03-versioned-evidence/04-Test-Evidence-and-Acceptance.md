# LRN-03 — Actual test evidence and acceptance register

**Source of truth:** [LRN-03 Trello user story](https://trello.com/c/aqRFYZ9G/9-learn-lrn-03-store-history-as-versioned-evidence). Mark only executed, observed tests as PASS. All synthetic sample data mentioned here is fictional.

## Executed evidence — 2 November 2026

| Test/gate | Observed output | Status | Boundary |
| --- | --- | --- | --- |
| Synthetic focused service tests | `4 succeeded, 0 failed, 0 skipped`, net10.0, exit **0**, build succeeded | PASS | Archive ordering, archive failure, identity separation, metadata redaction through fakes |
| Disposable PostgreSQL container | `visioniq-lrn03-test-postgres`, localhost 55433, `pg_isready` exit **0** | PASS | Server readiness only |
| Alembic migration on isolated DB | `20261101_0012` | PASS | Migration applied in isolated database |
| Evidence table inventory | **14** evidence tables; `source_items`, `source_revisions` present | PASS | Table existence, not row correctness |
| First `db.manage bootstrap` command | CLI `invalid choice: 'bootstrap'`, exit **2** | COMMAND ERROR | Earlier command was wrong; correct CLI action is `roles`; not a database/bootstrap logic failure |
| `db.manage roles` | Not executed in supplied transcript | PENDING | Runtime role/bootstrap verification |
| Two revisions + identical replay | Not executed in supplied transcript | PENDING | **Official LRN-03 completion evidence** |
| Existing revision ID + altered content | Not executed in supplied transcript | PENDING | Conflict must be returned; no revision overwrite |
| GCS raw snapshot integrity/immutability | No new LRN-03 run supplied | PENDING | Prior LRN-01 5/5 GCS hash sample is supporting context only |
| End-user Confluence ACL retrieval | Not executed | OUTSIDE STORAGE GATE / OPEN DEPENDENCY | LRN-02 authorization handoff |

## Acceptance criteria (original Trello wording)

- [ ] Each record keeps its source ID, revision, timestamps and access list. **Schema/code located; actual stored row test pending.**
- [ ] A raw copy is kept in Cloud Storage; sensitive fields are redacted before indexing. **Service code and fake-backed tests passed; live archive and indexing checks pending.**
- [ ] Re-importing the same item updates it instead of creating a duplicate. **PostgreSQL upsert implementation located; real replay/integration test pending.**

**Completion evidence:** the same item imported twice leaves **one logical record with two source revisions** (after introducing the second distinct revision and replaying records). Do not mark complete before verifying counts and latest pointer in the disposable PostgreSQL database.

## Documentation policy

- Store **exact commands and sanitized results** here as soon as each test passes or fails.
- Track failures with root cause and corrective action; do not hide failed attempts.
- Never include tokens, passwords, confidential pages, source descriptions or unrestricted company identifiers.
- PR stays Draft; no cloud deployment or main-branch merge is authorized by these local checks.

## Next action

Run the exact `python -m db.manage roles` command with the target preflight in [setup/operations](03-Setup-Commands-and-Troubleshooting.md); record the actual result here before further integration testing.
