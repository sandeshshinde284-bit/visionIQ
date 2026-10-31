"""B-1 regression: the C# adapter must never crash the interpreter on real code.

A native crash (segfault) cannot be caught by the scanner's per-file exception
isolation, so it would bypass the PARTIAL/last-good protection entirely. Parse
every tracked C# file in a child process and require a clean exit.
"""

import subprocess
import sys
from pathlib import Path

import pytest

REPO = Path(__file__).resolve().parents[2]
CHILD = """
import sys
from app.code_intelligence.parsers import CSharpTreeSitterAdapter
adapter = CSharpTreeSitterAdapter()
for path in sys.argv[1:]:
    with open(path, encoding="utf-8-sig") as handle:
        adapter.parse(path, handle.read())
print("ok", len(sys.argv) - 1)
"""


def test_csharp_adapter_parses_every_repository_file_without_native_crash() -> None:
    pytest.importorskip("tree_sitter_c_sharp")
    files = subprocess.run(
        ["git", "-C", str(REPO), "ls-files", "*.cs"],
        capture_output=True,
        text=True,
        check=True,
    ).stdout.split()
    if not files:
        pytest.skip("No C# files in this checkout")
    result = subprocess.run(
        [sys.executable, "-c", CHILD, *files],
        cwd=REPO,
        env={"PYTHONPATH": str(REPO / "src" / "VisionIQ.Intelligence"), "PATH": "/usr/bin:/bin"},
        capture_output=True,
        text=True,
        timeout=300,
    )
    assert result.returncode == 0, (
        f"C# parser exited with {result.returncode} (negative = killed by signal): "
        f"{result.stderr[-500:]}"
    )
