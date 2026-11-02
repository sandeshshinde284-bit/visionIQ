# LRN-02 — Test report and evidence register

Report date: **2 November 2026**. This is a consolidated evidence log based on user-run local commands, not a claim that GitHub CI or a cloud environment ran those commands. No confidential page bodies or tokens are included.

## Automated test execution
Command:
```powershell
dotnet test .\tests\VisionIQ.Ingestor.Tests\VisionIQ.Ingestor.Tests.csproj --configuration Release --no-restore --filter "FullyQualifiedName~Confluence|FullyQualifiedName~ContentRevisionTests" --verbosity normal
```
Reported result: **37 passed, 0 failed, 0 skipped, exit 0**. Includes inherited restriction evaluation, unauthorized synthetic viewer, decision recognition by title/label/template, snapshots with versions and ACLs, permission changes without new content versions, and canonical evidence access metadata. This filtered run does not prove a deployed retrieval endpoint exists or denies an actual unauthorized engineer. An earlier `FullyQualifiedName~ConfluenceAccess` filter matched **zero** tests and must not be cited as a passing test run.

## Live read-only inventory
Configured approved ERK Confluence Cloud space:
- Pages found: **589**
- Complete ACLs: **527**
- Incomplete ACLs: **62**
- Complete + restricted: **213**
- Complete + unrestricted: **314**
- Incomplete + restricted: **1**
- Incomplete + unrestricted: **61**
These figures are internally consistent; 213 + 1 + 61 = 275 restricted or unverified pages withheld by metadata discovery.

## Controlled local sync
- Scope: `--source confluence --since 2026-11-01`, isolated JSONL/file checkpoint directory.
- Run ID `3acff8e9-0bac-4e8f-b65c-5419101fea1c`; exit **2 (Partial)**.
- Exported page snapshots: **2**, both with positive versions and version histories.
- ACL output records: **589**.
- Exported pages failing ACL-safety check: **0**.
- Fresh restricted/unverified pages withheld from ordinary JSONL: **3**.
- Design-decision records produced in this small date slice: **0**.
- Neither of the two exported pages had labels or extracted properties.
- Exit 2 is **not** a successful complete history ingest; it records intentional withholding and incomplete ACLs.

## Metadata discovery runs
Title-only release: `pages=589; eligible-complete-unrestricted=314; title-candidates=0; withheld-or-unverified=275; exit=0`.
Title+labels release: `pages=589; eligible-complete-unrestricted=314; title-candidates=0; label-candidates=0; label-lookup-failures=0; withheld-or-unverified=275`; user transcript states `Discovery exit code: 0c`, apparently a trailing character; command itself reported no lookup failures. Do **not** misstate this as a separate compiled integration test. Discovery results prove only the query behavior and eligibility counts, not the presence/absence of decision content in templates.

## Missing acceptance evidence
1. Real requester identity mapping to Confluence account and groups, including membership freshness.
2. An actual restricted page denied by the VisionIQ retrieval interface for an unauthorized user, including absence from search and AI brief context.
3. Corresponding authorized-user positive retrieval when permission is granted.
4. End-to-end ACL change/revocation demonstration through persisted storage and retrieval.
5. A qualifying real ADR in the approved ERK space was not found by title or label; label/title discovery is not an exhaustive body/template scan. Synthetic decision-recognition tests passed.

## Test execution hygiene
No confidential page titles, IDs, URLs, bodies, credentials or group membership lists should be committed. Store local logs and reports in ignored artifact directories and publish only safe summaries. Do not mark test evidence as PASS until the actual test run returns exit 0 and asserts intended denials.
