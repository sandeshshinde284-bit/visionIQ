"""Focused source-backed local graph-inspector tests (no cloud or DB required)."""

import importlib.util
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "visioniq_local_graph_inspector", ROOT / "scripts" / "inspect_graph.py"
)
assert SPEC is not None and SPEC.loader is not None
inspector = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(inspector)


def test_only_explicit_local_db_configuration_is_accepted():
    valid = {
        "DB_TARGET": "local",
        "DB_HOST": "127.0.0.1",
        "DB_NAME": "test-db",
        "DB_API_USER": "test-user",
        "DB_API_PASSWORD": "test-password",
    }
    assert inspector.database_settings(valid)["dbname"] == "test-db"
    with pytest.raises(ValueError, match="loopback"):
        inspector.database_settings({**valid, "DB_HOST": "db.example.test"})
    with pytest.raises(ValueError, match="DB_TARGET"):
        inspector.database_settings({**valid, "DB_TARGET": "cloud"})
    with pytest.raises(ValueError, match="DB_API_PASSWORD"):
        inspector.database_settings({**valid, "DB_API_PASSWORD": ""})


class FakeCursor:
    def __init__(self, nodes, neighbors, root):
        self.nodes = nodes
        self.neighbors = neighbors
        self.root = root
        self.rows = []

    def __enter__(self):
        return self

    def __exit__(self, *unused):
        return False

    def execute(self, query, params):
        if query is inspector._ROOT:
            self.rows = [{"node_key": self.root}] if self.root else []
        elif query is inspector._NODE:
            row = self.nodes.get(params[0])
            self.rows = [row] if row is not None else []
        elif query is inspector._ACTIVE_EDGES:
            self.rows = self.neighbors.get(params[0], [])[: params[1]]
        else:
            raise AssertionError("Unexpected SQL request")

    def fetchall(self):
        return self.rows

    def fetchone(self):
        return self.rows[0] if self.rows else None


class FakeConnection:
    def __init__(self, nodes, edges, root="ticket"):
        self.state = FakeCursor(nodes, edges, root)

    def cursor(self, row_factory=None):
        return self.state


def node(key, kind, project="project-a"):
    return {
        "node_key": key,
        "node_kind": kind,
        "native_id": key,
        "item_key": key + "-source",
        "provider": "azure_devops",
        "source_instance": "https://ado.example.test/org",
        "project_id": project,
        "repository_id": "repo-" + project,
    }


def edge(target, relation, proof_project="project-a"):
    return {
        "to_node_key": target,
        "relation_type": relation,
        "classification": "explicit",
        "provenance_item_key": "proof-" + target,
        "provenance_revision_id": "version-1",
        "proof_project": proof_project,
    }


def test_real_structural_chain_and_project_boundaries():
    nodes = {
        "ticket": node("ticket", "work_item"),
        "pr": node("pr", "pull_request"),
        "commit": node("commit", "commit"),
        "file": node("file", "file"),
        "test": node("test", "test_case"),
        "outside": node("outside", "file", project="another-project"),
    }
    edges = {
        "ticket": [edge("pr", "ticket_pr"), edge("outside", "file_dependency")],
        "pr": [edge("commit", "pr_commit")],
        "commit": [edge("file", "commit_file")],
        "file": [edge("test", "file_test")],
    }
    result = inspector.inspect(
        FakeConnection(nodes, edges),
        "https://ado.example.test/org",
        "project-a",
        "123",
        {"project-a"},
        4,
        100,
    )
    assert len(result["nodes"]) == 5
    assert [e["relation"] for e in result["edges"]] == [
        "ticket_pr",
        "pr_commit",
        "commit_file",
        "file_test",
    ]
    assert all(e["provenance_revision"] == "version-1" for e in result["edges"])
    assert not any(n["key"] == "outside" for n in result["nodes"])


def test_cross_project_requires_explicit_additional_project_scope():
    nodes = {
        "ticket": node("ticket", "work_item"),
        "other": node("other", "repository", project="project-b"),
    }
    edges = {"ticket": [edge("other", "repository_dependency", "project-b")]}
    connection = FakeConnection(nodes, edges)
    excluded = inspector.inspect(
        connection,
        "https://ado.example.test/org",
        "project-a",
        "123",
        {"project-a"},
        1,
        100,
    )
    included = inspector.inspect(
        connection,
        "https://ado.example.test/org",
        "project-a",
        "123",
        {"project-a", "project-b"},
        1,
        100,
    )
    assert len(excluded["nodes"]) == 1
    assert len(included["nodes"]) == 2
    assert len(included["edges"]) == 1


def test_missing_root_is_not_invented():
    with pytest.raises(ValueError, match="No unique canonical"):
        inspector.inspect(
            FakeConnection({}, {}, root=None),
            "https://ado.example.test/org",
            "project-a",
            "123",
            {"project-a"},
            3,
            100,
        )


def test_cycle_does_not_expand_forever():
    nodes = {
        "ticket": node("ticket", "work_item"),
        "pr": node("pr", "pull_request"),
    }
    edges = {
        "ticket": [edge("pr", "ticket_pr")],
        "pr": [edge("ticket", "file_dependency")],
    }
    result = inspector.inspect(
        FakeConnection(nodes, edges),
        "https://ado.example.test/org",
        "project-a",
        "123",
        {"project-a"},
        5,
        100,
    )
    assert len(result["nodes"]) == 2
    assert len(result["edges"]) == 2
