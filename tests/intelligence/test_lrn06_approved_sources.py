"""LRN-06 approved-source discovery and production dependency-to-graph contract.

All identities and Git URLs below are generated test fixtures, never runtime
defaults or production project mappings. No database, network or Cloud Storage
access is required.
"""

from datetime import UTC, datetime
from types import SimpleNamespace

import pytest

from app.code_intelligence import approved_sources, graph
from app.code_intelligence.canonical import ScanEvidenceIdentity, build_scan_evidence
from app.code_intelligence.relationships import ApprovedRepository, resolve_dependencies
from app.code_intelligence.scanner import GitFile, RepositoryScan


class FakeConnection:
    def __init__(self, rows):
        self.rows = rows
        self.parameters = None
        self.query = ""

    def __enter__(self):
        return self

    def __exit__(self, *args):
        return False

    def execute(self, query, parameters):
        self.query = query
        self.parameters = parameters
        return self

    def fetchall(self):
        return self.rows


def source_evidence():
    scan = RepositoryScan(
        "/checkout",
        "a" * 40,
        "COMPLETE",
        (GitFile("package.json", "b" * 40, None, True),),
        (),
    )
    identity = ScanEvidenceIdentity(
        "https://code.example.test/teams/source", "source-project", "source-repository"
    )
    evidence = build_scan_evidence(
        scan,
        identity,
        identity.source_instance,
        datetime.now(UTC),
        ado_organization="https://dev.example.test/org",
    )
    return scan, evidence


def target_row(
    repository_id="target-repository",
    origin="https://code.example.test/teams/target",
    project_id="target-project",
    commit="c" * 40,
):
    identity = ScanEvidenceIdentity(origin, project_id, repository_id)
    return (identity.item_key(), origin, project_id, repository_id, commit, origin)


def fake_db(monkeypatch, rows):
    connection = FakeConnection(rows)
    monkeypatch.setattr(
        approved_sources.psycopg,
        "connect",
        lambda *args, **kwargs: connection,
    )
    return connection


def test_discovers_approved_canonical_targets_and_wires_to_graph(monkeypatch):
    scan, evidence = source_evidence()
    record = target_row()
    db = fake_db(monkeypatch, [record])
    approved, targets = approved_sources.load_approved_code_scans("fake-db", evidence)
    assert len(approved) == 1
    assert approved[0].repo_id == record[3]
    assert targets[record[3]].item_key == record[0]
    assert db.parameters == (evidence.item_key,)
    assert "r.acl_verified = TRUE" in db.query
    assert "r.archive_status = 'ARCHIVED'" in db.query
    assert "i.deleted_at IS NULL" in db.query

    manifest = '{"dependencies":{"shared":"git+https://code.example.test/teams/target.git"}}'
    links = resolve_dependencies(
        ApprovedRepository(evidence.identity.repository_id, evidence.source_url, scan),
        approved_repositories=approved,
        read_blob=lambda path: manifest,
    )
    assert len(links) == 1
    assert links[0].target_repo == record[3]
    assert links[0].target_commit == record[4]
    edges = graph.project_declared_dependencies(scan, evidence, links, targets)
    assert len(edges) == 1
    assert edges[0].kind == "repository_dependency"
    assert edges[0].target.anchor == record[0]


def test_excludes_ambiguous_repository_id_across_projects(monkeypatch):
    _, source = source_evidence()
    first = target_row(origin="https://code.example.test/org-one/shared")
    second = target_row(origin="https://code.example.test/org-two/shared", project_id="project-two")
    fake_db(monkeypatch, [first, second])
    assert approved_sources.load_approved_code_scans("fake-db", source) == ((), {})


def test_excludes_ambiguous_origin_across_repository_ids(monkeypatch):
    _, source = source_evidence()
    first = target_row()
    second = target_row(repository_id="another-id")
    fake_db(monkeypatch, [first, second])
    assert approved_sources.load_approved_code_scans("fake-db", source) == ((), {})


def test_forged_identity_or_unapproved_url_is_not_imported(monkeypatch):
    _, source = source_evidence()
    valid = target_row()
    wrong_item_key = ("f" * 64, *valid[1:])
    with_credentials = target_row(origin="https://token:secret@code.example.test/private")
    fake_db(monkeypatch, [wrong_item_key, with_credentials])
    assert approved_sources.load_approved_code_scans("fake-db", source) == ((), {})


def test_unverified_source_never_creates_cross_repo_edges(monkeypatch):
    _, source = source_evidence()
    fake_db(monkeypatch, [target_row()])
    unverified = SimpleNamespace(acl_verified=False, item_key=source.item_key)
    assert approved_sources.load_approved_code_scans("fake-db", unverified) == ((), {})


def test_provider_failure_is_not_masked_as_empty_inventory(monkeypatch):
    _, source = source_evidence()

    def fail(*args, **kwargs):
        raise RuntimeError("database inaccessible")

    monkeypatch.setattr(approved_sources.psycopg, "connect", fail)
    with pytest.raises(RuntimeError, match="database inaccessible"):
        approved_sources.load_approved_code_scans("fake-db", source)
