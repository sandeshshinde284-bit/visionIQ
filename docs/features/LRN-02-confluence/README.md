# LRN-02 — Include design decisions from Confluence

**Documentation baseline:** 2 November 2026  
**Implementation status:** Implemented and locally exercised  
**Documentation status:** Complete as-of baseline; updates tracked here  
**User-story acceptance:** OPEN — end-to-end user-specific retrieval authorization depends on LRN-03 integration

![Visual overview: how LRN-02 handles Confluence permissions](images/lrn02-security-flow.svg)

**Start with [the illustrated step-by-step walkthrough](05-Illustrated-Walkthrough.md)** — realistic synthetic examples, Mermaid flows, inherited ACL examples and explanations of each ERK count.

## Document index
1. [Architecture and implementation](01-Architecture-and-Implementation.md) — behavior, code locations, ACL model and storage boundaries.
2. [Setup and operations](02-Setup-and-Operations.md) — configuration, safe commands, diagnostics and operational limits.
3. [Test report and evidence](03-Test-Report-and-Evidence.md) — reported 37/37 tests, 589-page inventory, controlled sync, discovery outputs and unverified scenarios.
4. [Acceptance and LRN-03 handoff](04-Acceptance-and-LRN03-Handoff.md) — original acceptance criteria, security contract, risks and final signoff checklist.
5. [Illustrated walkthrough](05-Illustrated-Walkthrough.md) — diagram-based explanation, practical examples and test-result interpretation.

## Verified user-run evidence
- Confluence Cloud approved ERK space: **589 pages**, **527 complete** ACLs, **62 incomplete**.
- ACL classification: **314 complete/unrestricted**, **213 complete/restricted**, **61 incomplete/unrestricted**, **1 incomplete/restricted**.
- Local JSONL sample since 2026-11-01: **2 versioned page snapshots**, **589 ACL records**, **3 page snapshots withheld**, **0 unsafe exported page matches**, partial sync exit **2**.
- Automated filtered suite: **37 passed, 0 failed, 0 skipped**.
- Safe discovery: **0 title candidates**, **0 label candidates**, **0 label lookup failures** among 314 eligible pages. This is not a template/body search.
- **Not yet demonstrated:** per-requester denied read through the actual VisionIQ retrieval API, denial from AI context, and permission revocation at retrieval time.

## Decision
We can finish and freeze the LRN-02 **documentation baseline** now. We must **not** mark the story itself 100% accepted until the LRN-03 secure retrieval path passes positive and negative authorization integration tests. Neither production Cloud Run behavior nor complete 589-page historical content ingestion has been verified here.

## Safety
Keep credential values and Confluence contents out of source control. Read-only discovery is not proof of access for any particular engineer, and `--since` does not bypass full-space ACL evaluation. See the operations guide before new live runs.
