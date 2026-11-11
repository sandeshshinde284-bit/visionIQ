# VisionIQ LEARN — living handbook (LRN-01 through LRN-13)

**Updated:** 11 November 2026  
**Code baseline:** `main` after merged PRs #27–32, including LRN-06 verification and LRN-13 manual learning.  
**One current LEARN overview:** Keep detailed story-level architecture, commands, schema contracts, historical test artifacts and cloud runbooks linked below; do not create another competing LEARN summary.

Each story uses the same six sections: **What it is → What it does → Pending/not tested → How it fits VisionIQ → Testing → Move to next**.

**Evidence vocabulary:** Implemented means code exists; verified means a specific test ran successfully; **To be Tested** means end-to-end real-source/cloud acceptance is still open; deferred means deliberately outside the manual hackathon MVP. Focused CI results do **not** imply that the entire repository or real ADO/GCP workflow passed.

**Hackathon delivery decision:** Run ingestion, indexing, scanning and lesson learning **manually** on approved sources, with a guarded canonical database and GCS bucket. The application must be functional; automated CI/CD, automatic PR-triggered indexing, Scheduler, Pub/Sub `knowledge.changed` delivery, full cross-project graph-neighbor traversal, Entra delegated authorization and restricted Confluence pages are **not being claimed**. BRIEF and DEFECT are separate colleague-owned implementations whose integrated acceptance has not yet been verified.

**Security:** Use read-only approved source access and private test data; never run destructive fixtures against the shared demo database or expose source content through unauthenticated endpoints.

---

## LRN-01 — History connectors (Azure DevOps)

### 1. What it is
The ingestion gateway that reads existing engineering history from **approved** Azure DevOps organizations and projects.

### 2. What it does
- Discovers permitted projects, repositories, work items, PRs, commits, builds and other supported history.
- Reads source records with their original provider identifiers and versions.
- Uses pagination, checkpoints and retry behavior so large history can be acquired incrementally.
- Reports unreachable/unsupported streams as partial or **NOT_SYNCED**, not as an empty history.
- Keeps source-provider operations separate from the canonical evidence destination.

### 3. Pending / not tested
- Complete coverage and checkpoint recovery across every intended approved project/repository have **not been demonstrated** by one synthetic suite.
- Real source rate limits, partial pages, API access failures and long-history runs still need recorded validation on approved test data.
- A successful ADO connection by itself is **not** proof that all history was captured.

### 4. How it fits into VisionIQ
This is **where facts enter**. Without LRN-01, subsequent graph, search and lessons have no trustworthy ADO input.

### 5. Testing
Run the interactive `./scripts/learn-e2e.ps1` menu and select **LRN-01–07** or **individual LRN-01**. Confirm actual ADO connectivity, approved project scope, checkpoint behavior and non-zero imported evidence. Record PASS/FAIL/SKIP/BLOCKED and the run artifact; do not treat the connection check as full acceptance.

**Supporting documentation:** [LRN-01 history connectors](LRN-01-HISTORY-CONNECTORS/README.md).

### 6. Move to LRN-02
After establishing that source discovery/ingestion works, bring approved Confluence page context into the evidence pipeline.

---

## LRN-02 — Confluence engineering knowledge

### 1. What it is
The connector for engineering documentation stored in approved Confluence spaces.

### 2. What it does
- Reads Confluence page metadata, versions and supported page content from configured spaces.
- Normalizes page sections/content so the canonical evidence and later search pipeline can use them.
- Keeps page identity and source revision so a later answer can cite its origin.
- Handles connector-level failures without inventing unavailable pages.

### 3. Pending / not tested
- **Current-scope change:** ADR/decision-specific discovery, restricted-page ACL classification/withholding and live **per-user** Confluence permission checks were removed/deferred in the 9 Nov cleanup. Some older LRN-02 documents still describe those historical capabilities: they are **not current acceptance evidence**.
- Live end-user authorization, permission revocation and restricted-content handling are **not accepted**. Only use a corpus whose ingestion and downstream use are approved under the current access assumption.
- Historical page coverage, page revisions and failure handling need confirmation against the current checkout rather than old test totals.
- Follow-up: [Confluence access-control/ADR rebuild](https://trello.com/c/Ws9wyOpP/55-learn-follow-up-lrn-02-confluence-access-control-adr-live-auth-restricted-page-acl-removed-from-code-needs-rebuild).

### 4. How it fits into VisionIQ
Adds **engineering context** (design and feature documentation) beside the source-code and ticket history. A feature page is valid source evidence; an ADR-specific page is **not mandatory** to use the connector.

### 5. Testing
Use the same LEARN runner's LRN-02 option with an approved Confluence space/page. Verify page ingestion and exact revision identity; distinguish available content from withheld/missing content. Do **not** claim live per-user ACL tests passed. Do not create, edit or change provider pages as part of the test.

**Supporting documentation:** [LRN-02 Confluence](LRN-02-CONFLUENCE/README.md) (**older security sections may be superseded**).

### 6. Move to LRN-03
ADO and Confluence supply records; LRN-03 preserves their provenance and versions in one canonical evidence store.

---

## LRN-03 — Canonical versioned evidence

### 1. What it is
The versioned **source of truth** for the engineering evidence that VisionIQ ingests.

### 2. What it does
- Assigns stable source-scoped identities (provider, instance, project, repository, kind and external ID).
- Stores current pointers and **immutable source revisions** in PostgreSQL.
- Archives exact raw snapshots in a private GCS bucket with hash-based integrity.
- Replays the same revision idempotently, records new revisions rather than overwriting history and rejects conflicting same-revision content.
- Separates raw source text from conservative/redacted metadata used for later processing.

### 3. Pending / not tested
- Historical evidence reports describe isolated PostgreSQL/GCS successes, but **do not replace** a fresh canonical E2E run against the current commit and roles.
- Verify local Docker/DB role setup, effective credentials, migration head, actual GCS permissions and cross-source identity collisions.
- Distinguish successful storage from source access authorization: stored permission metadata alone does not authenticate an end user.

### 4. How it fits into VisionIQ
All later decisions must trace back to **which source record and revision** supported them. LRN-08 indexes this evidence, and LRN-09 cites it.

### 5. Testing
Choose **canonical** output (not JSONL), use a pre-approved test DB and private GCS bucket, and verify the same work item imported twice does not duplicate its logical record. Check revision changes and raw archive references. Preserve source content and never clear a shared bucket for test convenience.

**Supporting documentation:** [LRN-03 versioned evidence](LRN-03-VERSIONED-EVIDENCE/README.md).

### 6. Move to LRN-04
Once provenance is reliable, start connecting defects to the PRs and commits that actually relate to them.

---

## LRN-04 — Ticket-to-fix associations

### 1. What it is
A relationship layer that answers **“Which PR/commit is associated with this defect, and why?”**

### 2. What it does
- Records explicit ADO-declared ticket→PR/commit relationships with exact source references.
- Keeps weaker inferred/mentioned associations separate from explicit evidence and human verification.
- Tracks multiple fixes, disputes, retractions and revert observations without silently overwriting the earlier evidence.
- Rejects **time proximity** as sufficient proof of a fix.
- Does **not** label a draft/active PR as a completed or human-verified fix.

### 3. Pending / not tested
- Confirm source-declared ticket→PR associations in **real** approved records, including any cross-project/repository normalization.
- Verify actual PostgreSQL retraction/revert history and late-arriving revisions; fixture-based tests are not equivalent to real-source acceptance.
- A test or PR reference does not prove a test passed or that a fix works.

### 4. How it fits into VisionIQ
Transforms disconnected tickets and commits into **defensible engineering history**, used later by graph traversal and evidence-backed lesson qualification.

### 5. Testing
Use a known approved ticket/PR example; compare the declared source link to the persisted `evidence.ticket_fix_links` association. Test one missing/unrelated link and one revert/dispute case. Retain both the source proof and the observed outcome in the run report.

**Supporting documentation:** [LRN-04 ticket-fix links](LRN-04-TICKET-FIX-LINKS/README.md).

### 6. Move to LRN-05
A trustworthy link is useful only while it remains current. Next we maintain the history as sources change.

---

## LRN-05 — Historical backfill and incremental synchronization

### 1. What it is
The ongoing synchronization and recovery mechanism for ADO/Confluence engineering evidence.

### 2. What it does
- Runs a historical backfill of approved sources.
- Reads incremental changes, uses checkpoints and reconciles missing/removed source associations.
- Retries transient errors and records partial/quarantined failures without claiming a complete sync.
- Supports Service Hook/webhook processing in the implemented flows; synthetic delivery tests are not proof of a live external webhook.
- Triggers or supports downstream evidence refresh when relevant source revisions change.

### 3. Pending / not tested
- Cloud scheduling, actual provider-side webhook delivery and end-to-end operational recovery may remain open independently of local synchronization logic.
- Verify checkpoint resume, no accidental duplicate links, and new/removed PR associations in the **current** canonical database.
- Do not mark an entire source as successfully synchronized if one stream was skipped or failed.

### 4. How it fits into VisionIQ
Prevents the Engineering Time Machine and search index from turning into a stale snapshot. LRN-12/13 later use PR events and material evidence changes to update the knowledge base.

### 5. Testing
Run full/delta/replay cases with the approved source and a safe destination. Compare revision counts and checkpoints before/after; verify idempotence and explicit partial outcomes. Test webhook unit contracts separately from approved live-hook delivery.

**Supporting documentation:** [LRN-05 sync and backfill](LRN-05-BACKFILL-AND-SYNC/BACKFILL-OVERVIEW.md) and [webhook behavior](LRN-05-BACKFILL-AND-SYNC/SYNC-WEBHOOKS.md).

### 6. Move to LRN-06
We now know **which fixes happened**. Next we inspect the code those commits changed so VisionIQ understands affected files and symbols.

---

## LRN-06 — Code intelligence / repository understanding

### 1. What it is
A **language-agnostic** repository analysis capability that reads approved Git content without executing untrusted project code.

### 2. What it does
- Scans source files at a selected repository/commit and records file identities, imports/dependencies and supported symbols/functions.
- Uses supported parsers with **partial/unsupported** behavior when a language or file cannot be parsed reliably.
- Associates commit-pinned scan evidence with canonical project/repository identities.
- Records parser failures and avoids inventing a symbol simply because a filename or stack frame looks similar.
- Provides structural evidence for the relationship graph and later impact analysis.

### 3. Pending / not tested
- The focused scanner/impact/recovery suite passed for PR #31; still verify **the approved demo checkout** at a pinned PR-associated commit and inspect the generated symbol/dependency report.
- Validate language fallbacks, renames, cross-repository dependency candidates and error recovery. Large repositories/CI-triggered cloud scans need separate evidence.
- Parser support and **semantic correctness** vary by language. A file-level match is not automatically a function-level match.

### 4. How it fits into VisionIQ
It connects ticket/PR history to the **actual code structure**. This is how VisionIQ can answer what changed, which modules are linked and what might need investigation—without pretending to execute or verify the software.

### 5. Testing
Use the read-only operator command below against the **approved local checkout**, with an exact commit and known symbol. Inspect the JSON report and then separately verify persistence into canonical graph evidence.\n\n```powershell\n$env:PYTHONPATH = 'src/VisionIQ.Intelligence'\n.\\.venv\\Scripts\\python.exe -m app.code_intelligence.verify_cli --repo '<approved-local-Git-repo>' --cache '<private-local-cache>' --repository-id '<approved-repository-id>' --head '<commit-sha>' --base '<parent-or-base-sha>' --expect-symbol '<qualified-symbol>' --output artifacts/lrn06/code-proof.json\n```\n\nFocused PR #31 CI passed **32 tests** including parser failure/last-good safety. It does not certify accuracy on the real demo repository. Without successful LRN-06 source evidence, an extended LRN-07 graph-path check may be blocked/skipped.

**Supporting documentation:** [LRN-06 code intelligence](LRN-06-CODE-INTELLIGENCE/ARCHITECTURE.md) and [code-understanding guide](CODE-UNDERSTANDING-GUIDE.md).

### 6. Move to LRN-07
Combine LRN-04 association evidence with LRN-06 code evidence into one navigable Engineering Relationship Graph.

---

## LRN-07 — Evidence-backed Engineering Relationship Graph

### 1. What it is
The persisted and queryable **Engineering Time Machine** relationship graph connecting defects, fixes, commits, repositories, files, symbols, tests and other supported engineering entities.

### 2. What it does
- Materializes typed, source-backed relationships in PostgreSQL `evidence.graph_nodes` and `evidence.graph_assertions`.
- Supports ticket→PR, PR→commit, commit→file, file→symbol/test and other explicitly evidenced relationships.
- Preserves source item/revision provenance and the historical assertion lifecycle.
- Filters outdated/retracted/disputed/reverted/deleted evidence from current navigable results under the store's implemented rules.
- Supports trusted local graph inspection via `scripts/inspect_graph.py` and cross-project scope arguments, subject to approved stored access scopes.
- **Does not** treat a declared test as an executed/passing test or a draft PR as a completed fix.

### 3. Pending / not tested
- A **real-source** ticket→PR→exact commit→file→symbol/test E2E chain remains an acceptance gate, alongside missing-link, rename/revert and cross-project negative cases.
- Database-gated integration tests must be run against an approved migrated PostgreSQL target; skipped tests are not accepted.
- The public Graph HTTP endpoint is **disabled**. Microsoft Entra configuration and per-user graph ACL are **deferred**, not implemented. The newest cleanup made `GraphApiOptions.FromConfiguration()` always return `null`.
- **Current code-review finding:** a historical audit identified graph-auth test drift; do not treat that older finding as a current failing test without a fresh checkout-level run. It does not block the internal graph data model.
- Follow-ups: [authenticated internal GCP Graph API](https://trello.com/c/LctSCimm/58-deferred-integration-lrn-07-authenticated-internal-graph-api-via-google-cloud-iam) and [enterprise identity](https://trello.com/c/NOwMYjsM/56-deferred-auth-visioniq-enterprise-user-authentication-microsoft-entra-id-api-tokens).

### 4. How it fits into VisionIQ
It gives downstream retrieval and Engineering Brief workflows **structural relationships with cited proof**. Search (LRN-08/09) finds potentially relevant history; the graph explains the supported links among those historical items.

### 5. Testing
Run `./scripts/learn-e2e.ps1` → **LRN-01–07** or **individual LRN-07** with canonical evidence. Verify the real PR/commit IDs, persistent current graph assertions, source/revision proof, and negative cases. The **.NET internal traversal** is bounded to three hops; the separate **local inspector** can inspect a four-hop full chain. Do not infer that synthetic passing graph tests prove full real graph coverage.

**Supporting documentation:** [LRN-07 hackathon graph validation](LRN-07-RELATIONSHIP-GRAPH/lrn07-hackathon-graph-validation.md).

### 6. Move to LRN-08
The evidence and relationships are now represented in canonical storage. **LRN-08 makes approved historical evidence searchable** using keyword indexing and Gemini embeddings on Vertex AI.

---

## LRN-08 — Index history for search

**Current status (11 November 2026):** Keyword and vector indexing, published snapshots, and read-only acceptance probes are implemented on `main`. Live approved source-to-Vertex/GCS acceptance is a separate test gate, not proven by a synthetic fixture.

### 1. What it is

LRN-08 is VisionIQ's history indexing layer. It makes approved, versioned Azure DevOps and Confluence evidence searchable by **words** (PostgreSQL full-text) and by **meaning** (Gemini embeddings on Vertex AI stored in pgvector). It is *not* the final Engineering Brief or an AI answer generator.

### 2. What it does

- Reads latest supported canonical source revisions in PostgreSQL and archived raw evidence from private GCS.
- Redacts sensitive text, extracts sections, splits them into repeatable chunks.
- Writes chunk text, item identity, source revision, provenance and source access-scope metadata.
- Computes Vertex AI `gemini-embedding-001` vectors at **768 dimensions**, pins model/dimension and reuses unchanged embeddings.
- Maintains full-text and pgvector indexes; publishes a checked snapshot to keep incomplete/index-in-progress rows out of search.
- Skips deleted/unverified-scope items, and supports incremental re-indexing.

**Boundary:** Indexed access-scope metadata is not a replacement for live end-user permission checks. That integration is deferred. Do not use an unapproved sensitive corpus or expose this data in a public API.

### 3. Pending / not tested

- **Live approved-history demonstration:** one real work item should be returned by both keyword and Vertex-vector search.
- Vertex AI model access, latency and cost must be measured from a configured developer account. Offline hashing is a functional fixture only, not a semantic acceptance result.
- Real source scope, indexing throughput, reindex/replay, stale snapshot and missing-GCS/error cases need end-to-end evidence.
- Scheduled Cloud Run indexing jobs and operational alerting are separate cloud deployment tasks.
- LRN-09 handles hybrid result fusion, evidence-aware ranking, citations and abstention (full graph-neighbor traversal is deferred); **do not claim** LRN-08 delivers the full Engineering Brief.

### 4. How it fits into VisionIQ

```text
LRN-01/02 source history
       |
LRN-03 canonical revisions + private GCS
       |
LRN-05 keeps records current
       |
LRN-08 redacted chunking + keyword index + Vertex vectors
       |
LRN-09 keyword/vector/code-path retrieval
       |
Evidence-backed Engineering Brief and lessons
```

### 5. Testing

**Configuration in private `.env`:**

- Canonical local PostgreSQL: `DB_HOST`, `DB_PORT`, `DB_NAME`, `DB_INTELLIGENCE_USER` and `DB_INTELLIGENCE_PASSWORD`.
- Private raw archive bucket: `GCS_EVIDENCE_BUCKET` and approved Application Default Credentials.
- Vertex: `EMBEDDING_PROVIDER=vertex`, `GCP_PROJECT_ID` or `VERTEX_PROJECT`, `VERTEX_LOCATION` (model-supported location), `EMBEDDING_MODEL=gemini-embedding-001`, `EMBEDDING_DIMENSION=768`.
- The LEARN runner needs `INGESTION_OUTPUT_MODE=canonical` and an explicit confirmation before knowledge writes.

Run from the repository root:

```powershell
# Validate approved ADC, configured model, 768-vector dimension and simple semantics.
$env:PYTHONPATH = 'src/VisionIQ.Intelligence'
python -m app.knowledge.vertex_check

# The single interactive canonical E2E runner:
.\scripts\learn-e2e.ps1
```

Choose **individual LRN-08** or **LRN-08–13** with an approved ADO item and a canonical test DB/GCS. Where configured, the runner executes the index CLI and its verification stage; inspect actual PASS/FAIL/SKIP/BLOCKED outcomes rather than assuming a menu selection proves every feature. The probe derives its query from an **already redacted indexed chunk** and does not write query/source text to the evidence report. A wrong-scope negative check must return no selected item.

The search probe uses the configured embedding provider. A hashing-provider PASS is **not** a real Vertex acceptance PASS; the runner reports that distinction. Because its query is copied from the indexed item, this is an **index-path sanity check**, not a labelled Recall@10/Precision@5 quality benchmark.

**Direct read-only probe for troubleshooting** (requires an already-published snapshot):

```powershell
$env:PYTHONPATH = 'src/VisionIQ.Intelligence'
python scripts/learn_e2e_stages.py probe-index --item-key '<canonical-item-key>' --output '<gitignored-test-result>.json'
```

The probe reports boolean keyword/vector/scope checks, hit counts and the model identifier. It does not print or persist raw source material. Never pass secrets as CLI arguments.

**Acceptance:** keep LRN-08 **To be Tested** until real Vertex + canonical PostgreSQL/GCS and an approved source-grounded query pass, with logs and an explicit negative scope test.

### 6. Move to LRN-09

LRN-09 consumes **published** keyword/vector/code-path candidates, reranks with evidence and counterevidence, returns citations, and abstains when support is weak. Full graph-neighbor and cross-project traversal are deferred. Its live end-user authorization remains deferred and must not be marked passed by the LRN-08 internal probe.

**Existing detailed references:** [Search index](../PLANNED-FEATURES/LRN-08-HYBRID-RETRIEVAL/SEARCH-INDEX.md) · [Vertex configuration](../OPERATIONS-AND-INFRASTRUCTURE/VERTEX-AI-SETUP.md) · [LRN-08 Trello](https://trello.com/c/eKiuV1Pl/14-learn-lrn-08-index-history-for-search).

---



## LRN-09 — Find the right history for a ticket

### 1. What it is
The evidence-aware retrieval engine for a current defect/ticket, across approved **published** historical knowledge.

### 2. What it does
- Fuses keyword search, Gemini/Vertex vector results and code-path evidence; deduplicates/reranks candidates with traceable reasons.
- Returns source-scoped snippets and citations; flags stale evidence, reverted fixes and contradictory outcomes.
- Abstains on vague or weakly supported queries instead of fabricating a fix.
- Uses stored access scopes; it does **not** implement live delegated user authorization or full cross-project graph traversal.

### 3. Pending / not tested
- Four-case preliminary benchmark had incomplete independent ground-truth confirmation; do not claim a robust Recall/Precision benchmark.
- Live real-ticket relevance, code-path ranking, latency, and end-to-end BRIEF handoff remain acceptance gates.
- Retrieval cache and comprehensive graph-neighbor search deliberately deferred.

### 4. How it fits into VisionIQ
Turns LRN-08 published chunks and LRN-04/06/07 evidence into a **traceable shortlist** for Engineering Brief—not a confirmed repair.

### 5. Testing
Run focused retrieval tests and the read-only `app.knowledge.lrn09_demo` / `app.knowledge.lrn09_benchmark` commands with approved source scopes. Inspect actual cited tickets and revert warnings. [Retrieval design](../PLANNED-FEATURES/LRN-08-HYBRID-RETRIEVAL/retrieval.md) · [PR #28](https://github.com/sandeshshinde284-bit/visionIQ/pull/28).

### 6. Move to LRN-10
Use defensible historical fixes to identify repeated problems—without confusing a shared file with a shared root cause.

---

## LRN-10 — Turn repeated failures into lessons

### 1. What it is
Deterministic, evidence-qualified candidate lesson consolidation from multiple independently fixed defects.

### 2. What it does
- Requires explicit undisputed ticket-to-fix links, distinct defects, overlapping code changes **and corroborating symptoms** from the current approved, published redacted source index.
- Avoids duplicates, preserves append-only membership/revisions, and flags ambiguity for human review.
- Keeps generated common causes **unverified**; a later revert disputes the lesson and retains history.
- PR #27 repaired human-review/ambiguity audit paths; PR #29 strengthened symptom corroboration.

### 3. Pending / not tested
- Real historical false-match/false-negative inspection on approved tickets; synthetic convergence is not real causal equivalence.
- Human-review and disputed-history behavior require the actual migrated database and source data in the acceptance run.
- Full semantic causal clustering and automatic verified root-cause inference are out of scope.

### 4. How it fits into VisionIQ
Provides reusable, cited engineering cautions that can supplement LRN-09 retrieval and BRIEF.

### 5. Testing
Focused CI passed unit and disposable PostgreSQL regression suites, including published-index qualification. On real evidence verify matching symptoms, unrelated same-file defects remaining ungrouped, candidate status, idempotence and revert. [Lesson design and limits](../PLANNED-FEATURES/LRN-10-EVIDENCE-BACKED-LESSONS/lessons.md).

### 6. Move to LRN-11
Prove lessons and their source material can be trusted despite revocation, reversions or outages.

---

## LRN-11 — Prove the knowledge base can be trusted

### 1. What it is
Readiness and safety acceptance checks around canonical knowledge, snapshots and source provenance.

### 2. What it does
- Tests revoked items cannot be read through supported canonical/retrieval paths.
- Tests two PR fixes with one reverted remain visible without a fabricated verified cause.
- Tests last-good/fail-closed behavior during connector or indexing failure; checks malicious source text and interrupted reindexing.
- Produces a fixture/snapshot/assertion result artifact from the readiness suite.

### 3. Pending / not tested
- Five focused isolated readiness database tests passed, but this does not establish live enterprise authorization.
- Full shared snapshot regression and real source outage/revocation validation remain separate gates.

### 4. How it fits into VisionIQ
Prevents the Engineering Brief from citing untrusted, inaccessible, incomplete or superseded evidence.

### 5. Testing
Run `test_kb_readiness_db.py` only through the **disposable PostgreSQL** test runner, inspect `kb-readiness-results.json`, then perform an approved read-only cloud acceptance. [Knowledge readiness](LRN-11-KB-READINESS/README.md).

### 6. Move to LRN-12
Keep PR histories accurate as reviews and source status evolve.

---

## LRN-12 — Every PR event updates evidence

### 1. What it is
ADO PR lifecycle ingestion and reviewer evidence preservation.

### 2. What it does
- Parses PR created/updated/comment events, fetches the latest authoritative PR state, tracks draft/review/merge/abandon outcomes.
- Deduplicates webhook delivery via durable inbox, retries failures and records canonical versioned PR/reviewer evidence.
- PR #30 shares one reviewer-history projection between webhook and periodic sync; no automatic root-cause verification.

### 3. Pending / not tested
- **The webhook host is packaged but not deployed.** External ADO delivery needs an approved HTTPS ingress/auth decision and real Service Hook tests.
- For the manual hackathon, ingestion/reconciliation may be operator-triggered; do not call it live event-driven continuous learning.
- Do not open the existing private API/Intelligence services to anonymous traffic to get webhook delivery.

### 4. How it fits into VisionIQ
Maintains PR and reviewer facts that LRN-04/10/13 use to evaluate evolving fix evidence.

### 5. Testing
Focused .NET CI and webhook HTTP tests passed in PR #30. Verify live events separately if choosing to deploy the dedicated host. [Secure cloud webhook runbook](../OPERATIONS-AND-INFRASTRUCTURE/LRN12-WEBHOOK-CLOUD-DEPLOYMENT.md).

### 6. Move to LRN-13
Refresh affected indexed evidence and revise lessons only when supported evidence changes.

---

## LRN-13 — Lessons change only on proof (manual MVP)

### 1. What it is
A repeatable, guarded **manual** PR evidence refresh and evidence-based lesson history workflow.

### 2. What it does
- Accepts explicit, approved canonical PR/work-item item keys; reindexes only those items and reuses unchanged embeddings.
- Publishes a **checked snapshot** before lesson consolidation; failed indexing/rejected publication prevents the lesson update.
- Revises lessons on material qualifying evidence only; stores prior candidate/disputed history and change-event **database rows**.
- PR #32 added an operator CLI and structured JSON evidence report for before/after snapshots, lesson revisions and idempotency.

### 3. Pending / not tested
- Focused unit and isolated synthetic fix→revert tests passed; a **real historic ADO fix and actual revert SHA** still need approved manual evidence replay.
- The current consolidator evaluates all qualifying observations, not a production-grade targeted pattern queue.
- **Explicit deferrals:** automatic PR-event-to-index job, Cloud Scheduler and Pub/Sub `knowledge.changed` delivery. A database event record is **not** proof of Pub/Sub delivery.

### 4. How it fits into VisionIQ
Shows that VisionIQ learns conservatively from new **proof and counterevidence**, without rewriting history or claiming a verified root cause.

### 5. Testing
After trusted ingestion and an initial full snapshot, run on approved canonical keys:

```powershell
$env:PYTHONPATH = 'src/VisionIQ.Intelligence'
.\\.venv\\Scripts\\python.exe -m app.knowledge.lrn13_manual_cli --item-key '<64-character-canonical-PR-key>' --output artifacts/lrn13/manual-fix.json
```

After ingesting the **actual** revert, repeat with `--output artifacts/lrn13/manual-revert.json`. Check candidate→disputed, two revisions, same original identity, no fabricated cause and zero new revisions on an unchanged third run. Never use demo production data with destructive test fixtures. [Full manual acceptance runbook](LRN-13-MANUAL-LEARNING-ACCEPTANCE.md).

### 6. Move to integrated acceptance
Integrate the approved BRIEF/DEFECT changes, run LRN-01–13 as one **manual** source→evidence→search→Brief→defect workflow and then verify the approved Cloud Run/Cloud SQL/GCS deployment.

---

## One manual hackathon acceptance path

1. Verify the exact approved ADO/Git source scope and current canonical migrations/permissions (LRN-01–05).
2. Run [LRN-06 repository verification](CODE-UNDERSTANDING-GUIDE.md) against the selected commit and inspect the JSON symbols, dependencies and diff impacts.
3. Confirm the LRN-07 graph chain; run LRN-08 published index checks using **real Gemini embeddings** and check LRN-09 cited results and a weak-evidence abstention.
4. Validate a candidate LRN-10 lesson from at least two independently fixed, symptom-corroborated defects; execute the LRN-11 safety checks on an isolated database.
5. After manual ingest of the real historical fix and revert, run LRN-13 twice and confirm revision history plus a third idempotent run.
6. Integrate and run the actual BRIEF and DEFECT implementation in the **same** approved environment; inspect grounded citations, safety gates and final outputs.
7. Record PASS/FAIL/SKIP/BLOCKED honestly; retain local JSON reports, published snapshot IDs, commit IDs and sanitized logs. **Code merged ≠ cloud acceptance.**

## Documentation policy

- **This handbook** is the single current six-section LEARN summary.
- **Detailed story guides** remain under their story directories for operational commands, data contracts, architecture and dated evidence. Historical reports are evidence, not current acceptance claims.
- **[LEARN index](README.md)** provides curated entry points; **[operations index](../OPERATIONS-AND-INFRASTRUCTURE/README.md)** covers cloud/manual deployment; **[review archive](../REVIEW-AND-AUDIT/README.md)** preserves the audit trail.
- Do not create a second LRN-01–13 summary. Do not remove source-of-truth diagrams, specs, troubleshooting guides or archived decisions merely to reduce file count.
