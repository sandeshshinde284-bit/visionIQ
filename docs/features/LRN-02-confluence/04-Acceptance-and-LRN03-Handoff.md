# LRN-02 — Acceptance matrix, risks and LRN-03 handoff

## Source acceptance criteria
1. **Pages are stored with version and page permissions.** Controlled two-page local version/ACL sample passed; tests cover versioning and permission updates. Whole-space historical content import has not been demonstrated.
2. **A page an engineer cannot open in Confluence is never shown to that engineer through VisionIQ.** Ingestion-side `ConfluenceAccess.CanView` and the API's live per-user check (`ConfluencePageAccessVerifier`) are implemented and unit-tested. **End-to-end retrieval enforcement is not yet accepted**: no endpoint returns Confluence pages to users yet.

**Status: implementation and documentation complete; original user-story acceptance remains OPEN.** Do not close the Trello card or merge based on the current sample.

## Security risks and controls
| Risk | Control / required follow-up |
| --- | --- |
| Ingestor credential sees more than an engineer | At retrieval, resolve actual requesting Confluence identity/groups; enforce each ancestor ACL |
| Permission revocation after a snapshot | Recheck authoritative permissions and avoid stale authorization before serving evidence |
| Unreadable/missing/truncated ACL | Fail closed; no body to unauthorized callers |
| Restricted content in a generated brief or LLM prompt | Filter before retrieval/context construction, not after generation |
| Space access confused with page access | Verify space plus each restricted ancestor level |
| Local unrestricted JSONL leaked | Keep corporate exports local, ignored, access-controlled |
| Canonical evidence archived but retrieval path not secure | Do not expose pages through the API until protected retrieval is tested |
| Permission status silently downgraded | Reject or quarantine incomplete ACL; use audit/error metrics |

## Protected read path: what exists and what remains

| Requirement | Status |
| --- | --- |
| Authenticate the caller; never trust a client-supplied identity | **Implemented** in the API (Entra ID token validation, approved callers only) |
| Map the caller to their Confluence identity | **Implemented** via `CONFLUENCE_IDENTITY_MAP_FILE`; unmapped users are denied |
| Check space, page, inherited restrictions and revocation at read time | **Implemented** by asking Confluence Cloud live per page (`permission/check`); Data Center pages stay hidden |
| Denied evidence absent from responses and diagnostics | **Implemented** for the graph API: denied items are left out exactly like missing ones, with no hidden counts; a denied or missing root item gives the same 404 |
| A user-facing surface that returns Confluence evidence | **Missing**: ticket-to-page links are not created yet, and BRIEF retrieval is not built |
| Denied evidence absent from search hits, snippets and LLM prompts | **Pending** the BRIEF retrieval and prompt path |
| Live positive and negative tests (an allowed and a denied engineer), including revocation | **Pending** a delivery surface, a real identity map and Entra configuration |

LRN-03 remains responsible for versioned evidence storage; it is not the owner of the per-user check.

## Return-to-LRN-02 signoff checklist
- [x] Approved Confluence inventory and connector behavior verified
- [x] Versioned page and ACL samples verified
- [x] Permission enforcement logic unit-tested
- [x] Safe local export behavior checked
- [x] Title + label candidate discovery completed
- [x] Configuration, architecture, operations and evidence documented
- [ ] Live identity-aware retrieval positive and negative tests
- [ ] AI-context/search-result non-disclosure verified end-to-end
- [ ] Revocation/change-of-permission behavior verified at retrieval
- [ ] Acceptance signed off and story marked complete

## How to interpret no live ADR candidates
Zero title- or label-matched candidates among 314 complete/unrestricted eligible pages is a valid **negative discovery result**, not a failed detector and not proof that no decision exists. Keep automated synthetic title/label/template tests as functional evidence. If an authorized, specifically relevant page is later approved for a live test, validate it without bulk historical export. This is useful additional evidence, not an invented production success.

## Traceability
- Story: `LEARN LRN-02 — Include design decisions from Confluence`.
- Related: LRN-01 source history connector, LRN-03 secure evidence storage/retrieval.
- Code paths: see `01-Architecture-and-Implementation.md`.
- Local commands: see `02-Setup-and-Operations.md`.
- Results: see `03-Test-Report-and-Evidence.md`.

## LRN-03 integration closure (3 November 2026)

LRN-03's **original versioned-storage acceptance** is complete on `feature/learn` (persisted source identity/revisions/timestamps/ACL, real GCS integrity and redaction boundary tests passed). This does **not** sign off LRN-02 live per-user Confluence delivery, denied snippets or revocation: those remain pending delivery-surface and authorized-user integration tests. See [LRN-03 acceptance](../LRN-03-versioned-evidence/04-Test-Evidence-and-Acceptance.md).
