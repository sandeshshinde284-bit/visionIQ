# VisionIQ — Detailed knowledge references and deferred scope

**Updated:** 11 November 2026.

Despite this historical folder name, LRN-08–11 core development is **implemented** and described in the [current LRN-01–13 handbook](../LEARN-PHASE-COMPLETE/LEARN-HANDBOOK.md). These files are technical references, not proof that real-source/cloud acceptance passed.

| Capability | Detailed reference | Actual scope |
|---|---|---|
| LRN-08 keyword/Vertex indexing | [Search index](LRN-08-HYBRID-RETRIEVAL/SEARCH-INDEX.md) | Implemented; real approved snapshot proof required |
| LRN-09 evidence-aware retrieval | [Retrieval](LRN-08-HYBRID-RETRIEVAL/retrieval.md) | Implemented; full graph-neighbor/cross-project retrieval deferred |
| LRN-10 evidence-backed lessons | [Lesson qualification](LRN-10-EVIDENCE-BACKED-LESSONS/lessons.md) | Implemented; causes unverified until reviewed |
| LRN-11 KB readiness | [Knowledge readiness](../LEARN-PHASE-COMPLETE/LRN-11-KB-READINESS/README.md) | Focused tests pass; real-cloud safety gates open |
| LRN-12 event evidence | [Webhook deployment](../OPERATIONS-AND-INFRASTRUCTURE/LRN12-WEBHOOK-CLOUD-DEPLOYMENT.md) | Webhook code implemented, live ADO ingress not deployed |
| LRN-13 proof-based updates | [Manual learning runbook](../LEARN-PHASE-COMPLETE/LRN-13-MANUAL-LEARNING-ACCEPTANCE.md) | Manual replay implemented; automatic triggers/Pub/Sub deferred |

**Deferred for the hackathon:** live Entra delegated user authorization, restricted Confluence page retrieval, full graph-neighbor/cross-project traversal, automated cloud scheduling/CI/CD, automatic PR-event-to-reindex orchestration, and Pub/Sub `knowledge.changed` publishing. Never describe a deferred feature as a deployed guarantee.
