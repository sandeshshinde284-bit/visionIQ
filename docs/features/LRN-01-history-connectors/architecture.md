# LRN-01 — Architecture and data flow

## Source-to-evidence sequence

```mermaid
sequenceDiagram
    autonumber
    participant T as Trigger (local CLI today)
    participant C as LRN-01 CLI / SyncRunner
    participant A as Azure DevOps REST
    participant K as PostgreSQL checkpoints
    participant E as Canonical evidence sink (LRN-03)
    participant G as Cloud Storage
    participant P as PostgreSQL evidence
    T->>C: sync selected sources
    C->>K: Load cursor per source/scope
    K-->>C: Previous cursor or none
    C->>A: Read newer work items/PRs/commits/builds
    A-->>C: Source records + links
    loop Each eligible record
        C->>E: Write source evidence
        E->>G: Archive source snapshot
        E->>P: Persist evidence identity/version
    end
    C->>K: Save checkpoint after successful progress
    C-->>T: Sync report + exit code
    Note over C,K: A failed stream must not advance past unwritten evidence
```

This depicts the *canonical mode* conceptually. There are additional graph/link projection steps, per-item handling, revision processing and exception cases in implementation.

## Responsibilities

| Concern | Component | Description |
|---|---|---|
| Entry point | `src/VisionIQ.Ingestor.Cli/Program.cs` | Parses CLI, chooses JSONL/canonical mode, creates dependencies and reports results |
| ADO integration | `src/VisionIQ.Ingestor/Ado/` | REST clients/readers for work items, Git and build sources |
| Orchestration | `src/VisionIQ.Ingestor/Sync/SyncRunner.cs` | Loops approved scopes; processes streams and checkpoints |
| Local export | `src/VisionIQ.Ingestor/Sync/JsonLinesSink.cs` | Writes envelopes `{"key", "fetchedAt", "record"}` |
| Checkpoints | `src/VisionIQ.Ingestor/Checkpoints/` | File or PostgreSQL cursor storage |
| Canonical destination | `src/VisionIQ.Ingestor.Cli/CanonicalEvidenceSink.cs` and evidence services | Evidence archival/persistence (LRN-03 boundary) |

## What LEARN always-on means

```mermaid
flowchart LR
  H["Initial backfill"] --> S["Trusted evidence store"]
  D["Repeated delta sync"] --> S
  W["Approved event/webhook path — planned integration"] --> S
  S --> R["Normalize + relate (LRN-03–07)"]
  R --> I["Index/qualify (later LEARN stories)"]
  I --> Q["Future ticket briefs"]
```

**The LEARN capability can be continuously current without an always-running ingestor process.** Scheduled jobs or event triggers run ingestion repeatedly, with checkpoints and downstream processing.

## Critical boundaries

- **LRN-01** connects and reads source history, handles failures and checkpoints.
- **LRN-03** archives/normalizes canonical evidence; its storage guarantees need separate acceptance.
- **LRN-07** projects evidence-backed relationships, not raw-source connectivity.
- **LRN-02** handles Confluence; its permission controls and testing are separate.
- Pipeline inventory only identifies definitions; successful ingestion of actual **build runs** requires a separate test (completed 2026-11-02).
