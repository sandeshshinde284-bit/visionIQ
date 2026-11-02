# LRN-01 — Implementation guide

## Entry points and files

| Location | Why inspect it |
|---|---|
| `src/VisionIQ.Ingestor.Cli/Program.cs` | Entry point, modes, sync report, exit codes, dependency setup |
| `src/VisionIQ.Ingestor/Sync/SyncRunner.cs` | Work-item / PR / commit / build stream orchestration, checkpoint advancement and stream isolation |
| `src/VisionIQ.Ingestor/Ado/` | ADO HTTP integration, pagination and source-record models |
| `src/VisionIQ.Ingestor/Checkpoints/` | Resume cursor design |
| `src/VisionIQ.Ingestor/Sync/JsonLinesSink.cs` | Local JSONL output envelope |
| `src/VisionIQ.Ingestor.Cli/CanonicalEvidenceSink.cs` | Bridge from source records to LRN-03 canonical evidence |
| `tests/VisionIQ.Ingestor.Tests/` | Unit/regression tests for sync, source parsing, checkpoints and failure recovery |

## CLI lifecycle

1. Parse `inventory`, `sync`, or `serve-webhooks`.
2. Load settings from environment/merged `.env`; validate output mode.
3. Open file or PostgreSQL checkpoint store.
4. Select source streams and approved projects/repositories.
5. Read data from ADO; write to JSONL or canonical sink.
6. Persist per-stream checkpoints after successful processing.
7. Emit `report.json` and an exit code.

Exit codes from current CLI comments: **0** all streams synced, **2** partially/not synced, **1** configuration or usage failure.

## Modes

- `INGESTION_OUTPUT_MODE=jsonl`: local export; `work-items.jsonl` lines are wrappers containing `record`. Good for inspection but sensitive engineering data remains on disk.
- `INGESTION_OUTPUT_MODE=canonical`: PostgreSQL checkpoint and evidence metadata plus immutable Cloud Storage snapshot archive; requires `CHECKPOINT_STORE=postgres` and `GCS_EVIDENCE_BUCKET`.

## The subtle difference between sync options

- **Delta:** normal sync uses saved checkpoint for each stream. `--since` influences streams **without** checkpoints.
- **Full:** `--full --since YYYY-MM-DD` requests a backfill from the date rather than respecting normal delta cursors; can be large.
- **Reconcile:** a bounded lookback for missed changes; source implementation deliberately avoids advancing normal delta checkpoints during reconciliation.

Never interpret `--since` as a hard limit if a checkpoint already exists.

## Data safety

The ingestor reads Azure DevOps; it does not edit ADO work items. **It does write evidence** to its configured local or canonical destination. Do not run `--full` against shared cloud storage without permission and an understood immutable-revision strategy. Raw records and JSONL output may contain sensitive engineering details.
