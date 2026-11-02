# LRN-02 — Acceptance matrix, risks and LRN-03 handoff

## Source acceptance criteria
1. **Pages are stored with version and page permissions.** Controlled two-page local version/ACL sample passed; tests cover versioning and permission updates. Whole-space historical content import has not been demonstrated.
2. **A page an engineer cannot open in Confluence is never shown to that engineer through VisionIQ.** Unit tests for `ConfluenceAccess.CanView` passed. **End-to-end retrieval enforcement is not yet accepted**.

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

## Explicit LRN-03 integration contract
LRN-03 must provide a concrete protected read path that:
1. Authenticates the caller and maps them to the appropriate Confluence identity/group principals (not an arbitrary client-supplied identity).
2. Checks the approved space, complete current ACL, all inherited restriction levels, and permission refresh/revocation before exposing any page text.
3. Excludes denied evidence from search hits, evidence detail responses, API error diagnostics, cached snippets, LLM prompts and generated summaries.
4. Permits a known authorized caller to read an eligible restricted fixture and denies a different unauthorized caller.
5. Includes integration tests for missing identity, missing group, wrong space, incomplete ACL, inherited restrictions, stale/revoked permissions and cross-user cache leakage.
6. Produces sanitized test output proving both allow and deny cases, with no confidential content shared.

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
