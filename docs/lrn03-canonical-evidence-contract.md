# LRN-03 — Canonical, versioned evidence contract (proposal)

**Status:** Draft interface for LRN-01, LRN-03, LRN-06 integration. **Not an approved schema or completed user story.**

LRN-03 consumes approved records from source connectors and records them as
versioned, attributed, permission-aware evidence. It must not invent historical
links, leak private content, or silently overwrite conflicting revisions.

## Original story acceptance

1. Each item retains its immutable source ID, revision, timestamps and access list.
2. Raw copies are archived to approved Cloud Storage; **redaction occurs before indexing**.
3. Re-importing the same item is idempotent and preserves historical revisions.

Cloud Storage and end-to-end access enforcement are **pending FND-04 and LRN-01
integration**. This proposal alone does not satisfy any acceptance criterion.

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

## Proposed PostgreSQL model (migration not yet committed)

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

No new tables are being created yet: confirm the shape with the LRN-01
source-envelope owner to avoid incompatible migrations. Continue to use
FND-03's *single Alembic migration chain*, not a parallel EF migration system.

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

## Non-goals of this first commit

No Cloud Storage bucket creation; no Google Cloud deployment; no schema
migration yet; no automatic ingestion from ADO; no source tokens; no approved
source-envelope changes until integration review. All LRN-03 story and
acceptance criteria stay **Open**.
