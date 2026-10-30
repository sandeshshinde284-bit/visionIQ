"""Explicit parser contracts. No language has structural support until an adapter is registered.

An adapter's output is evidence, not proof of runtime calls, causality, or complete
dependency coverage. Unsupported extensions must never produce invented symbols.
"""

from dataclasses import dataclass, field
from typing import Protocol


@dataclass(frozen=True)
class SymbolReference:
    name: str
    kind: str
    start_line: int
    end_line: int


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
    """Review and test each implementation before registering it."""

    name: str
    version: str
    extensions: frozenset[str]
    capabilities: frozenset[str]

    def parse(self, path: str, content: str) -> ParserResult: ...


@dataclass
class ParserRegistry:
    """Only explicitly registered adapters are trusted for structural parsing."""

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
        name = path.rsplit("/", maxsplit=1)[-1].lower()
        if "." not in name:
            return None
        extension = "." + name.rsplit(".", maxsplit=1)[-1]
        return self._by_extension.get(extension)
