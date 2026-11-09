# LEARN Phase Documentation (LRN-01 through LRN-07)

**Status (9 Nov 2026):** LRN-01–07 implementation documentation; **not fully E2E accepted**. Real-source, DB and cloud acceptance remain partly open. Authentication and per-user ACL work is deferred.

**Next wave:** LRN-08–13. See the [agreed execution and acceptance sequence](../phase1-learn-integration-plan.md#agreed-execution-sequence--move-to-lrn-0813).

---

## Overview

The LEARN phase is VisionIQ's foundation: reading approved engineering history, storing it immutably, and linking evidence to enable intelligent decision-making.

### What Each Feature Does

| Feature | Purpose | Status |
|---------|---------|--------|
| **[LRN-01: History Connectors](LRN-01-HISTORY-CONNECTORS/)** | Read Azure DevOps history (work items, PRs, commits, builds) | ✅ Complete |
| **[LRN-02: Confluence](LRN-02-CONFLUENCE/)** | Confluence page ingestion; ADR/ACL-specific functionality deferred | Code present; acceptance partial |
| **[LRN-03: Versioned Evidence](LRN-03-VERSIONED-EVIDENCE/)** | Store evidence immutably in PostgreSQL + Cloud Storage | ✅ Complete |
| **[LRN-04: Ticket-Fix Links](LRN-04-TICKET-FIX-LINKS/)** | Link tickets to their fixes; track reverts | ✅ Complete |
| **[LRN-05: Backfill & Sync](LRN-05-BACKFILL-AND-SYNC/)** | Full backfill + incremental sync + webhooks | ✅ Complete |
| **[LRN-06: Code Intelligence](LRN-06-CODE-INTELLIGENCE/)** | Scan code for symbols, imports, dependencies | ✅ Complete |
| **[LRN-07: Relationship Graph](LRN-07-RELATIONSHIP-GRAPH/)** | Evidence-backed graph: tickets→PRs→commits→files→symbols/tests; internal inspection | Code and focused CI verified; real-data E2E pending |

---

## Recommended Reading

### For New Developers
1. [Architecture Overview](../START-HERE/ARCHITECTURE-OVERVIEW.md) — System diagram
2. [Integration Flow](INTEGRATION-FLOW.md) — Data flow across all features
3. [Your favorite feature](LRN-01-HISTORY-CONNECTORS/README.md) — Pick one to deep dive

### For Operations/DevOps
1. [Operations Guide](../OPERATIONS-AND-INFRASTRUCTURE/)
2. Each feature's SETUP section (e.g., [LRN-03 Setup](LRN-03-VERSIONED-EVIDENCE/03-SETUP-COMMANDS-AND-TROUBLESHOOTING.md))

### For Review/Understanding Decisions
1. [Decision Log](../REVIEW-AND-AUDIT/DECISION-LOG.md) — All decisions with rationale
2. Each feature's DECISIONS.md file

---

## Feature Deep Dives

### LRN-01: History Connectors
[📂 LRN-01 Folder](LRN-01-HISTORY-CONNECTORS/)

Reads approved Azure DevOps sources:
- Work items + comments + revisions
- Pull requests + commits + changes
- CI builds and links

**Key concepts:** Checkpoints (resumable sync), partial failures, source scope filtering

**Read:** [LRN-01 README](LRN-01-HISTORY-CONNECTORS/README.md)

---

### LRN-02: Confluence
[📂 LRN-02 Folder](LRN-02-CONFLUENCE/)

Ingests approved Confluence pages and versions. The former ADR-specific discovery,
live per-user access checks and restricted-page ACL classification were removed
from the active implementation. They are tracked for separate rework; do not
interpret the older linked feature documentation as proof of these behaviors.

**Key concepts:** Page version evidence, source scope, explicitly deferred per-user permissions

**Read:** [LRN-02 README](LRN-02-CONFLUENCE/README.md)

---

### LRN-03: Versioned Evidence
[📂 LRN-03 Folder](LRN-03-VERSIONED-EVIDENCE/)

Single source of truth: immutable, versioned, deduplicating:
- PostgreSQL: identity, revisions, access metadata
- Cloud Storage: raw bytes archived by SHA-256
- No overwrites; conflicts detected

**Key concepts:** Content-hash revisions, archive-first, idempotent upsert

**Read:** [LRN-03 README](LRN-03-VERSIONED-EVIDENCE/README.md)

---

### LRN-04: Ticket-Fix Links
[📂 LRN-04 Folder](LRN-04-TICKET-FIX-LINKS/)

Determines which fixes closed which tickets:
- Explicit links (ADO-declared)
- Inferred (commit message mentions)
- Revert tracking + retraction lifecycle
- Candidate reverts (title-matched)

**Key concepts:** Classification (explicit/inferred/verified), revert detection, lifecycle events

**Read:** [LRN-04 README](LRN-04-TICKET-FIX-LINKS/README.md)

---

### LRN-05: Backfill & Sync
[📂 LRN-05 Folder](LRN-05-BACKFILL-AND-SYNC/)

Keeps evidence current:
- Full backfill for approved projects
- Webhooks for near-real-time updates
- Scheduled reconciliation to catch missed changes
- Quarantine for failed records

**Key concepts:** Checkpoints, webhooks, reconciliation, quarantine

**Read:** [LRN-05 README](LRN-05-BACKFILL-AND-SYNC/README.md)

---

### LRN-06: Code Intelligence
[📂 LRN-06 Folder](LRN-06-CODE-INTELLIGENCE/)

Scans code without executing it:
- Symbols (functions, classes, imports)
- Call graphs + dependencies
- File metadata + change impact
- Parser-failure recovery

**Key concepts:** Structural parsing (no execution), multi-language support, fault tolerance

**Read:** [LRN-06 README](LRN-06-CODE-INTELLIGENCE/README.md)

---

### LRN-07: Relationship Graph
[📂 LRN-07 Folder](LRN-07-RELATIONSHIP-GRAPH/)

Evidence-backed graph for trusted internal/local use:
- Nodes: tickets, PRs, commits, files, symbols and tests
- Typed assertions and exact source-revision provenance
- Bounded traversal, missing-link detection, and latest-active relationships
- Reversions, retractions, renames and source tombstones retained/filtered appropriately
- Public Graph HTTP API disabled; end-user ACL enforcement deferred

**Key concepts:** Proof strength, active/disputed/retracted edges, source/project boundaries,
trusted local inspection (not an authenticated user-facing API).

**Current implementation guide:** [LRN-07 local graph validation](../lrn07-hackathon-graph-validation.md)

**Read:** [LRN-07 README](LRN-07-RELATIONSHIP-GRAPH/README.md)

---

## Cross-Feature Documentation

- [**Integration Flow**](INTEGRATION-FLOW.md) — How data flows LRN-01 → 02 → 03 → 04 → 05/06/07
- [**State Machines**](STATE-MACHINES.md) — Lifecycle diagrams for each feature
- [**Command Register**](COMMAND-REGISTER.md) — Testing and operations commands
- [**Integration Test Matrix**](INTEGRATION-TEST-MATRIX.md) — Test status across all features

---

## Acceptance Status

**Do not infer a complete live end-to-end test from passing unit tests.**
Previous status entries were too optimistic. Track per-story evidence in the
relevant test report and keep skipped/blocked gates explicitly open.

| Story group | Current verified scope | Still required |
| --- | --- | --- |
| LRN-01–05 | Implemented source and canonical evidence paths; test coverage | Outstanding real-source/DB integrations and cloud handoff where applicable |
| LRN-06 | Commit-pinned parsing and dependency implementation | Approved source-backed scan and integration verification |
| LRN-07 | Graph persistence/traversal implementation; focused CI and local inspector tests | Actual ticket → PR → commit → file → symbol/test E2E, renames/reverts and multi-project evidence |
| LRN-08–13 | Next implementation/testing wave | Local canonical acceptance, indexed retrieval, lessons and PR event lifecycle |
| LRN-14 | After demo | Separate later scope |

**Sequence:** (0) Review incoming auth-cleanup push → (1) record remaining
LRN-01–07 acceptance gaps without rebuilding completed features →
(2) implement/test LRN-08–13 locally → (3) run full LRN-01–13 E2E →
(4) deploy/validate manually on Google Cloud →
(5) final evidence review and LEARN acceptance. LRN-14 is after demo.

See [Phase 1 LEARN integration and acceptance plan](../phase1-learn-integration-plan.md)
for detailed gates, single-run commands and deferred security decisions.

---

## Key Design Principles

1. **No time-proximity inference** — Links come from explicit references or authoritative evidence only
2. **Access scope** — Honor approved source/project boundaries; live per-user ACL checks are deferred, not claimed as implemented
3. **Content-hash identity** — Revisions identified by hash; changes create new revisions
4. **Append-only audit** — No overwrites; lifecycle events are immutable
5. **Internal-only graph access** — Public Graph API remains disabled until an authenticated interface is safely implemented
6. **Weak evidence → partial result** — Never invent certainty

---

## Documentation Quality

Each LRN feature folder follows a consistent structure:

```
LRN-XX-NAME/
├── README.md ........................... Overview & status
├── 01-ARCHITECTURE.md .................. Design & concepts
├── 02-SETUP-COMMANDS.md ................ How to set up locally
├── 03-ACCEPTANCE.md .................... Acceptance criteria & results
├── DECISIONS.md ........................ Why we made each choice
├── images/ ............................ Diagrams and visuals
└── (Feature-specific docs)
```

---

## Review & Audit Trail

See [Review & Audit](../REVIEW-AND-AUDIT/) for:
- [Decision Log](../REVIEW-AND-AUDIT/DECISION-LOG.md) — All decisions with rationale
- [Integration Review](../REVIEW-AND-AUDIT/2026-Q4/INTEGRATION-REVIEW.md) — Latest findings
- [Blocker Fixes](../REVIEW-AND-AUDIT/2026-Q4/BLOCKER-FIXES-B01-B17.md) — Critical findings resolved

---

## Next Steps

**New to LEARN?**
→ Start with [START-HERE](../START-HERE/) and [Architecture Overview](../START-HERE/ARCHITECTURE-OVERVIEW.md)

**Want to set up?**
→ Go to [Operations Guide](../OPERATIONS-AND-INFRASTRUCTURE/)

**Want to contribute?**
→ Read [Developer Guides](../DEVELOPER-GUIDES/)

**Want to understand decisions?**
→ Check [Decision Log](../REVIEW-AND-AUDIT/DECISION-LOG.md)

---

**Status Summary:** LRN-01–07 source implementation exists and focused checks run; complete real-data E2E and cloud acceptance remain pending. Continue LRN-08–13 while tracking unverified gates explicitly.
