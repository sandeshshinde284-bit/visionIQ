# How to Understand the VisionIQ Code

**Quick Start:** Three diagrams show you everything:
1. [File Connections & Feature Map](images/file-connections.html) — Which files implement which features?
2. [Code Flow Scenarios](images/code-flow-scenarios.html) — What happens step-by-step when feature runs?
3. Plus code comments explaining the WHY

---

## The Three Questions You'll Ask

### Question 1: "Which files do I need to understand for LRN-03 (Evidence Archival)?"

**Answer:** Open [file-connections.html](images/file-connections.html)

Look at the **Feature Map** table:

| Feature | Files |
|---------|-------|
| **LRN-03** | RawEvidenceArchive.cs (GCS) |
| | EvidenceRevisionStore.cs (PG) |
| | PostgreSQL schema + migrations |

Then read:
1. `RawEvidenceArchive.cs:26` — How Cloud Storage archival works (with comments)
2. `EvidenceRevisionStore.cs` — How PostgreSQL versioning works

**Time to understand: ~15 minutes**

---

### Question 2: "I'm debugging: a work item was written twice. Where did deduplication fail?"

**Answer:** Open [code-flow-scenarios.html](code-flow-scenarios.html) → Scenario 1

Trace the execution:
1. ✅ LoadCursorAsync() — Did cursor load correctly? (check checkpoint store)
2. ✅ QueryIdsAsync() — Did query return expected IDs?
3. ✅ GetWorkItemsAsync() — Were batches read?
4. ✅ SaveCursorAsync() — Did cursor save? (this is where duplication prevention happens!)

**The checkpoint mechanism:**
- If cursor saves BEFORE all records are written → Some records stay unwritten
- Next run re-queries and re-writes them
- Result: **No duplicates** (because checkpoint is the "write cursor", not the "read cursor")

Read: `SyncRunner.cs:451` for the SaveCursorAsync logic and comments

**Time to debug: ~10 minutes**

---

### Question 3: "I want to trace what happens when a webhook arrives. Which files?"

**Answer:** Open [code-flow-scenarios.html](code-flow-scenarios.html) → Scenario 2

Files involved:
1. `SourceChangeProcessor.cs:94` — Entry point (ProcessAsync)
2. `PostgresSourceChangeInbox.cs:177` — Claim event with unique constraint
3. Handler implementation — Re-fetches from ADO
4. `PostgresSourceChangeInbox.cs:267` — Mark complete or quarantine

**Key insight:** 
- Unique (source_id, event_id) prevents duplicates
- Lease expiry (5 min) allows worker recovery
- Dead-lettering after 5 attempts prevents poison events

Read: `SourceChangeProcessor.cs:86` with comments explaining deduplication

**Time to understand: ~15 minutes**

---

## Learning Path by Role

### 👨‍💻 **Frontend: Just Want to Query Evidence**

You need:
1. **Data contract** → Read LRN-03 README (what fields are stored?)
2. **Query examples** → Read COMMAND-REGISTER.md (SQL examples)
3. **Understanding** → You don't need file details; understand evidence_revisions schema

**Time: 30 minutes**

---

### 🏗️ **Backend: Implementing New Feature**

You need:
1. **Architecture Overview** → Read ARCHITECTURE-DIAGRAMS.md (5 min)
2. **File Map** → Open [file-connections.html](images/file-connections.html) (find what relates to your feature)
3. **Code Flow** → Open [code-flow-scenarios.html](images/code-flow-scenarios.html) (see execution order)
4. **Deep Dive** → Read the .cs files with comments

**Time: 1-2 hours to be proficient**

---

### 🐛 **Debugging: Production Issue**

You need:
1. **Symptom** → Use it to identify which feature is broken
2. **Feature Map** → Find the files
3. **Code Flow** → Trace the execution
4. **Comments** → Read the WHY (e.g., "why didn't checkpoint save?")
5. **Data** → Query PostgreSQL to see actual state

**Time: 15-30 minutes depending on complexity**

---

### 📚 **Learning: New to the Codebase**

**Recommended order:**

**Day 1: Big Picture** (1 hour)
- Read: [START-HERE/ARCHITECTURE-OVERVIEW.md](../START-HERE/ARCHITECTURE-OVERVIEW.md)
- Look at: [learn-pipeline-flow.html](images/learn-pipeline-flow.html) (visual pipeline)
- Result: "I understand data flows from ADO → links → archive → sync → graph"

**Day 2: Trace One Feature** (2 hours)
- Pick: LRN-01 (History Connectors) — simplest
- Read: [LRN-01-HISTORY-CONNECTORS/README.md](LRN-01-HISTORY-CONNECTORS/README.md)
- Look at: [file-connections.html](images/file-connections.html) → LRN-01 section
- Read: `AdoWorkItemSource.cs` and `SyncRunner.cs` (with comments)
- Result: "I understand how work items are read and synced"

**Day 3: Checkpoint Logic** (2 hours)
- Look at: [code-flow-scenarios.html](images/code-flow-scenarios.html) → Scenario 1 (work item sync)
- Read: `SyncRunner.cs:421` (LoadCursorAsync) → `SyncRunner.cs:451` (SaveCursorAsync)
- Trace: Why checkpoint is the last *written* item, not last *queried* item
- Result: "I understand why there are no duplicates even on crashes"

**Day 4: Webhook Processing** (1 hour)
- Look at: [code-flow-scenarios.html](images/code-flow-scenarios.html) → Scenario 2
- Read: `SourceChangeProcessor.cs` and `PostgresSourceChangeInbox.cs`
- Result: "I understand at-least-once semantics and deduplication"

**Day 5: Your First Feature** (2 hours)
- Pick: A real task or bug
- Use: File map and code flow diagrams to navigate
- Read: Comments in relevant .cs files
- Result: "I can find and fix bugs independently"

**Total: 8 hours to be comfortable with the codebase**

---

## How to Read a .cs File

When you open a .cs file, here's the order:

### 1️⃣ **Class/Interface Comment** (top of class)
```csharp
/// <summary>
/// This explains WHY the class exists and what it does.
/// </summary>
public sealed class SyncRunner
```

**Example from SyncRunner.cs:**
> "Reads every configured project, resuming each stream from its checkpoint. A source that fails is reported as NOT SYNCED with the reason; the others continue."

**Action:** Understand the purpose before diving into code.

### 2️⃣ **Constructor Parameters**
Look for what this class *depends on*:
```csharp
public SyncRunner(
    AdoClient client,           // Talks to Azure DevOps
    ICheckpointStore checkpoints,   // Loads/saves cursor
    IRecordSink sink,           // Writes to storage
    SourceCapabilityRegistry registry   // Tracks what works
)
```

**Action:** These are your key dependencies; understand each one.

### 3️⃣ **Main Method** (usually public, starts async task)
Find the entry point:
```csharp
public async Task<SyncReport> RunAsync(SyncRequest request, ...)
```

Read the **method summary** and inline comments.

**Action:** This is your roadmap.

### 4️⃣ **Follow the Call Chain**
Each method calls others. Method comments explain:
- WHAT it does
- WHY it does it
- WHAT data flows where

**Action:** Don't try to read all at once; follow calls one at a time.

### 5️⃣ **Comments Explain Invariants**
Look for lines like:
```csharp
// B-10: the query is ordered by ChangedDate, Id ascending.
// When every returned item was written, checkpointing at the
// last written item is safe and lets the next run continue.
```

**Action:** These explain WHY the code is written that way; they're gold.

---

## Diagrams & Their Purpose

| Diagram | Shows | Use When |
|---------|-------|----------|
| [learn-pipeline-flow.html](images/learn-pipeline-flow.html) | Source → Links → Archive → Sync → Graph pipeline | You need the big picture |
| [file-connections.html](images/file-connections.html) | Which files implement which features | You want to know where to look |
| [code-flow-scenarios.html](images/code-flow-scenarios.html) | Step-by-step execution of key scenarios | You want to trace what happens |
| [webhook-at-least-once.html](images/webhook-at-least-once.html) | Webhook dedup + retry logic | You need to understand webhooks |
| [learn-pipeline-flow.html](images/learn-pipeline-flow.html) (checkpoint diagram) | Why checkpoint works | You're debugging duplicates |

---

## Cheat Sheet: File Locations

**Need to add a new data field?**
- Model: `src/VisionIQ.Api/Features/Evidence/`
- Schema: `sql/migrations/`

**Need to fix sync logic?**
- Source reading: `src/VisionIQ.Ingestor/Ado/`
- Sync orchestration: `src/VisionIQ.Ingestor/Sync/SyncRunner.cs`
- Checkpoint logic: `src/VisionIQ.Ingestor/Checkpoints/`

**Need to fix webhook processing?**
- Handler: `src/VisionIQ.Api/Features/Backfill/SourceChangeProcessor.cs`
- Dedup inbox: `src/VisionIQ.Api/Features/Backfill/PostgresSourceChangeInbox.cs`

**Need to fix link detection?**
- Extractor: `src/VisionIQ.Ingestor/Links/LinkExtractor.cs`
- Store: `src/VisionIQ.Api/Features/TicketFixes/PostgresTicketFixLinkStore.cs`

---

## When You're Stuck

### "I don't understand what this method does"

1. **Read the method comment** (top of method)
2. **Look at code comments** (inline explanations)
3. **Find where it's called** → Understand context
4. **Read the .cs file's class comment** → Understand role
5. **Trace a real example** → Use [code-flow-scenarios.html](images/code-flow-scenarios.html)

### "Why is this designed this way?"

Comments answer this! Examples:
- "Why checkpoint at last *written*?" → Read SyncRunner.cs:220
- "Why IfGenerationMatch=0?" → Read RawEvidenceArchive.cs:50
- "Why dedup inbox?" → Read SourceChangeProcessor.cs:177

### "What data is here and why?"

- **Evidence**: See LRN-03 README (what, why)
- **Schema**: See sql/migrations/ (EXACTLY what's stored)
- **Queries**: See COMMAND-REGISTER.md (examples)

---

## Testing Your Understanding

### Quick Checkpoints

**Checkpoint 1: The Big Picture**
- [ ] I can explain data flow: ADO → Links → Archive → Sync → Graph
- [ ] I can name the 7 LRN features
- [ ] I understand checkpoints prevent duplicates

**Checkpoint 2: File Map**
- [ ] I can find files for any feature (use [file-connections.html](images/file-connections.html))
- [ ] I can name which files are .NET vs Python
- [ ] I understand SyncRunner is the orchestrator

**Checkpoint 3: Execution Tracing**
- [ ] I can trace work item sync (Scenario 1) step-by-step
- [ ] I understand why checkpoint saves happen last
- [ ] I know what happens if a step fails (quarantine)

**Checkpoint 4: Code Reading**
- [ ] I can open SyncRunner.cs and understand the structure
- [ ] I can find where comments explain design decisions
- [ ] I can trace a method call to its implementation

---

## Resources Summary

**Learning materials created for you:**

1. **Architecture Diagrams** (5 visual mechanisms)
   - [ARCHITECTURE-DIAGRAMS.md](ARCHITECTURE-DIAGRAMS.md) — Text + diagrams

2. **Code Flow Diagrams** (3 key scenarios)
   - [code-flow-scenarios.html](images/code-flow-scenarios.html) — Interactive execution traces

3. **File Connections** (feature-to-files map)
   - [file-connections.html](images/file-connections.html) — Which files implement what?

4. **Code Comments** (in .cs files)
   - SyncRunner.cs — Checkpoint logic
   - SourceChangeProcessor.cs — Webhook deduplication
   - RawEvidenceArchive.cs — Cloud Storage archival
   - PostgresTicketFixLinkStore.cs — Link retraction lifecycle
   - AdoWorkItemSource.cs — Two-phase query strategy

5. **Documentation** (high-level references)
   - Each LRN-XX feature has README + Architecture + Acceptance docs
   - COMMAND-REGISTER.md — How to test each feature locally

---

## Next: Local Testing

Once you understand the code, test it locally:
1. Set up: [docs/OPERATIONS-AND-INFRASTRUCTURE/README.md](../OPERATIONS-AND-INFRASTRUCTURE/README.md)
2. Run: [COMMAND-REGISTER.md](COMMAND-REGISTER.md)
3. Verify: Check PostgreSQL to see data actually stored
4. Debug: Use code comments + flow diagrams if something breaks

**Ready? Let's test!**
