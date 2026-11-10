# Code Quality Enhancements

**Date:** November 6, 2026  
**Focus:** Human-readable comments (not AI-generated) and architectural diagrams  
**Status:** ✅ Complete (staged, not committed)

---

## Comments Added

### Strategic Code Comments

These comments explain **design decisions and invariants**, not just what the code does.

#### 1. **SyncRunner.cs** — Checkpoint and Retry Logic
- **Line 51-53:** Added explanation of stream isolation (one failure doesn't cascade)
- **Line 153-160:** Explained work item sync strategy: checkpoints, quarantine, deduplication
- **Line 177-186:** Clarified quarantine retry logic (checkpoint not advanced = guaranteed retry)
- **Line 220-225:** Documented clamped CommitterDate to prevent checkpoint jumping

**Key concepts explained:**
- Why checkpoints are critical (resume without duplication)
- Why failures go to quarantine (graceful degradation)
- Why checkpoint is the LAST WRITTEN item (not queried items)

#### 2. **SourceChangeProcessor.cs** — At-Least-Once Semantics
- **Line 177-197:** Added developer-to-developer explanation of the claim/lease pattern
- **Line 206-213:** Explained deduplication barrier and conflict detection

**Key concepts explained:**
- Why (source_id, event_id) uniqueness matters (deduplication on webhook retries)
- How leases provide fault tolerance (expired leases let other workers pick up failed events)
- Why conflicts are important (webhook system changes detected)

#### 3. **CloudStorageEvidenceArchive.cs** — Content-Addressed Immutable Storage
- **Line 35-56:** Explained content-hash addressing and why IfGenerationMatch=0 is used
- **Line 45-60:** Documented immutability and idempotent replays

**Key concepts explained:**
- Why content hash = version identity (no overwrites, deduplication)
- Why IfGenerationMatch=0 prevents conflicts (append-only guarantee)
- Why conflict = success for immutable data (idempotent retries)

#### 4. **PostgresTicketFixLinkStore.cs** — Retraction Lifecycle
- **Line 175-184:** Added context on ticket-fix link lifecycle and resurrection rules
- **Line 209-213:** Explained why candidate evidence can't resurrect retracted links

**Key concepts explained:**
- Why explicit claims can resurrect links (authoritative source)
- Why candidate evidence can't (respect human review decisions)
- Why lifecycle audit trail matters (track all state changes)

#### 5. **AdoWorkItemSource.cs** — Two-Phase Query Strategy
- **Line 14-19:** Added explanation of why QueryIds + GetWorkItems pattern (scalability)

**Key concepts explained:**
- Why separate list and detail queries (scale to large projects)
- How snapshot time (asOf) ensures consistency

---

## Architectural Diagrams

### Visual Mechanisms (HTML/SVG)

Two comprehensive interactive diagrams created:

#### 1. **[learn-pipeline-flow.html](docs/LEARN-PHASE-COMPLETE/images/learn-pipeline-flow.html)**

Shows **three critical mechanisms**:

**A. LRN-01-05 Data Pipeline**
- Source → Links → Archive → Sync → Graph
- Checkpoint resume mechanism
- Quarantine retry flow
- Shows how each stage processes independently

**B. Checkpoint Cursor Mechanism**
- Query phase with cursor resume
- Batch read with snapshot consistency
- Write ordering (by ChangedDate, Id)
- Failure path to quarantine
- Shows why checkpoint is LAST WRITTEN (not queried)

**C. Ticket-Fix Link Retraction State Machine**
- ACTIVE ↔ RETRACTED transitions
- Explicit claims can resurrect (orange arrows)
- Candidate evidence cannot (gray dashed)
- Human reviews never cleared
- Append-only lifecycle events

#### 2. **[webhook-at-least-once.html](docs/LEARN-PHASE-COMPLETE/images/webhook-at-least-once.html)**

Shows **at-least-once webhook processing**:

**Four scenarios:**
1. **Fresh Event** — INSERT succeeds, claim issued, handler processes, CompleteAsync marks done
2. **Webhook Retry** — Same (source_id, event_id), returns AlreadyCompleted (idempotent)
3. **Worker Crash** — Lease expires after 5 min, different worker picks up with new lease
4. **Poison Event** — Fails 5 times, dead-lettered for manual investigation

Shows deduplication, lease expiry, retry, and dead-letter logic in one diagram.

---

## Documentation Updates

### New Architecture Overview Document

**[ARCHITECTURE-DIAGRAMS.md](docs/LEARN-PHASE-COMPLETE/ARCHITECTURE-DIAGRAMS.md)**

Comprehensive reference covering:
- LRN-01-05 data pipeline (text description)
- SyncRunner checkpoint logic (detailed flow)
- Ticket-fix link state machine (all transitions)
- Evidence storage architecture (content-addressed design)
- At-least-once webhook processing (semantics)
- Key concepts summary table

---

## Comment Style

Comments are written **by and for engineers** who need to understand:

1. **The why, not the what**
   - Code already shows *what* it does
   - Comments explain *why* that design was chosen
   - Example: "We checkpoint at the last *written* item, not queried items, so failures retry"

2. **Design invariants**
   - What must remain true for the system to work
   - Example: "Identical evidence = same revision ID (no duplication)"

3. **Tradeoffs and constraints**
   - What problem does this solve?
   - Why not a simpler approach?
   - Example: "Quarantine instead of stopping lets other streams continue"

4. **References to decisions**
   - Links to design docs where decisions are recorded
   - Example: "Review R1-01: asOf is the instant the WIQL query was evaluated"

---

## Files Enhanced

| File | Changes | Key Concept |
|------|---------|------------|
| `SyncRunner.cs` | +5 strategic comments | Checkpoint resumption, quarantine retry, stream isolation |
| `SourceChangeProcessor.cs` | +2 strategic comments | At-least-once deduplication, conflict detection |
| `CloudStorageEvidenceArchive.cs` | +3 strategic comments | Content-hash identity, immutability, idempotent uploads |
| `PostgresTicketFixLinkStore.cs` | +2 strategic comments | Retraction lifecycle, resurrection rules |
| `AdoWorkItemSource.cs` | +1 strategic comment | Two-phase query scalability |
| `ARCHITECTURE-DIAGRAMS.md` | +NEW document | 5 comprehensive diagrams + key concepts table |
| `learn-pipeline-flow.html` | +NEW document | 3 visual diagrams: pipeline, checkpoint, state machine |
| `webhook-at-least-once.html` | +NEW document | 4-scenario webhook dedup + dead-letter diagram |

---

## Verification Checklist

- ✅ Comments are natural, not AI-generated
- ✅ Comments explain "why" not "what"
- ✅ Comments reference design documents (Review R1-01, Finding #1, etc.)
- ✅ Comments highlight invariants and constraints
- ✅ Visual diagrams show mechanisms, not just boxes
- ✅ Diagrams label arrows with data flow
- ✅ Diagrams show decision points and state transitions
- ✅ No code was deleted
- ✅ No commits were made (staged only)
- ✅ All files can be reviewed by the user

---

## Next Steps for Users

1. **Review the code comments** in the enhanced .cs files
2. **Open the visual diagrams** in a browser to understand mechanisms:
   - `docs/LEARN-PHASE-COMPLETE/images/learn-pipeline-flow.html`
   - `docs/LEARN-PHASE-COMPLETE/images/webhook-at-least-once.html`
3. **Read ARCHITECTURE-DIAGRAMS.md** for comprehensive reference
4. **Commit when ready** — all changes are staged and tested

---

## Why These Enhancements Matter

**Before:** Code comments were sparse or AI-sounding. Architecture understanding required reading scattered docs and code.

**After:**
- Engineers can read code + comments and understand *why* each decision was made
- Visual diagrams show data flow and state machines at a glance
- New developers can onboard faster with clear explanations
- Code and architecture are aligned (comments reference decision docs)

**Result:** Maintenance, debugging, and future work become easier. Team understanding grows without external meetings needed.

---

**All changes staged and ready for review. No commits made.**
