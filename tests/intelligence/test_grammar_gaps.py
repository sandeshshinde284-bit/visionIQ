"""N-16: a grammar gap degrades one file to file-level evidence.

Real parser crashes still fail the scan (PARTIAL, last good scan kept).
"""

import os
import subprocess
from datetime import UTC, datetime
from pathlib import Path

import pytest

from app.code_intelligence.adapters import ParserResult
from app.code_intelligence.canonical import ScanEvidenceIdentity, build_scan_evidence
from app.code_intelligence.graph import project_scan_edges
from app.code_intelligence.parsers import default_parser_registry
from app.code_intelligence.scanner import ParserRegistry, scan_repository

pytest.importorskip("tree_sitter_c_sharp")

GOOD_CS = "namespace Shop;\npublic class Cart { public void Add() {} }\n"
FIELD_CS = (
    "public class Bad {\n"
    "    string[] F(string field) { return string.IsNullOrEmpty(field) ? [] : [field]; }\n"
    "}\n"
)
ENV = {
    "GIT_AUTHOR_NAME": "t",
    "GIT_AUTHOR_EMAIL": "t@t",
    "GIT_COMMITTER_NAME": "t",
    "GIT_COMMITTER_EMAIL": "t@t",
}


def _repo(tmp_path: Path, files: dict[str, str]) -> Path:
    root = tmp_path / "repo"
    for name, text in files.items():
        (root / name).parent.mkdir(parents=True, exist_ok=True)
        (root / name).write_text(text, encoding="utf-8")
    for args in (["init", "-q"], ["add", "."], ["commit", "-qm", "init"]):
        subprocess.run(["git", "-C", str(root), *args], check=True, env={**os.environ, **ENV})
    return root


def _scan(tmp_path: Path, files: dict[str, str], registry: ParserRegistry | None = None):
    return scan_repository(_repo(tmp_path, files), registry or default_parser_registry())


def _by_path(scan) -> dict:
    return {analysis.path: analysis for analysis in scan.analyses}


def test_a_csharp_grammar_gap_keeps_the_repository_complete(tmp_path: Path) -> None:
    scan = _scan(tmp_path, {"Cart.cs": GOOD_CS, "Bad.cs": FIELD_CS})

    analyses = _by_path(scan)
    assert scan.status == "COMPLETE"
    assert analyses["Cart.cs"].symbol_analysis == "AVAILABLE"
    assert analyses["Bad.cs"].symbol_analysis == "UNAVAILABLE"
    expected = "C# grammar could not fully parse this file (first problem at line 2)"
    assert expected in analyses["Bad.cs"].diagnostics[0]


def test_a_python_syntax_error_is_a_grammar_gap_not_a_crash(tmp_path: Path) -> None:
    scan = _scan(tmp_path, {"ok.py": "x = 1\n", "old.py": "print 'python 2'\n"})

    analysis = _by_path(scan)["old.py"]
    assert scan.status == "COMPLETE"
    assert analysis.symbol_analysis == "UNAVAILABLE"
    assert analysis.diagnostics[0].startswith("Python grammar could not fully parse")


class _CrashingAdapter:
    name = "crashing"
    version = "1"
    extensions = (".cs",)

    def parse(self, path: str, content: str) -> ParserResult:
        raise RuntimeError("parser bug")


def test_a_real_parser_crash_still_marks_the_scan_partial(tmp_path: Path) -> None:
    registry = ParserRegistry()
    registry.register(_CrashingAdapter())

    scan = _scan(tmp_path, {"Cart.cs": GOOD_CS}, registry)

    assert scan.status == "PARTIAL"
    assert _by_path(scan)["Cart.cs"].symbol_analysis == "FAILED"


def test_systematic_grammar_gaps_mark_the_scan_partial(tmp_path: Path) -> None:
    files = {f"Bad{i}.cs": FIELD_CS for i in range(3)} | {"Cart.cs": GOOD_CS}

    scan = _scan(tmp_path, files)

    assert scan.status == "PARTIAL"
    assert any("Grammar could not fully parse 3 of 4" in error for error in scan.errors)


def test_a_few_gaps_below_the_minimum_do_not_fail_a_small_repository(tmp_path: Path) -> None:
    assert _scan(tmp_path, {"Bad1.cs": FIELD_CS, "Bad2.cs": FIELD_CS}).status == "COMPLETE"


def test_a_degraded_file_keeps_its_graph_node_without_symbols(tmp_path: Path) -> None:
    scan = _scan(tmp_path, {"Cart.cs": GOOD_CS, "Bad.cs": FIELD_CS})
    url = "https://example.com/r.git"
    evidence = build_scan_evidence(
        scan, ScanEvidenceIdentity(url, "p", "r"), url, datetime.now(UTC)
    )

    edges = project_scan_edges(scan, evidence)
    symbol_edges = [e for e in edges if e.kind in ("file_symbol", "file_test")]

    assert any(e.kind == "commit_file" and e.target.native_id == "Bad.cs" for e in edges)
    assert not any(e.source.native_id == "Bad.cs" for e in symbol_edges)
    assert any(e.source.native_id == "Cart.cs" for e in symbol_edges)


def test_the_adapter_itself_still_rejects_incomplete_parses() -> None:
    # The B-1 self-check calls adapters directly, so VisionIQ's own code must still parse fully.
    from app.code_intelligence.parsers import CSharpTreeSitterAdapter

    with pytest.raises(ValueError, match="could not fully parse"):
        CSharpTreeSitterAdapter().parse("Bad.cs", FIELD_CS)


# Review R6-01: a deeply nested or generated file used to raise RecursionError in the recursive tree
# walk, which marked the whole repository PARTIAL, and build_scan_evidence then refused every later
# scan until that file changed. It is now a per-file downgrade, like a grammar gap.
DEEP_CS = "public class Gen { void M(bool b) {\n" + "if (b) {\n" * 600 + "}\n" * 600 + "} }\n"
DEEP_PY = "x = " + "-" * 3000 + "1\n"


def test_a_deeply_nested_file_degrades_only_that_file(tmp_path: Path) -> None:
    scan = _scan(
        tmp_path,
        {
            "Cart.cs": GOOD_CS,
            "Gen.cs": DEEP_CS,
            "deep.py": DEEP_PY,
            "ok.py": "def f():\n    return 1\n",
        },
    )

    analyses = _by_path(scan)
    assert scan.status == "COMPLETE", scan.errors
    assert analyses["Cart.cs"].symbol_analysis == "AVAILABLE"
    assert analyses["ok.py"].symbol_analysis == "AVAILABLE"
    for path in ("Gen.cs", "deep.py"):
        assert analyses[path].symbol_analysis == "UNAVAILABLE"
        assert "depth limit" in analyses[path].diagnostics[0]
    evidence = build_scan_evidence(
        scan,
        ScanEvidenceIdentity("https://github.com/approved/deep", "project-one", "repo-one"),
        "https://github.com/approved/deep",
        datetime(2026, 11, 4, tzinfo=UTC),
    )
    assert evidence.revision == scan.commit_sha.lower()
