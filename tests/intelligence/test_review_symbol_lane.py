"""Review R6-02: the retrieval "symbols -> files" lane must read symbol names, not line numbers.

Symbol node ids come from code_intelligence/graph.py ("path::Qualified.Name:LINE").
The lane used regexp_replace(native_id, '^.*[.:]', ''), which always yielded the LINE,
so stack-frame symbols such as "Submit" or "run" never matched and the lane returned nothing.
"""

import os
import re

import pytest
from test_code_relationship_graph import _evidence, _scan

from app.code_intelligence.graph import project_scan_edges
from app.knowledge.retrieval import CODE_PATH_SQL, SYMBOL_NAME_PATTERN


def _symbol_native_ids() -> list[str]:
    scan = _scan()
    return [
        edge.target.native_id
        for edge in project_scan_edges(scan, _evidence(scan))
        if edge.target.kind == "symbol"
    ]


def test_the_lane_uses_the_reviewed_symbol_name_pattern() -> None:
    assert f"regexp_replace(sn.native_id, '{SYMBOL_NAME_PATTERN}', '\\1')" in CODE_PATH_SQL
    assert "'^.*[.:]'" not in CODE_PATH_SQL


@pytest.mark.parametrize(
    ("native_id", "name"),
    [
        ("src/A.cs::Acme.Orders.OrderService.Submit:40", "Submit"),
        ("src/app/a.py::Svc.run:12", "run"),
        ("svc.py::run:3", "run"),
        ("x/My.File.cs::Ns.Outer.Inner<T>.Get:7", "Get"),
    ],
)
def test_symbol_name_pattern_extracts_the_simple_name(native_id: str, name: str) -> None:
    # Python's re agrees with PostgreSQL ARE for this pattern (no class shorthands, no lookaround).
    assert re.sub(SYMBOL_NAME_PATTERN, r"\1", native_id) == name


def test_real_projected_symbol_ids_resolve_to_their_names() -> None:
    ids = _symbol_native_ids()
    assert ids, "the fixture scan must declare at least one symbol"
    for native_id in ids:
        name = re.sub(SYMBOL_NAME_PATTERN, r"\1", native_id)
        assert name.isidentifier(), (native_id, name)
        assert not name.isdigit()


@pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
def test_postgresql_evaluates_the_pattern_the_same_way() -> None:
    psycopg = pytest.importorskip("psycopg")
    from corpus_support import conninfo

    ids = _symbol_native_ids()
    with psycopg.connect(conninfo("API")) as connection:
        rows = connection.execute(
            f"SELECT regexp_replace(v, '{SYMBOL_NAME_PATTERN}', '\\1') "
            "FROM unnest(%s::text[]) WITH ORDINALITY AS t(v, n) ORDER BY n",
            (ids,),
        ).fetchall()
    assert [row[0] for row in rows] == [re.sub(SYMBOL_NAME_PATTERN, r"\1", i) for i in ids]
