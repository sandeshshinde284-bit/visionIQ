"""LRN-06 read-only proof of code coverage, dependencies and diff-to-symbol impact.

Does not execute source code, write canonical evidence, or contact ADO/GCP.
Run against an approved local Git checkout and pinned commits; this is acceptance
evidence, not a claim of a verified call graph or runtime dependency.
"""

import argparse
import json
import subprocess
import sys
from dataclasses import asdict
from pathlib import Path

from app.code_intelligence import (
    ApprovedRepository,
    default_parser_registry,
    map_diff_to_symbols,
    resolve_dependencies,
    scan_repository,
    scan_with_recovery,
)


def _git(repo: Path, *args: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(repo), *args],
        capture_output=True,
        text=True,
        check=False,
        timeout=60,
    )
    if result.returncode:
        raise ValueError("The requested committed Git revision could not be read")
    return result.stdout.strip()


def report(
    repo: Path,
    cache: Path,
    repo_id: str,
    head: str | None = None,
    base: str | None = None,
    expect_symbols: tuple[str, ...] = (),
) -> tuple[dict, int]:
    if not repo_id.strip():
        raise ValueError("An approved repository identifier is required")
    registry = default_parser_registry()
    # A historical scan does not update the cache for the current HEAD.
    scan = (
        scan_repository(repo, registry=registry, commit=head)
        if head
        else scan_with_recovery(repo, cache, registry)
    )
    analyses = {a.path: a for a in scan.analyses}
    parsed = [a for a in scan.analyses if a.symbol_analysis == "AVAILABLE"]
    unavailable = [a.path for a in scan.analyses if a.symbol_analysis == "UNAVAILABLE"]
    failed = [a.path for a in scan.analyses if a.symbol_analysis == "FAILED"]

    def committed_blob(path: str) -> str:
        # Only files from the committed, already-inventoried tree are eligible.
        if path not in {f.path for f in scan.files}:
            raise ValueError("Requested manifest is not part of the scanned commit")
        raw = subprocess.run(
            ["git", "-C", scan.repo_root, "show", f"{scan.commit_sha}:{path}"],
            capture_output=True,
            check=False,
            timeout=60,
        )
        if raw.returncode:
            raise ValueError("Committed manifest is unavailable")
        return raw.stdout.decode("utf-8")

    dependencies = resolve_dependencies(
        ApprovedRepository(repo_id, "", scan),
        read_blob=committed_blob,
    )
    impacts = ()
    if base:
        resolved_base = _git(repo, "rev-parse", "--verify", f"{base}^{{commit}}")
        impacts = map_diff_to_symbols(repo, resolved_base, scan)
    known = {
        symbol.qualified_name or symbol.name for analysis in parsed for symbol in analysis.symbols
    }
    missing = sorted(set(expect_symbols) - known)
    evidence = {
        "story": "LRN-06",
        "repository_id": repo_id,
        "commit": scan.commit_sha,
        "base_commit": impacts[0].source_commit
        if impacts
        else (_git(repo, "rev-parse", "--verify", f"{base}^{{commit}}") if base else None),
        "status": scan.status,
        "last_good_commit": scan.last_good.commit_sha if scan.last_good else None,
        "counts": {
            "files": len(scan.files),
            "parsed_files": len(parsed),
            "file_only": len(unavailable),
            "failed_files": len(failed),
            "symbols": len(known),
            "resolved_dependencies": sum(d.status == "RESOLVED_EXPLICIT" for d in dependencies),
            "declared_cross_repo_dependencies": sum(
                d.kind == "DECLARED_GIT_DEPENDENCY" for d in dependencies
            ),
            "impacted_files": len(impacts),
            "impacted_symbols": sum(bool(i.symbols) for i in impacts),
        },
        "files": [
            {"path": path, "analysis": a.symbol_analysis, "symbols": [asdict(s) for s in a.symbols]}
            for path, a in sorted(analyses.items())
            if a.symbol_analysis != "UNAVAILABLE"
        ],
        "file_only_paths": sorted(unavailable),
        "failed_paths": sorted(failed),
        "dependencies": [asdict(d) for d in dependencies],
        "diff_impacts": [asdict(d) for d in impacts],
        "missing_expected_symbols": missing,
        "limitations": [
            "No parser means file-level evidence, not invented symbols.",
            "Imports and lexical calls are not verified runtime call graphs.",
            "Cross-repository dependencies require separately approved sources.",
        ],
    }
    return evidence, 2 if scan.status != "COMPLETE" else 3 if missing else 0


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description="LRN-06 commit-pinned read-only acceptance evidence")
    p.add_argument("--repo", required=True, type=Path)
    p.add_argument("--cache", required=True, type=Path)
    p.add_argument("--repository-id", required=True)
    p.add_argument("--head", help="Exact committed SHA/ref; does not change last-good cache")
    p.add_argument("--base", help="Committed base SHA/ref, to report changed-file symbol impact")
    p.add_argument(
        "--expect-symbol",
        action="append",
        default=[],
        help="Declared fully qualified symbol that must exist (repeatable)",
    )
    p.add_argument("--output", required=True, type=Path, help="Local JSON report path")
    args = p.parse_args(argv)
    try:
        evidence, code = report(
            args.repo,
            args.cache,
            args.repository_id,
            args.head,
            args.base,
            tuple(args.expect_symbol),
        )
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(evidence, indent=2), encoding="utf-8")
        print(
            json.dumps(
                {
                    "status": evidence["status"],
                    "commit": evidence["commit"],
                    "counts": evidence["counts"],
                    "report": str(args.output),
                    "missing_expected_symbols": evidence["missing_expected_symbols"],
                }
            )
        )
        return code
    except (OSError, ValueError, subprocess.TimeoutExpired) as error:
        print(f"LRN-06 verification blocked: {type(error).__name__}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
