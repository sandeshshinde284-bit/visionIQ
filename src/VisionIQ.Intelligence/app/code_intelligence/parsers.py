"""Reviewed Python and C# structural adapters; no Roslyn and no automatic language guessing.

Python uses the standard-library AST. C# uses Tree-sitter (grammar pinned in
requirements.txt). These extract declared symbols and lexical references, not
a verified call graph or runtime dependency.
"""

import ast

from app.code_intelligence.adapters import (
    CallReference,
    ImportReference,
    ParserRegistry,
    ParserResult,
    SymbolReference,
)


class PythonAstAdapter:
    name = "python-ast"
    version = "1"
    extensions = frozenset({".py"})
    capabilities = frozenset({"symbols", "imports", "lexical_calls"})

    def parse(self, path: str, content: str) -> ParserResult:
        tree = ast.parse(content, filename=path)
        symbols: list[SymbolReference] = []
        imports: list[ImportReference] = []
        calls: list[CallReference] = []

        class Walker(ast.NodeVisitor):
            def __init__(self) -> None:
                self.scope: list[str] = []

            def _symbol(self, node: ast.ClassDef | ast.FunctionDef | ast.AsyncFunctionDef) -> None:
                qualified = ".".join([*self.scope, node.name])
                symbols.append(
                    SymbolReference(
                        name=node.name,
                        kind=("class" if isinstance(node, ast.ClassDef) else "function"),
                        start_line=node.lineno,
                        end_line=node.end_lineno or node.lineno,
                        qualified_name=qualified,
                    )
                )
                self.scope.append(node.name)
                self.generic_visit(node)
                self.scope.pop()

            def visit_ClassDef(self, node: ast.ClassDef) -> None:
                self._symbol(node)

            def visit_FunctionDef(self, node: ast.FunctionDef) -> None:
                self._symbol(node)

            def visit_AsyncFunctionDef(self, node: ast.AsyncFunctionDef) -> None:
                self._symbol(node)

            def visit_Import(self, node: ast.Import) -> None:
                for alias in node.names:
                    imports.append(ImportReference(target=alias.name, line=node.lineno))

            def visit_ImportFrom(self, node: ast.ImportFrom) -> None:
                # Leading dots retain relative-import information for the resolver.
                prefix = "." * node.level
                imports.append(
                    ImportReference(target=prefix + (node.module or ""), line=node.lineno)
                )

            def visit_Call(self, node: ast.Call) -> None:
                name = None
                if isinstance(node.func, ast.Name):
                    name = node.func.id
                elif isinstance(node.func, ast.Attribute):
                    name = node.func.attr
                if name:
                    calls.append(CallReference(target=name, line=node.lineno))
                self.generic_visit(node)

        Walker().visit(tree)
        return ParserResult(symbols=tuple(symbols), imports=tuple(imports), calls=tuple(calls))


class CSharpTreeSitterAdapter:
    name = "csharp-tree-sitter"
    version = "0.23.5"
    extensions = frozenset({".cs"})
    capabilities = frozenset({"symbols", "namespace_imports", "lexical_calls"})

    def parse(self, path: str, content: str) -> ParserResult:
        import tree_sitter_c_sharp
        from tree_sitter import Language, Parser

        raw = content.encode("utf-8")
        tree = Parser(Language(tree_sitter_c_sharp.language())).parse(raw)
        if tree is None or tree.root_node.has_error:
            raise ValueError("C# grammar returned an incomplete parse")
        symbols: list[SymbolReference] = []
        imports: list[ImportReference] = []
        calls: list[CallReference] = []
        kinds = {
            "class_declaration": "class",
            "interface_declaration": "interface",
            "struct_declaration": "struct",
            "record_declaration": "record",
            "enum_declaration": "enum",
            "method_declaration": "method",
            "constructor_declaration": "constructor",
        }

        def node_text(node: object) -> str:
            return raw[node.start_byte : node.end_byte].decode("utf-8")

        def walk(node: object, scope: tuple[str, ...]) -> None:
            node_type = node.type
            if node_type in kinds:
                name_node = node.child_by_field_name("name")
                if name_node is None:
                    name_node = next(
                        (child for child in node.named_children if child.type == "identifier"),
                        None,
                    )
                if name_node is not None:
                    name = node_text(name_node)
                    symbols.append(
                        SymbolReference(
                            name=name,
                            kind=kinds[node_type],
                            start_line=node.start_point.row + 1,
                            end_line=node.end_point.row + 1,
                            qualified_name=".".join((*scope, name)),
                        )
                    )
                    scope = (*scope, name)
            elif node_type == "using_directive":
                target = node_text(node).strip().rstrip(";")
                if target.startswith("using "):
                    target = target[6:].strip()
                if target:
                    imports.append(ImportReference(target=target, line=node.start_point.row + 1))
            elif node_type == "invocation_expression":
                function = node.child_by_field_name("function")
                if function is not None:
                    calls.append(
                        CallReference(
                            target=node_text(function),
                            line=node.start_point.row + 1,
                        )
                    )
            for child in node.named_children:
                walk(child, scope)

        walk(tree.root_node, ())
        return ParserResult(symbols=tuple(symbols), imports=tuple(imports), calls=tuple(calls))


def default_parser_registry() -> ParserRegistry:
    """Enable only the tested Python and C# adapters.

    Instantiate at the call-site, rather than changing the scanner's default
    fallback-only behavior unexpectedly.
    """
    registry = ParserRegistry()
    registry.register(PythonAstAdapter())
    registry.register(CSharpTreeSitterAdapter())
    return registry
