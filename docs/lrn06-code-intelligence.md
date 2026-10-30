# LRN-06 — Understand the Code

**Status:** Implementation in progress; not yet accepted or merged. Draft PR #3.
**Scope:** S06-03/04/05/06/07/08/10/13. No Roslyn and no standalone UI.

## Implemented modules

| Task | Code | Demonstration / test |
| --- | --- | --- |
| S06-03 | `scanner.py` | Commit-pinned `git ls-tree` file inventory, blob SHA, language and manifest hints |
| S06-04 | `adapters.py` | Typed Python Protocol, parser name/version/capabilities, fake parser contract tests |
| S06-05 | `scanner.py` | Unknown language remains file-level `UNAVAILABLE`; no invented symbols |
| S06-06 | `parsers.py` | Python native `ast`; C# pinned Tree-sitter grammar; golden symbol fixtures |
| S06-07 | `relationships.py` | Proven explicit Python import and C# project-reference links; unsupported/ambiguous targets unresolved |
| S06-08 | `impact.py` | Git revision comparison and exact changed-new-line overlap with declared symbols; otherwise `FILE_ONLY` |
| S06-10 | `relationships.py` | Cross-project C# project references; cross-repository links only for an explicit Git URL declaration matching an allowlisted repository |
| S06-13 | `snapshots.py` | Persistent, atomic last-good metadata snapshots; parser-crash `PARTIAL` and restart recovery tests |

The implementation reads committed Git content, never executes repository code
and does not obtain source tokens or cloud secrets. It avoids the teammate's
FND-05 event definitions, the separate LRN-01 source connectors, FND-04 cloud
deployment, and database ownership decisions within LRN-03.

## Important accuracy limits

- AST and Tree-sitter expose structural evidence, not a verified call graph,
  runtime invocation, causality, or a complete blast radius.
- Cross-repository references are only `DECLARED_TARGET_REPOSITORY`. They
  do not prove runtime integration, change impact, or a ticket's root cause.
  Matching names without an explicit declared Git URL are deliberately ignored.
- The C# parser supports C# *syntax*; JavaScript, Vue, Java and other languages
  still receive file-level fallback until their adapters are reviewed.
- The scanner cannot access unapproved repositories; submodule contents and
  symlink targets are not traversed.
- The local `snapshots.py` cache is an interim evidence metadata store, not
  the final ACL-scoped `LRN-03` canonical PostgreSQL/Cloud Storage store.
- `scan_repository` still defaults to no parser; callers opt in with
  `default_parser_registry()`. This keeps source of truth explicit.

## Example local usage

```python
from app.code_intelligence import default_parser_registry, scan_with_recovery

scan = scan_with_recovery(
    "C:/approved/demo/repo",
    "C:/approved/visioniq-private-cache",
    default_parser_registry(),
)
print(scan.status, scan.commit_sha, len(scan.files))
```

When scanning fails, `scan.last_good` contains the previous complete metadata
snapshot if one exists for the same local repo identity. No source code or
passwords are stored in this cache, but paths and filenames may still be
sensitive: keep it outside the repository with appropriate filesystem ACL.

## Validation commands

```powershell
python -m pytest tests/intelligence/test_code_scanner.py tests/intelligence/test_code_intelligence_integration.py -q
python -m ruff check .
python -m ruff format --check .
```

## Remaining integration and acceptance evidence

1. Confirm decision D03: the *approved* demo repo and its actual language mix.
   Python and C# parsers were selected because both languages exist in the
   VisionIQ application, not because the external demo repo is known.
2. Run the scanner on the approved demo repo and review symbols and explicit
   dependencies with source maintainers; save a verified golden fixture.
3. Agree with LRN-03 on persistence of symbol spans, ACL visibility,
   provenance IDs, file revision identities and durable last-good state.
   Existing local cache should be replaced or wrapped by that store.
4. Integrate Git source identities and commit/checkpoint metadata from LRN-01;
   maintain strict access control for cross-project source references.
5. Verify parser crashes and recovery with an actual persisted integration
   store; measure memory and time budgets against approved larger repositories.
6. Run full CI plus authorized acceptance demonstrations, then request review.

**Do not close LRN-06 until these acceptance conditions pass.**
