# Review & Audit Trail

**Purpose:** Document all design decisions, findings, and review outcomes

This folder contains:
- Design decisions with rationale
- Code review findings and fixes
- Integration review results by quarter
- Audit trail for traceability

---

## Contents

### Decision Log
[DECISION-LOG.md](DECISION-LOG.md) — **Central location** for all major decisions across the project, with rationale and impact.

### 2026 Q4 Review
[2026-Q4/](2026-Q4/)

Latest integration review (November 2026):
- [INTEGRATION-REVIEW.md](2026-Q4/INTEGRATION-REVIEW.md) — Latest findings and corrections
- [BLOCKER-FIXES-B01-B17.md](2026-Q4/BLOCKER-FIXES-B01-B17.md) — Critical findings resolved
- [INTEGRATION-CLOSURE.md](2026-Q4/INTEGRATION-CLOSURE.md) — Integration summary
- [ACCEPTED-AS-IS.md](2026-Q4/) — Features accepted without changes

### Historical Reviews
[HISTORICAL-REVIEWS/](HISTORICAL-REVIEWS/) — Earlier review rounds (archived)

---

## Key Findings

### Q4 2026 Blockers (B-1 to B-17)
All critical findings identified and resolved.

**Topics:**
- ADO evidence access model
- Confluence page ACL enforcement
- Scanner database role isolation
- Content revision hashing
- Backfill counting

See [BLOCKER-FIXES-B01-B17.md](2026-Q4/BLOCKER-FIXES-B01-B17.md)

### Q4 2026 Medium Findings (M-1 to M-16)
Design decisions that don't block but improve reliability.

### Q4 2026 Minor Findings (m-1 to m-12)
Small improvements and cleanups.

---

## Design Decision Topics

### Evidence & Access Control
- ADO project-level access (not area-path or repository level yet)
- Confluence live permission re-check
- Content-hash based revisions

### Linking & Reverts
- Commit-message mentions = explicit (not proof of fix)
- Title-matched revert candidates (not confirmed)
- Retraction lifecycle tracking

### Sync & Currency
- Checkpoint semantics
- Webhook replay handling
- Reconciliation strategies

See [DECISION-LOG.md](DECISION-LOG.md) for all decisions.

---

## Audit Trail

Use this folder to trace:
- **"Why was decision X made?"** → Check DECISION-LOG.md
- **"What was found in review Y?"** → Check review folder
- **"What was the blocker Z about?"** → Check BLOCKER-FIXES

---

## Status

All findings are resolved and documented.  
Design decisions are traceable and rationale preserved.
