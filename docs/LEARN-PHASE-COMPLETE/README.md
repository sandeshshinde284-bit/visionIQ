# VisionIQ LEARN — documentation index

**Updated:** 11 November 2026 · **Coverage:** LRN-01–13

Start with the **[single living LEARN handbook](LEARN-HANDBOOK.md)**. It explains each story in the same six-section format (what it is, what it does, pending/not tested, how it fits, testing, and next story) and distinguishes current code from real cloud/data acceptance.

**Current hackathon mode:** **manual** approved ingestion, repository scan, indexing, retrieval, lesson refresh and verification. No claim of automatic PR-triggered learning, Pub/Sub knowledge.changed delivery, live delegated Microsoft Entra authorization, restricted Confluence page handling or complete cross-project graph traversal. The detailed older technical guides below remain useful but may describe earlier designs or tests; defer to the handbook and current code when status differs.

## Find the detailed source documentation

| Feature | Detailed guide |
|---|---|
| LRN-01 ADO connectors | [History connectors](LRN-01-HISTORY-CONNECTORS/README.md) |
| LRN-02 Confluence pages | [Confluence](LRN-02-CONFLUENCE/README.md) |
| LRN-03 canonical evidence | [Versioned PostgreSQL/GCS evidence](LRN-03-VERSIONED-EVIDENCE/README.md) · [schema contract](LRN-03-VERSIONED-EVIDENCE/CONTRACTS.md) |
| LRN-04 fixes and reverts | [Ticket-fix links](LRN-04-TICKET-FIX-LINKS/README.md) |
| LRN-05 synchronization | [Backfill and sync](LRN-05-BACKFILL-AND-SYNC/BACKFILL-OVERVIEW.md) |
| LRN-06 repository understanding | [Code intelligence](LRN-06-CODE-INTELLIGENCE/ARCHITECTURE.md) · [test verification](CODE-UNDERSTANDING-GUIDE.md) |
| LRN-07 relationship graph | [Graph architecture](LRN-07-RELATIONSHIP-GRAPH/ARCHITECTURE.md) · [hackathon validation](LRN-07-RELATIONSHIP-GRAPH/lrn07-hackathon-graph-validation.md) |
| LRN-08 indexing and embeddings | [Search index](../PLANNED-FEATURES/LRN-08-HYBRID-RETRIEVAL/SEARCH-INDEX.md) |
| LRN-09 retrieval | [Retrieval details](../PLANNED-FEATURES/LRN-08-HYBRID-RETRIEVAL/retrieval.md) |
| LRN-10 lesson patterns | [Qualification rules](../PLANNED-FEATURES/LRN-10-EVIDENCE-BACKED-LESSONS/lessons.md) |
| LRN-11 trust/readiness | [Knowledge-base readiness](LRN-11-KB-READINESS/README.md) |
| LRN-12 PR reviews/events | [Webhook cloud deployment and security](../OPERATIONS-AND-INFRASTRUCTURE/LRN12-WEBHOOK-CLOUD-DEPLOYMENT.md) |
| LRN-13 manual learning | [Manual fix/revert acceptance](LRN-13-MANUAL-LEARNING-ACCEPTANCE.md) |

## Operational and evidence references

- [LEARN command register](COMMAND-REGISTER.md) — historical commands and recorded outcomes; verify current branch/configuration before executing.
- [One canonical testing/acceptance matrix](../QUALITY-ASSURANCE/ACCEPTANCE-MATRIX.md) — recorded requirements, not blanket real-data acceptance.
- [Manual deployment and operations](../OPERATIONS-AND-INFRASTRUCTURE/README.md).
- [Dated integration findings](../REVIEW-AND-AUDIT/2026-Q4/LRN-1-13-LIVE-REVIEW-2026-11-09.md) — retain as historical audit evidence rather than current claims.
- [Source-of-truth master design](../Functional/VisionIQ_Master_Engineering_Design_v1_11_Final.pdf) — approved scope and design context.

## Completion definition

Code merged plus focused unit tests ≠ complete source/cloud acceptance. For the manual hackathon, confirm one approved ticket→fix→code→published search→cited Brief and a genuine fix→revert→disputed lesson in the **same approved** environment. Keep results PASS/FAIL/SKIP/BLOCKED, with source IDs, commit hashes and snapshot IDs. Never run disposable DB fixture tests against a shared or published demo database.

Avoid creating additional LRN-01–13 summary documents. Update [LEARN-HANDBOOK.md](LEARN-HANDBOOK.md); keep distinctive feature runbooks, architecture diagrams, schema contracts, dated evidence and troubleshooting material in place.
