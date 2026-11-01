"""N-17: code-scan evidence records which ADO project may read it."""

import os
import subprocess
import uuid
from datetime import UTC, datetime
from pathlib import Path

import pytest

from app.code_intelligence.canonical import (
    ScanEvidenceIdentity,
    ado_project_principal,
    build_scan_evidence,
)
from app.code_intelligence.scanner import scan_repository

REPO = Path(__file__).resolve().parents[2]
ORG = "https://dev.azure.com/contoso"
ENV = {
    "GIT_AUTHOR_NAME": "t",
    "GIT_AUTHOR_EMAIL": "t@t",
    "GIT_COMMITTER_NAME": "t",
    "GIT_COMMITTER_EMAIL": "t@t",
}


def _scan(tmp_path: Path):
    root = tmp_path / "repo"
    root.mkdir()
    (root / "a.py").write_text("x = 1\n", encoding="utf-8")
    for args in (["init", "-q"], ["add", "."], ["commit", "-qm", "init"]):
        subprocess.run(["git", "-C", str(root), *args], check=True, env={**os.environ, **ENV})
    return scan_repository(root)


def test_the_principal_format_matches_the_dotnet_api() -> None:
    # Must equal the .NET AdoAccessPrincipals.Project(org, project), or nobody could read the code.
    source = (REPO / "src/VisionIQ.Api/Features/Evidence/AdoProjectAccess.cs").read_text()
    assert '"ado-project:" + sourceInstance.TrimEnd(\'/\') + "/" + project' in source
    assert ado_project_principal(ORG + "/", "Payments") == f"ado-project:{ORG}/Payments"


def test_with_an_approved_organization_the_code_is_readable_by_its_project(tmp_path: Path) -> None:
    identity = ScanEvidenceIdentity("https://example.com/r.git", "Payments", "repo-1")
    evidence = build_scan_evidence(
        _scan(tmp_path),
        identity,
        "https://example.com/r.git",
        datetime.now(UTC),
        ado_organization=ORG,
    )

    assert evidence.acl_verified is True
    assert evidence.acl_principals == (f"ado-project:{ORG}/Payments",)


def test_without_an_organization_the_code_stays_hidden(tmp_path: Path) -> None:
    identity = ScanEvidenceIdentity("https://example.com/r.git", "Payments", "repo-1")
    evidence = build_scan_evidence(
        _scan(tmp_path), identity, "https://example.com/r.git", datetime.now(UTC)
    )

    assert (evidence.acl_verified, evidence.acl_principals) == (False, ())


def test_an_unsafe_organization_is_rejected() -> None:
    with pytest.raises(ValueError):
        ado_project_principal("http://dev.azure.com/contoso", "Payments")


@pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
def test_the_store_persists_the_verified_access_list(tmp_path: Path) -> None:
    psycopg = pytest.importorskip("psycopg")
    from psycopg.conninfo import make_conninfo

    from app.code_intelligence.canonical import PostgresScanEvidenceStore

    def conninfo(prefix: str) -> str:
        return make_conninfo(
            "",
            host=os.environ.get("DB_HOST", "127.0.0.1"),
            dbname=os.environ.get("DB_NAME", "visioniq"),
            user=os.environ[f"DB_{prefix}_USER"],
            password=os.environ[f"DB_{prefix}_PASSWORD"],
        )

    identity = ScanEvidenceIdentity(
        "https://example.com/r.git", "Payments", "r-" + uuid.uuid4().hex[:8]
    )
    evidence = build_scan_evidence(
        _scan(tmp_path),
        identity,
        "https://example.com/r.git",
        datetime.now(UTC),
        ado_organization=ORG,
    )
    PostgresScanEvidenceStore(conninfo("INTELLIGENCE")).save(evidence, "gs://test/x")

    with psycopg.connect(conninfo("API")) as connection:
        row = connection.execute(
            "SELECT acl_verified, acl_principals FROM evidence.source_revisions "
            "WHERE item_key = %s",
            (evidence.item_key,),
        ).fetchone()
    assert row == (True, [f"ado-project:{ORG}/Payments"])
