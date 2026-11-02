# LRN-02 — Architecture and Implementation

## Purpose and acceptance criteria
Confluence design decisions must be available as versioned historical evidence while respecting page and inherited ancestor permissions. Source: LEARN LRN-02, S04-08 page snapshots, S04-09 permission mapping, S05-07 normalization. Requirements: (1) retain version and permission data and (2) never disclose a page to an engineer who cannot open it in Confluence.

## Components and flow
1. `ConfluenceSources.Create` chooses a Cloud or Data Center read-only connector.
2. `ConfluenceCloudSource` reads current pages, restrictions, labels and version metadata through Confluence REST endpoints. The metadata-only route skips storage body requests.
3. `ConfluenceAccess.ComputeAcls` follows the page-parent/ancestor chain and constructs effective page ACLs. Missing parents, malformed loops, overly deep chains and unreadable/truncated restrictions result in incomplete ACLs (deny).
4. `ConfluenceSyncRunner` writes ACL snapshots, selects fresh page versions, normalizes rich text and records detected decisions. It re-reads ACLs on every run because permissions can change without content revision.
5. `ConfluenceText.Normalize` yields plain text, sections and key/value properties. `DesignDecisionDetector.Detect` recognizes approved label, title or decision-template signals; outputs version-specific `DesignDecisionRecord` with ACL.
6. The `discover-confluence-decisions` CLI command reads metadata and labels, computes ACLs, excludes restricted or incomplete candidates and prints only counts.

## Permission model
`ConfluencePageAcl` contains page ID, space key, `Complete`, inherited `Levels` and a permission fingerprint. `ConfluenceAccess.CanView(acl,viewer)` requires a complete ACL, space membership and authorization at **every** restricted level; it denies on missing viewer identity or group context when needed. `Visible` applies this before yielding text. An ingestion service credential is **not** a substitute for the requesting engineer's Confluence identity. Cloud and Data Center principal identifiers must not be conflated.

## Export and canonical boundaries
Local JSONL sinks are not user-aware permission gates. Restricted/unverified page bodies are withheld from ordinary JSONL. The canonical evidence pipeline uses distinct access metadata, but a working per-engineer Confluence retrieval enforcement path and live denial proof are not yet demonstrated. Do not interpret persistence as permission to retrieve or put content in an LLM prompt. ACL snapshots must not be exposed as ordinary user evidence.

## Detection conditions
Labels: `decision`, `decisions`, `decision-log`, `adr`, `design-decision`, `architecture-decision`. Title forms include numbered ADRs and explicit Decision, Design Decision or Architecture Decision headings. Decision-template recognition requires Status and Decision/Outcome properties after content normalization. This **template** path does not run in metadata-only discovery.

## Design boundaries
No standalone UI. Source data stays in approved configured spaces. No cloud deployment is required to perform discovery. LRN-03 will provide protected evidence storage/retrieval and is the dependency for the final live per-user unauthorized-read acceptance test.

## Relevant source files
- `src/VisionIQ.Ingestor/Confluence/ConfluenceModels.cs`
- `src/VisionIQ.Ingestor/Confluence/ConfluenceAccess.cs`
- `src/VisionIQ.Ingestor/Confluence/ConfluenceCloudSource.cs`
- `src/VisionIQ.Ingestor/Confluence/ConfluenceDataCenterSource.cs`
- `src/VisionIQ.Ingestor/Confluence/ConfluenceText.cs`
- `src/VisionIQ.Ingestor/Confluence/ConfluenceSyncRunner.cs`
- `src/VisionIQ.Ingestor.Cli/ConfluenceDecisionDiscovery.cs`
- `src/VisionIQ.Ingestor.Cli/Program.cs`
