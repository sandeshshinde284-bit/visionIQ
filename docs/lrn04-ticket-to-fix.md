# LRN-04 — Ticket-to-fix provenance and reviewed outcomes

**Status:** code under review; real-source acceptance OPEN.  
**PR:** #6; branch `feature/lrn04-ticket-fix-provenance`.

## Official story

A past defect must show which PRs and commits were explicitly linked to it
and whether a later change reverted the fix. Time proximity cannot establish
a link. Each association must remain distinguishable as explicit, inferred,
verified, disputed or unknown, including multiple fixes and reverts.

## Implemented source-based relationship projection

The .NET 10 `TicketFixResolver` consumes Azure DevOps source work item / PR /
commit records produced by LRN-01. It generates deterministic, project/repo
scoped candidates from explicit ADO PR work-item and PR commit references,
as well as direct commit-message ticket references.

`Explicit` means the source references the ticket: it is **not** a verified
fix, deployment outcome, successful regression test, or causal proof.

An explicit revert is attached only if a commit message names a uniquely
resolvable prior commit SHA. Ambiguous SHA prefixes are not guessed.
Each ticket can retain multiple PRs and commits, and later reverts remain
visible rather than overwriting history. Inferred proposals are separately
classified, not automatically promoted.

## PostgreSQL persistence

Migration `20261030_0004` adds:
- `evidence.ticket_fix_links`: source scope, ID and evidence metadata,
  source classification, reviewed status, reverts and observation timestamps.
- `evidence.ticket_fix_reviews`: immutable review reference/status decisions,
  unique per association and review reference; no destructive downgrade.

`PostgresTicketFixLinkStore` serializes conflicting review transitions,
preserves prior verdicts during reimport, and rejects conflicting decision
replays. It does not expose source data over HTTP. All writes use the limited
.NET API database runtime role, never migration/admin credentials.

No free-text ticket bodies, credentials or source titles are included in
link/revert database rows.

## CI and acceptance

- Unit tests verify five synthetic explicit ticket/fix cases, multiple fixes,
  direct mention, cross-project/repo isolation, short-SHA ambiguity and reverts.
- PR CI verifies the Alembic 0004 migration, fresh-volume migration rerun,
  and opt-in real PostgreSQL idempotency/review-audit behavior.
- **Still required:** five **approved real** source tickets reviewed by the
  team for correctness or logged disputes; source-revision and current-ACL
  enforcement from LRN-03; end-to-end graph indexing via LRN-07; approved
  evidence refs linking stored source claims to reviewed fix decisions.

Do not call an observed link a proven bug fix without independent verification.
Do not mark LRN-04 Done before the real-data acceptance demonstration.
