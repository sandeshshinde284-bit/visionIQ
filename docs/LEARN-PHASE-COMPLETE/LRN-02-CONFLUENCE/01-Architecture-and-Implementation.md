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
Local JSONL sinks are not user-aware permission gates. Restricted/unverified page bodies are withheld from ordinary JSONL. The canonical evidence pipeline stores only a space-level candidate (never the page's restricted users or groups); whether a person may see a page is decided live per request (next section). Live end-to-end denial proof is not yet demonstrated. Do not interpret persistence as permission to retrieve or put content in an LLM prompt. ACL snapshots must not be exposed as ordinary user evidence.

## Per-user read path (implemented; live end-to-end proof pending)

Showing a page to a person is decided **live by Confluence**, not by VisionIQ's stored ACL copy:

1. The caller is authenticated by the VisionIQ API (Microsoft Entra ID token: tenant, audience, scope
   and approved calling application are validated; see `docs/graph-api.md`).
2. The caller's e-mail is mapped to their Atlassian account ID through an administrator-maintained
   **identity map** (`CONFLUENCE_IDENTITY_MAP_FILE`, JSON `{"engineer@company.com": "<account-id>"}`).
   Unmapped users are denied without contacting Confluence; nothing is guessed.
3. `ConfluencePageAccessVerifier` (`src/VisionIQ.Api/Features/Evidence/ConfluenceReadAccess.cs`) asks
   Confluence Cloud `POST /rest/api/content/{id}/permission/check` whether **this account can read this
   page now**. Confluence evaluates space permissions, page restrictions and inherited ancestor
   restrictions itself, so a permission change or revocation applies at the next request.
4. Any error, an unapproved space or site, an ACL snapshot record, or a **Data Center** page (which
   has no such check) is denied. A denied page is simply left out of the response, exactly as if it did not exist; nothing reveals that it was hidden.

Unit tests: `ConfluencePageAccessTests` (allow, deny, unmapped user, Confluence error, Data Center,
other spaces/sites/kinds) and `ConfluencePrincipalTests` (site spelling normalization).

**What is still missing for an end-to-end demonstration:** a **delivery surface** that returns
Confluence evidence to a user. Today the only evidence endpoint is the graph API (LRN-07), which
returns items linked to a work item, and nothing yet creates ticket-to-page (`design_related`) links.
So the check exists but no request can reach a Confluence page yet. It becomes testable when either
(a) ticket-to-page links are extracted (an LRN-02/LRN-07 follow-up), or (b) BRIEF retrieval serves
Confluence evidence through this verifier.

## Detection conditions
Labels: `decision`, `decisions`, `decision-log`, `adr`, `design-decision`, `architecture-decision`. Title forms include numbered ADRs and explicit Decision, Design Decision or Architecture Decision headings. Decision-template recognition requires Status and Decision/Outcome properties after content normalization. This **template** path does not run in metadata-only discovery.

## Design boundaries
No standalone UI. Source data stays in approved configured spaces. No cloud deployment is required to perform discovery. LRN-03 provides versioned evidence storage. The per-user read check is part of LRN-02 (above); the final live allow/deny demonstration waits for a delivery surface.

## Relevant source files
- `src/VisionIQ.Ingestor/Confluence/ConfluenceModels.cs`
- `src/VisionIQ.Ingestor/Confluence/ConfluenceAccess.cs`
- `src/VisionIQ.Ingestor/Confluence/ConfluenceCloudSource.cs`
- `src/VisionIQ.Ingestor/Confluence/ConfluenceDataCenterSource.cs`
- `src/VisionIQ.Ingestor/Confluence/ConfluenceText.cs`
- `src/VisionIQ.Ingestor/Confluence/ConfluenceSyncRunner.cs`
- `src/VisionIQ.Ingestor.Cli/ConfluenceDecisionDiscovery.cs`
- `src/VisionIQ.Ingestor.Cli/Program.cs`
- `src/VisionIQ.Api/Features/Evidence/ConfluenceReadAccess.cs` (per-user read check, identity map)
- `src/VisionIQ.Api/Features/Graph/GraphServiceRegistration.cs` (wiring; enabled only when Confluence and the identity map are configured)
