"""Explicit, versioned parser contracts. Structural findings are source evidence, not causality."""

from dataclasses import dataclass, field
from typing import Protocol


@dataclass(frozen=True)
class SymbolReference:
    name: str
    kind: str
    start_line: int
    end_line: int
    qualified_name: str | None = None


@dataclass(frozen=True)
class ImportReference:
    target: str
    line: int


@dataclass(frozen=True)
class CallReference:
    target: str
    line: int


@dataclass(frozen=True)
class ParserResult:
    symbols: tuple[SymbolReference, ...] = ()
    imports: tuple[ImportReference, ...] = ()
    calls: tuple[CallReference, ...] = ()
    diagnostics: tuple[str, ...] = ()


class ParserAdapter(Protocol):
    """Review and test implementations before registering them.

    A Protocol rather than an ABC: adapters (stdlib ast, tree-sitter, future
    ones) come from different libraries with their own class hierarchies, so
    structural typing avoids forcing them through a shared base class.
    """

    name: str
    version: str
    extensions: frozenset[str]
    capabilities: frozenset[str]

    def parse(self, path: str, content: str) -> ParserResult: ...


@dataclass
class ParserRegistry:
    """Only explicitly registered adapters can produce structural evidence."""

    _by_extension: dict[str, ParserAdapter] = field(default_factory=dict)

    def register(self, adapter: ParserAdapter) -> None:
        if not adapter.name or not adapter.version or not adapter.extensions:
            raise ValueError("Adapter requires a name, version and extensions")
        for extension in adapter.extensions:
            if not extension.startswith(".") or extension.lower() != extension:
                raise ValueError("Adapter extensions must be lowercase and start with '.'")
            if extension in self._by_extension:
                raise ValueError(f"Parser already registered for {extension}")
        for extension in adapter.extensions:
            self._by_extension[extension] = adapter

    def for_path(self, path: str) -> ParserAdapter | None:
        # Matched by file extension only, never by shebang/content sniffing -
        # extension-based routing is what keeps "no code execution" true here.
        name = path.rsplit("/", maxsplit=1)[-1].lower()
        if "." not in name:
            return None
        extension = "." + name.rsplit(".", maxsplit=1)[-1]
        return self._by_extension.get(extension)


class IncompleteParseError(ValueError):
    """N-16: a grammar could not fully parse otherwise readable source.

    This is not a parser crash: the scanner keeps file-level evidence for the file
    (state UNAVAILABLE plus this diagnostic) instead of failing the whole repository.
    """

    def __init__(self, language: str, line: int) -> None:
        self.line = line
        super().__init__(
            f"{language} grammar could not fully parse this file (first problem at line {line}); "
            "file-level evidence only."
        )
