"""Reviewed Python and C# structural adapters; no Roslyn and no automatic language guessing.

Python uses the standard-library AST. C# uses Tree-sitter (grammar pinned in
requirements.txt). These extract declared symbols and lexical references, not
a verified call graph or runtime dependency.
"""

import ast

from app.code_intelligence.adapters import (
    CallReference,
    ImportReference,
    IncompleteParseError,
    ParserRegistry,
    ParserResult,
    SymbolReference,
)


class PythonAstAdapter:
    name = "python-ast"
    version = "2"  # LRN-07: test functions are "test" symbols
    extensions = frozenset({".py"})
    capabilities = frozenset({"symbols", "imports", "lexical_calls"})

    def parse(self, path: str, content: str) -> ParserResult:
        try:
            tree = ast.parse(content, filename=path)
        except SyntaxError as error:
            # N-16: invalid or newer-than-supported syntax is a grammar gap, not a crash.
            raise IncompleteParseError("Python", error.lineno or 1) from None
        symbols: list[SymbolReference] = []
        imports: list[ImportReference] = []
        calls: list[CallReference] = []

        # LRN-07: pytest/unittest test functions in test files become "test" symbols.
        file_name = path.rsplit("/", 1)[-1]
        is_test_file = file_name.startswith("test_") or file_name.endswith("_test.py")

        class Walker(ast.NodeVisitor):
            def __init__(self) -> None:
                self.scope: list[str] = []

            def _symbol(self, node: ast.ClassDef | ast.FunctionDef | ast.AsyncFunctionDef) -> None:
                qualified = ".".join([*self.scope, node.name])
                symbols.append(
                    SymbolReference(
                        name=node.name,
                        kind=(
                            "class"
                            if isinstance(node, ast.ClassDef)
                            else "test"
                            if is_test_file and node.name.startswith("test")
                            else "function"
                        ),
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
                # Lexical only: for `obj.method()` we record "method", not the
                # type of `obj`, since that needs type inference we don't do.
                # This is why CallReference isn't a verified call graph edge.
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
    # Grammar 0.23.5; ".3" = namespaces, using forms, properties (M-9) and test methods (LRN-07).
    version = "0.23.5.3"
    extensions = frozenset({".cs"})
    capabilities = frozenset({"symbols", "namespace_imports", "lexical_calls"})

    def parse(self, path: str, content: str) -> ParserResult:
        import tree_sitter_c_sharp
        from tree_sitter import Language, Parser

        raw = content.encode("utf-8")
        tree = Parser(Language(tree_sitter_c_sharp.language())).parse(raw)
        if tree is None:
            raise ValueError("C# grammar returned no parse tree")
        if tree.root_node.has_error:
            # N-16: e.g. tree-sitter-c-sharp 0.23.5 cannot parse "[field]" collection expressions.
            raise IncompleteParseError("C#", _first_error_line(tree.root_node))
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
            "property_declaration": "property",
        }
        name_types = {"identifier", "qualified_name", "generic_name", "alias_qualified_name"}
        test_attributes = {
            "Fact",
            "Theory",
            "Test",
            "TestCase",
            "TestCaseSource",
            "TestMethod",
            "DataTestMethod",
        }

        def is_test_method(method) -> bool:
            for attribute_list in (c for c in method.named_children if c.type == "attribute_list"):
                for attribute in (
                    c for c in attribute_list.named_children if c.type == "attribute"
                ):
                    name_node = attribute.child_by_field_name("name") or next(
                        (c for c in attribute.named_children if c.type in name_types), None
                    )
                    if name_node is None:
                        continue
                    name = node_text(name_node).rsplit(".", 1)[-1].removesuffix("Attribute")
                    # Custom subclasses such as [DatabaseFact] or [SkippableTheory] count too.
                    if name in test_attributes or name.endswith(("Fact", "Theory")):
                        return True
            return False

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
                    kind = kinds[node_type]
                    if node_type == "method_declaration" and is_test_method(node):
                        kind = "test"  # LRN-07: xUnit/NUnit/MSTest test method
                    symbols.append(
                        SymbolReference(
                            name=name,
                            kind=kind,
                            start_line=node.start_point.row + 1,
                            end_line=node.end_point.row + 1,
                            qualified_name=".".join((*scope, name)),
                        )
                    )
                    scope = (*scope, name)
            elif node_type == "namespace_declaration":
                # M-9: block namespaces become part of every nested symbol's qualified name.
                name_node = node.child_by_field_name("name")
                if name_node is not None:
                    scope = (*scope, *node_text(name_node).split("."))
            elif node_type == "using_directive":
                # M-9: the imported namespace/type is the last name node, for plain,
                # "global using", "using static" and "using Alias = Target" forms alike.
                names = [child for child in node.named_children if child.type in name_types]
                if names:
                    imports.append(
                        ImportReference(target=node_text(names[-1]), line=node.start_point.row + 1)
                    )
            elif node_type == "invocation_expression":
                function = node.child_by_field_name("function")
                if function is not None:
                    calls.append(
                        CallReference(
                            target=node_text(function),
                            line=node.start_point.row + 1,
                        )
                    )
            child_scope = scope
            for child in node.named_children:
                if child.type == "file_scoped_namespace_declaration":
                    # M-9: "namespace X;" applies to the declarations that FOLLOW it (siblings).
                    name_node = child.child_by_field_name("name")
                    if name_node is not None:
                        child_scope = (*scope, *node_text(name_node).split("."))
                    continue
                walk(child, child_scope)

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


def _first_error_line(node) -> int:
    """1-based line of the first ERROR or missing node (iterative; no recursion limit)."""
    stack = [node]
    while stack:
        current = stack.pop()
        if current.type == "ERROR" or current.is_missing:
            return current.start_point.row + 1
        stack.extend(reversed(current.children))
    return 1
