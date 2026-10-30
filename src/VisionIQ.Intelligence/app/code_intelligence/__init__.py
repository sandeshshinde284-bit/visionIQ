"""Language-neutral Git code intelligence (LRN-06)."""

from app.code_intelligence.adapters import (
    ImportReference,
    ParserAdapter,
    ParserRegistry,
    ParserResult,
    SymbolReference,
)
from app.code_intelligence.impact import DiffImpact, map_diff_to_symbols
from app.code_intelligence.parsers import (
    CSharpTreeSitterAdapter,
    PythonAstAdapter,
    default_parser_registry,
)
from app.code_intelligence.relationships import (
    ApprovedRepository,
    DependencyLink,
    resolve_dependencies,
)
from app.code_intelligence.scanner import FileAnalysis, GitFile, RepositoryScan, scan_repository
from app.code_intelligence.snapshots import (
    load_last_good,
    save_last_good,
    scan_with_recovery,
)

__all__ = [
    "ApprovedRepository",
    "CSharpTreeSitterAdapter",
    "DependencyLink",
    "DiffImpact",
    "FileAnalysis",
    "GitFile",
    "ImportReference",
    "ParserAdapter",
    "ParserRegistry",
    "ParserResult",
    "PythonAstAdapter",
    "RepositoryScan",
    "SymbolReference",
    "default_parser_registry",
    "load_last_good",
    "map_diff_to_symbols",
    "resolve_dependencies",
    "save_last_good",
    "scan_repository",
    "scan_with_recovery",
]
