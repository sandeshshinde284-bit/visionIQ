# LRN-06 — Language-neutral code intelligence (initial slice)

**Status: in progress. This document does not claim LRN-06 completion.**

This branch deliberately avoids the LRN-01 Azure DevOps/history connectors,
teammate-owned FND-05 event contracts, FND-04 deployment configuration, and
LRN-03 persistence/schema migrations.

## Implemented

- **S06-03 — Read-only Git repository scanner:** reads an immutable committed
  Git tree with `git ls-tree`, records commit SHA, file paths, Git blob SHAs,
  language hints and manifest hints. Never executes repository code.
- **S06-04 — Typed parser-adapter contract:** explicit registry, version and
  capabilities for each reviewed language adapter, with structures for symbols,
  imports, calls and diagnostics.
- **S06-05 — Honest file-level fallback:** every file is represented, even when
  no approved parser exists; `symbol_analysis=UNAVAILABLE` and zero invented
  symbols. Multiple languages can coexist in the same repository.
- **S06-13 — Parser failure isolation (initial unit-level coverage):** a
  crashing adapter marks its file `FAILED` and scan `PARTIAL`, and the
  previous complete snapshot is exposed as `last_good` without replacing it.
  This is an in-memory hand-off only, not a persisted last-good guarantee.

## Next / deliberately not implemented

- S06-06: tree-sitter or another reviewed parser **for the selected demo-repo
  language** (D03 must be confirmed). No Roslyn and no universal language claim.
- S06-07: verified dependency relationships, not inferred call-graph certainty.
- S06-08: diff-hunk-to-symbol mapping with line-span confidence.
- S06-10: cross-project dependency references tied to traceable evidence.
- S06-13 remaining: persistence-backed last-good recovery and restart tests
  once the LRN-03 evidence schema is agreed.

Do not store scanner output in new tables until LRN-03 ownership and
provenance/ACL schema are finalized. Do not import ADO tickets from this module.

## Run tests

```powershell
python -m pytest tests/intelligence/test_code_scanner.py -q
python -m ruff check src/VisionIQ.Intelligence/app/code_intelligence tests/intelligence/test_code_scanner.py
python -m ruff format --check src/VisionIQ.Intelligence/app/code_intelligence tests/intelligence/test_code_scanner.py
```

## Integration contract

```python
from app.code_intelligence import ParserRegistry, scan_repository

registry = ParserRegistry()  # no parser implicitly registered
snapshot = scan_repository("C:/path/to/approved/local/git/repo", registry)
assert snapshot.status in {"COMPLETE", "PARTIAL"}
```

`RepositoryScan` records paths, content blob IDs, commit ID and explicit
`FileAnalysis` status. It never uploads source code, reads cloud secrets, or
uses the deployment identity. Correlation with LRN-01 ticket/commit evidence
comes later via immutable Git SHA and the canonical source identifiers in
LRN-03. Do not identify a commit as fixed merely because a file was touched.

**Security:** Approved source repositories only. Git must be available
locally; the scanner executes Git commands with argument arrays (no shell).
The module intentionally excludes symlinks and submodule contents; each
submodule is a separate repository that needs explicit authorization.
