"""Language-neutral, read-only Git code intelligence (LRN-06)."""

from app.code_intelligence.adapters import (
    ImportReference,
    ParserAdapter,
    ParserRegistry,
    ParserResult,
    SymbolReference,
)
from app.code_intelligence.scanner import (
    FileAnalysis,
    GitFile,
    RepositoryScan,
    scan_repository,
)

__all__ = [
    "FileAnalysis",
    "GitFile",
    "ImportReference",
    "ParserAdapter",
    "ParserRegistry",
    "ParserResult",
    "RepositoryScan",
    "SymbolReference",
    "scan_repository",
]
