# LRN-03 — Deployed index and retrieval validation

**Status (2 November 2026): NOT YET VERIFIED.** Do not mark live deployed-index validation PASS from metadata unit tests, PostgreSQL revision tests or GCS tests.

## Code audit findings

- The .NET API `src/VisionIQ.Api/Program.cs` registers evidence ingestion and a permission-aware **graph** API; it does not register a dedicated raw evidence full-text/vector search endpoint.
- `PostgresEvidenceStore` persists `evidence.source_revisions.redacted_payload` and separately references raw GCS, but does not itself populate a vector/search index.
- `EvidenceNormalizer` filters fields; commit commentary and raw environment fields were removed. Eight focused regression tests passed.
- The earlier deployment established Cloud Run services `visioniq-api` and `visioniq-intelligence`; their **presence alone does not demonstrate index construction or a deployed retrieval endpoint**. A deployed environment must be inspected.

## Exact read-only inventory — Windows PowerShell

```powershell
$project = "gbr-mts-wgh-hkt-dev"
$region  = "europe-west3"
gcloud run services list --project=$project --region=$region --format="table(metadata.name,status.url,status.conditions[0].status)"
gcloud run jobs list --project=$project --region=$region --format="table(metadata.name)"
```

Do **not** interpret a healthy service as proof indexing is implemented.

## Database inspection — isolated local database (read only)

```powershell
docker exec visioniq-lrn03-test-postgres psql -U visioniq_test -d visioniq_lrn03_test -c "\dt knowledge.*"
docker exec visioniq-lrn03-test-postgres psql -U visioniq_test -d visioniq_lrn03_test -c "SELECT schemaname, tablename, indexname, indexdef FROM pg_indexes WHERE schemaname IN ('knowledge','evidence') ORDER BY schemaname, tablename, indexname;"
```

These commands inspect **tables and PostgreSQL indexes**, not necessarily actual indexed content or a search query. Do not switch to Cloud SQL credentials to force checks. A generic B-tree index on a source-items table is **not** a semantic downstream search index.

## Required end-to-end test before claiming PASS

1. Identify exact deployed index writer: module, consumer data contract, storage/index destination, and authenticated deployment identity.
2. Confirm index writer consumes only approved `redacted_payload`, not raw `RawJson`, text from GCS or unrestricted source descriptions. If full text is intended in the future, require a separate PII/secret policy and requester authorization.
3. Insert one uniquely identified **fictional** evidence record containing a sentinel in raw-only fields into an explicitly isolated test source. Confirm the GCS raw snapshot contains the sentinel and the downstream indexed projection does not.
4. Query the **actual authenticated retrieval endpoint** twice: approved metadata query returns the expected record; sentinel query returns no sensitive text. Check source ACL filtering for an unauthorized requester, plus that returned GCS URI is not a public download link.
5. Record exact endpoint, isolated target, commands, counts, sanitized output, cleanup and commit. No service redeployment or production writes without explicit authorization.

If no real index writer/retrieval path exists, label this gate **NOT IMPLEMENTED / NOT APPLICABLE TO LRN-03 STORAGE ACCEPTANCE**, and track its implementation under its intended learning/retrieval story, instead of inventing passing evidence.

## Current conclusion

LRN-03 raw storage, deduplication and metadata normalization have concrete local+GCS integration results. **A deployed indexing and retrieval flow has not been established or tested.** The 8/8 boundary test result cannot substitute for an authenticated deployed retrieval test.

## User-run infrastructure inventory — 2 November 2026

**Cloud Run listing:** observed `visioniq-api` and `visioniq-intelligence` services, and `visioniq-db-migrate` job in `gbr-mts-wgh-hkt-dev` / `europe-west3`. The listing also included unrelated project services; those are not part of the LRN-03 evidence trail. Listing proves resource existence, not service readiness, implemented retrieval routes, or up-to-date image revisions.

**Isolated local PostgreSQL:** `\\dt knowledge.*` yielded `Did not find any relation named "knowledge.*".` The `pg_indexes` query returned **27 indexes under `evidence`**, with no `knowledge` index rows. These include B-tree identity/revision/graph indexes and GIN `source_revisions_acl_idx` for `acl_principals`. No vector/embedding search index was observed. The query does not establish whether another Cloud SQL database has any indexes.

**Decision:** downstream deployed semantic indexing/retrieval remains **NOT IMPLEMENTED/NOT DEMONSTRATED in inspected environment**, rather than a failed LRN-03 storage criterion. Keep LRN-03 canonical evidence ingestion and secure redacted metadata handoff as a separate completed boundary. Track an independently scoped future story for an index writer, embedding/chunking safeguards, requester-level ACL validation and retrieval test before making any deployed search claims.
