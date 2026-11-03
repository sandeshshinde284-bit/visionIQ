"""M-9: C# symbols carry namespaces; using forms and legacy encodings are handled."""

import os
import subprocess
from pathlib import Path

import pytest

pytest.importorskip("tree_sitter_c_sharp")

from app.code_intelligence.parsers import CSharpTreeSitterAdapter, default_parser_registry
from app.code_intelligence.scanner import scan_repository

SOURCE = """global using System.Text;
using static System.Math;
using Json = System.Text.Json;
using System;
namespace Acme.Billing;
public class Invoice { public int Total { get; set; } void Pay() { Foo(); } }
"""


def test_file_scoped_namespace_and_properties_are_in_qualified_names() -> None:
    symbols = CSharpTreeSitterAdapter().parse("Invoice.cs", SOURCE).symbols
    assert [(s.qualified_name, s.kind) for s in symbols] == [
        ("Acme.Billing.Invoice", "class"),
        ("Acme.Billing.Invoice.Total", "property"),
        ("Acme.Billing.Invoice.Pay", "method"),
    ]


def test_block_namespace_is_in_qualified_names() -> None:
    symbols = (
        CSharpTreeSitterAdapter()
        .parse("L.cs", "namespace Outer.Inner { class Ledger { } }")
        .symbols
    )
    assert [s.qualified_name for s in symbols] == ["Outer.Inner.Ledger"]


def test_every_using_form_yields_the_imported_name() -> None:
    imports = CSharpTreeSitterAdapter().parse("Invoice.cs", SOURCE).imports
    assert [i.target for i in imports] == [
        "System.Text",
        "System.Math",
        "System.Text.Json",
        "System",
    ]


def test_a_windows_1252_file_does_not_fail_the_repository_scan(tmp_path: Path) -> None:
    root = tmp_path / "repo"
    root.mkdir()
    (root / "Legacy.cs").write_bytes("// Café\nclass Legacy { }\n".encode("cp1252"))
    (root / "Bom.cs").write_bytes("\ufeffclass WithBom { }\n".encode())
    env = {
        **os.environ,
        "GIT_AUTHOR_NAME": "t",
        "GIT_AUTHOR_EMAIL": "t@t",
        "GIT_COMMITTER_NAME": "t",
        "GIT_COMMITTER_EMAIL": "t@t",
    }
    for args in (["init", "-q"], ["add", "."], ["commit", "-qm", "init"]):
        subprocess.run(["git", "-C", str(root), *args], check=True, env=env)

    scan = scan_repository(root, default_parser_registry())

    assert scan.status == "COMPLETE"
    by_path = {a.path: a for a in scan.analyses}
    assert [s.name for s in by_path["Legacy.cs"].symbols] == ["Legacy"]
    assert any("not valid UTF-8" in d for d in by_path["Legacy.cs"].diagnostics)
    assert [s.name for s in by_path["Bom.cs"].symbols] == ["WithBom"]
