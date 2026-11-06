# LRN-03 — Canonical, versioned evidence contract and implementation

**Status:** In progress; implementation exists, but story acceptance is not complete.

LRN-03 consumes approved records from source connectors and records them as
versioned, attributed, permission-aware evidence. It must not invent historical
links, leak private content, or silently overwrite conflicting revisions.

## Original story acceptance

1. Each item retains its immutable source ID, revision, timestamps and access list.
2. Raw copies are archived to approved Cloud Storage; **redaction occurs before indexing**.
3. Re-importing the same item is idempotent and preserves historical revisions.

Cloud Storage and end-to-end access enforcement are **pending FND-04 and LRN-01
integration**. This proposal alone does not satisfy any acceptance criterion.

## Implementation progress — 30 October 2026

This document started as a design contract. **The following is now
implemented on the LRN-03 story branch, pending review and integration:**

| Story task | Implementation |
| --- | --- |
| S05-01 | `db/migrations/versions/20261030_0002_versioned_evidence.py`: canonical source items and immutable revision rows in `evidence` schema; FND-03 Alembic migration chain |
| S05-02 | `EvidenceIdentity`: deterministic SHA-256 source keys with length-prefixed provider/instance/project/repository/type/native ID |
| S05-03 | `CloudStorageEvidenceArchive`: content-addressed raw JSON object upload with generation precondition; requires approved private bucket and workload identity |
| S05-04 | `EvidenceNormalizer`: conservatively extracts metadata for work-item revisions |
| S05-05 | `EvidenceNormalizer`: conservatively extracts metadata for pull requests, commits and reviews |
| S05-06 | `EvidenceNormalizer`: conservatively extracts metadata for CI runs, deployment and incident artifacts |
| S05-11 | `PostgresEvidenceStore`: transactional source-row lock, immutable native revision/hash conflict detection, latest pointer and replay idempotence |

**API boundary:** `EvidenceIngestionService` is registered internally in
`Program.cs` and intentionally has no unauthenticated HTTP endpoint.
A trusted LRN-01/LRN-02 connector must create `EvidenceEnvelope` objects.

**Validation:** .NET unit tests cover identity/collision separation, fail-closed
ACL, metadata redaction, archive-before-store and archive failure. An opt-in
real PostgreSQL integration test verifies one source with two immutable
revisions, duplicate replay and content conflict; CI runs it against an
isolated local PostgreSQL volume. Cloud Storage itself is not live-tested.

**Privacy:** This implementation deliberately **does not** index free-text
titles, descriptions or bodies. Only a narrow allowlist of metadata is
normalized. This prevents unreviewed personal/sensitive text entering
search until an approved provider-aware redaction process exists.
`acl_verified=false` retains an empty ACL and no read API is exposed.
Source-revision ACL enforcement in retrieval is pending the dedicated
access-aware query layer and current provider-permission reconciliation.

**Still required before story closure:**
1. Align connectors and field mapping with completed LRN-01/LRN-02 code
   (not present in the public companion repository yet).
2. Create an **approved locked-down Cloud Storage bucket** and authorize
   the runtime service identity; verify immutable raw upload, retry and
   retention, with no sensitive object names.
3. Implement audited current-ACL filtered retrieval and permission-change
   invalidation (an old ACL snapshot must not authorize a newly revoked user).
4. Approve provider-specific normalization/redaction for any content
   actually used by retrieval, and test non-leakage.
5. Verify authorized production-like Cloud SQL migration and credentials
   after FND-04 permissions are available. No cloud mutation is done here.
6. Repeat live acceptance using one actual LRN-01 source imported twice
   with two revisions and verify access control before marking LRN-03 Done.

## Original proposal and invariants

## Stable canonical identity (LRN-01 provides source metadata)

Define a normalized identity composed of:

| Field | Meaning |
| --- | --- |
| `provider` | `azure_devops`, `git`, `confluence` (future providers are extensible) |
| `source_instance` | Canonical organization/server/tenant identifier, not a credential |
| `project_id` | Provider-native project identifier; tenant-wide records use explicit scope |
| `repository_id` | Provider-native repository identity where applicable, else null |
| `kind` | `work_item`, `pull_request`, `commit`, `build`, `file`, `design_page`, ... |
| `external_id` | Immutable provider-native ID (NOT display title or path) |

Canonical item key: SHA-256 of the length-prefixed UTF-8 encoding of the six
fields above, including explicit null markers. Length-prefixing avoids
ambiguous delimiter collisions. Do not silently lowercase case-sensitive
provider IDs. Normalize provider names and canonical source URLs by a reviewed
provider-specific normalization function only.

A Git file is additionally pinned to repository + commit SHA + path (and
blob SHA), rather than a mutable working-tree path alone.

## Evidence revision

The item has a stable canonical item key; each provider revision has:

- `provider_revision_id` (required, immutable; use Git commit/blob SHA when appropriate)
- `source_updated_at` and `observed_at` (UTC timestamps; never infer one from another)
- `content_sha256` (hash of the immutable raw-source snapshot, computed before redaction)
- `source_url` and `retrieved_under_principal` (no access tokens)
- `acl_snapshot` with the provider's access restrictions as of that revision
- `redaction_version`, `redacted_payload`, `normalizer_version`
- `raw_snapshot_uri`, `archive_status` (e.g. `PENDING`, `ARCHIVED`, `FAILED`)

When the source does not give a revision number, a reviewed revision identity
rule must be chosen per provider; do not treat `observed_at` as evidence of
a new provider revision.

## PostgreSQL model (implemented by Alembic revision 20261030_0002)

All writes use the **evidence** schema, owned by the .NET API/ingestor:

- `evidence.source_items`: immutable item key and identity columns,
  latest-known revision pointer, first/last observed timestamps.
- `evidence.source_revisions`: item key + provider revision unique key,
  original/retrieval timestamps, content hash, redacted normalized payload,
  versioned ACL snapshot, source URL, archive status and immutable raw-copy
  location once archived.
- An import/upsert audit table (or existing audited event facility) records
  `CREATED`, `UNCHANGED`, `NEW_REVISION`, `CONFLICT`,
  `NOT_SYNCED`, `PARTIAL`, `FAILED` with correlation ID and counts.

The source and revision tables are committed in the existing Alembic chain. Integration with the LRN-01 source-envelope owner remains subject to review; no parallel EF migrations were created.

## Ingestion and deduplication invariants

1. Transactionally upsert immutable item identity by unique item key.
2. Insert revision by unique (`item_key`, `provider_revision_id`).
3. Replaying the same revision/hash changes **zero** revision rows.
4. A different hash for an existing native revision is a **CONFLICT**;
   never silently overwrite already attributed evidence.
5. A newer valid revision updates the latest pointer only after persistence;
   older revisions remain queryable.
6. Inaccessible or unreachable sources are `NOT_SYNCED`, **not empty history**.
7. Archive failure is explicit and independently retryable; an unarchived
   item is never falsely marked archived.

## Privacy and access rules (fail closed)

- Missing, unknown or unmapped ACL -> `UNVERIFIED_ACCESS`: excluded from
  search, Brief and agent responses until permission mapping is verified.
- Store source ACL as a versioned snapshot, never trust free-text access claims.
- Retrieval rechecks requester access where required by provider semantics.
- Minimize and redact credentials, tokens, PII and sensitive fields before
  indexing. Do not copy unredacted source into vector embeddings, logs, or PRs.
- Never treat inferred source-to-fix links as verified provenance.
- LRN-06 scanner results inherit repository/file/commit provenance and access
  scope from LRN-03; no default public visibility.

## Contracts to agree with concurrent workstreams

**LRN-01 producer** should supply: canonical provider/source/project/repo IDs,
provider-native item and revision IDs, source URL, source timestamps,
permission info, raw snapshot reference or content under secure transfer,
sync result state and correlation ID. No direct writes to the knowledge schema.

**LRN-06 producer** supplies: approved repository ID, commit SHA, file path,
blob SHA, adapter name/version, symbols and spans, parser capability/status,
and explicit dependency provenance. The canonical evidence writer decides
how to store it, after source ACLs are attached.

**LRN-04 consumer** uses explicitly supported ticket/PR/commit references and
confidence labels. Time proximity alone must never create a confirmed fix link.

## Review and acceptance tests to implement next

- Same item imported twice -> one item, one revision, unchanged count.
- Changed provider revision -> second immutable revision, latest pointer correct.
- Same native revision but different raw hash -> conflict, no evidence loss.
- Project/repository collision -> distinct item identity across scopes.
- Missing ACL -> excluded from query/Brief by default.
- Restricted revision -> never returned to an unauthorized requester.
- Partial source error -> NOT_SYNCED/PARTIAL reporting, not zero items.
- Archive failure -> retry path, no false ARCHIVED state.
- LRN-06 Git blob and source SHA -> joins correct immutable revision.

## Integration boundaries

No Cloud Storage bucket creation or Google Cloud deployment; no automatic ingestion from ADO; no source tokens; no unapproved source-envelope changes. All LRN-03 story and acceptance criteria remain **Open** until end-to-end evidence is verified.
