"""LRN-06 golden fixtures: parser, provenance, graph links, diff, and restart recovery."""

import subprocess
from pathlib import Path

import pytest

from app.code_intelligence import (
    ApprovedRepository,
    CSharpTreeSitterAdapter,
    ParserRegistry,
    PythonAstAdapter,
    default_parser_registry,
    load_last_good,
    map_diff_to_symbols,
    resolve_dependencies,
    scan_repository,
    scan_with_recovery,
)
from app.code_intelligence.adapters import ParserResult


def git(repo: Path, *args: str) -> str:
    result = subprocess.run(
        ["git", "-C", str(repo), *args], capture_output=True, text=True, check=True
    )
    return result.stdout.strip()


def create_repo(tmp_path: Path, name: str = "source") -> Path:
    root = tmp_path / name
    root.mkdir()
    git(root, "init")
    git(root, "config", "user.name", "CI User")
    git(root, "config", "user.email", "ci@example.test")
    return root


def commit(repo: Path, message: str) -> str:
    git(repo, "add", ".")
    git(repo, "commit", "-m", message)
    return git(repo, "rev-parse", "HEAD")


def write(repo: Path, path: str, body: str) -> None:
    target = repo / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(body, encoding="utf-8")


def read_blob(repo: Path, commit_sha: str, path: str) -> str:
    return git(repo, "show", f"{commit_sha}:{path}")


def test_python_ast_golden_symbols_imports_calls() -> None:
    content = (
        "import os\n"
        "from demo.models import Item\n"
        "class Handler:\n"
        "    async def process(self):\n"
        "        return Item()\n"
    )
    result = PythonAstAdapter().parse("src/handler.py", content)
    assert [(s.name, s.start_line, s.end_line) for s in result.symbols] == [
        ("Handler", 3, 5),
        ("process", 4, 5),
    ]
    assert result.symbols[1].qualified_name == "Handler.process"
    assert {x.target for x in result.imports} == {"os", "demo.models"}
    assert [(c.target, c.line) for c in result.calls] == [("Item", 5)]


def test_csharp_tree_sitter_golden_symbols() -> None:
    content = (
        "using System;\n"
        "namespace Demo {\n"
        "  public class Worker {\n"
        "    public void Run() { Console.WriteLine(1); }\n"
        "  }\n"
        "}\n"
    )
    result = CSharpTreeSitterAdapter().parse("Worker.cs", content)
    assert ("Worker", "class", 3) in {
        (sym.name, sym.kind, sym.start_line) for sym in result.symbols
    }
    assert ("Run", "method", 4) in {
        (sym.name, sym.kind, sym.start_line) for sym in result.symbols
    }
    assert result.imports and "System" in result.imports[0].target


def test_language_fallback_and_parser_isolation(tmp_path: Path) -> None:
    repo = create_repo(tmp_path)
    write(repo, "ok.py", "def ok():\n    return True\n")
    write(repo, "unknown.xyz", "unsupported")
    commit(repo, "initial")
    scan = scan_repository(repo, default_parser_registry())
    assert scan.status == "COMPLETE"
    py = next(a for a in scan.analyses if a.path == "ok.py")
    unrecognized = next(a for a in scan.analyses if a.path == "unknown.xyz")
    assert py.symbol_analysis == "AVAILABLE"
    assert {s.name for s in py.symbols} == {"ok"}
    assert unrecognized.symbol_analysis == "UNAVAILABLE"
    assert unrecognized.symbols == ()


def test_dependency_graph_uses_only_committed_explicit_references(tmp_path: Path) -> None:
    repo = create_repo(tmp_path)
    write(repo, "pkg/__init__.py", "# package\n")
    write(repo, "consumer.py", "from pkg import object\n")
    write(repo, "service/Service.csproj", (
        '<Project><ItemGroup><ProjectReference Include="../shared/Shared.csproj" />'
        "</ItemGroup></Project>"
    ))
    write(repo, "shared/Shared.csproj", "<Project />")
    sha = commit(repo, "deps")
    scan = scan_repository(repo, default_parser_registry())
    source = ApprovedRepository("app-project", "https://github.com/example/app", scan)
    links = resolve_dependencies(
        source, read_blob=lambda path: read_blob(repo, sha, path)
    )
    assert any(
        x.kind == "PYTHON_IMPORT"
        and x.target_path == "pkg/__init__.py"
        and x.status == "RESOLVED_EXPLICIT"
        for x in links
    )
    assert any(
        x.kind == "PROJECT_REFERENCE"
        and x.target_path == "shared/Shared.csproj"
        and x.status == "RESOLVED_EXPLICIT"
        for x in links
    )
    assert not any(x.status == "INFERRED" for x in links)


def test_cross_repo_association_uses_declared_git_url_only(tmp_path: Path) -> None:
    app = create_repo(tmp_path, "app")
    lib = create_repo(tmp_path, "lib")
    write(app, "package.json", (
        '{"dependencies":{"lib":"git+https://github.com/acme/lib.git"}}'
    ))
    write(lib, "index.py", "answer = 42\n")
    app_sha = commit(app, "declare dep")
    commit(lib, "lib")
    source = ApprovedRepository(
        "app", "https://github.com/acme/app.git", scan_repository(app)
    )
    target = ApprovedRepository(
        "lib", "https://github.com/acme/lib.git", scan_repository(lib)
    )
    links = resolve_dependencies(
        source,
        approved_repositories=(target,),
        read_blob=lambda path: read_blob(app, app_sha, path),
    )
    assert len(links) == 1
    assert links[0].source_repo == "app"
    assert links[0].target_repo == "lib"
    assert links[0].target_commit == target.scan.commit_sha
    assert links[0].status == "DECLARED_TARGET_REPOSITORY"
    assert links[0].target_path is None


def test_no_cross_repo_link_from_similar_name(tmp_path: Path) -> None:
    app = create_repo(tmp_path, "app")
    lib = create_repo(tmp_path, "lib")
    write(app, "package.json", '{"dependencies":{"lib":"1.2.3"}}')
    write(lib, "index.py", "pass\n")
    sha = commit(app, "declare package")
    commit(lib, "lib")
    source = ApprovedRepository("app", "https://github.com/acme/app", scan_repository(app))
    target = ApprovedRepository("lib", "https://github.com/acme/lib", scan_repository(lib))
    assert resolve_dependencies(
        source, (target,), read_blob=lambda p: read_blob(app, sha, p)
    ) == ()


def test_diff_to_symbol_uses_exact_line_overlap(tmp_path: Path) -> None:
    repo = create_repo(tmp_path)
    write(repo, "feature.py", "def first():\n    return 1\n\ndef second():\n    return 2\n")
    base = commit(repo, "base")
    write(repo, "feature.py", "def first():\n    return 3\n\ndef second():\n    return 2\n")
    commit(repo, "change first")
    head = scan_repository(repo, default_parser_registry())
    impact = map_diff_to_symbols(repo, base, head)
    assert len(impact) == 1
    assert impact[0].symbols == ("first",)
    assert impact[0].confidence == "EXACT_LINE_OVERLAP"
    assert impact[0].changed_new_lines == (2,)


def test_diff_of_unparsed_file_is_file_only(tmp_path: Path) -> None:
    repo = create_repo(tmp_path)
    write(repo, "x.foo", "old\n")
    base = commit(repo, "base")
    write(repo, "x.foo", "new\n")
    commit(repo, "new")
    impact = map_diff_to_symbols(repo, base, scan_repository(repo))
    assert len(impact) == 1
    assert impact[0].symbols == ()
    assert impact[0].confidence == "FILE_ONLY"


class CrashingAdapter:
    name = "crash"
    version = "1"
    extensions = frozenset({".py"})
    capabilities = frozenset({"symbols"})

    def parse(self, path: str, content: str) -> ParserResult:
        raise RuntimeError("fake parser crashed with secret literal")


def test_parser_failure_preserves_durable_last_good_after_restart(tmp_path: Path) -> None:
    repo = create_repo(tmp_path)
    write(repo, "valid.py", "def good():\n    return 1\n")
    commit(repo, "first")
    cache_dir = tmp_path / "cache"
    good = scan_with_recovery(repo, cache_dir, default_parser_registry())
    assert good.status == "COMPLETE"
    assert load_last_good(cache_dir, repo) == good

    crashing = ParserRegistry()
    crashing.register(CrashingAdapter())
    partial = scan_with_recovery(repo, cache_dir, crashing)
    assert partial.status == "PARTIAL"
    assert partial.last_good == good
    assert not any("secret literal" in e for e in partial.errors)

    # Restart simulation: no in-memory snapshot passed.
    second = scan_with_recovery(repo, cache_dir, crashing)
    assert second.status == "PARTIAL"
    assert second.last_good == good
    assert load_last_good(cache_dir, repo) == good


def test_partial_scan_cannot_overwrite_snapshot(tmp_path: Path) -> None:
    from app.code_intelligence import save_last_good

    repo = create_repo(tmp_path)
    write(repo, "a.py", "def ok():\n    pass\n")
    commit(repo, "first")
    good = scan_repository(repo, default_parser_registry())
    cache = tmp_path / "cache"
    save_last_good(good, cache)
    crashing = ParserRegistry()
    crashing.register(CrashingAdapter())
    partial = scan_repository(repo, crashing, previous=good)
    with pytest.raises(ValueError, match="Only complete"):
        save_last_good(partial, cache)
    assert load_last_good(cache, repo) == good
