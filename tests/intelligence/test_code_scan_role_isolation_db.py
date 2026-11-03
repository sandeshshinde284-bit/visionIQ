"""B-15: the scanner writes with its own login, limited to its own evidence.

Requires a bootstrapped database (python -m db.manage all) and
VISIONIQ_DB_INTEGRATION=1, with DB_API_* and DB_INTELLIGENCE_* credentials.
"""

import os
import subprocess
import uuid
from datetime import UTC, datetime
from pathlib import Path

import pytest

psycopg = pytest.importorskip("psycopg")
from psycopg.conninfo import make_conninfo  # noqa: E402

from app.code_intelligence.canonical import (  # noqa: E402
    PostgresScanEvidenceStore,
    ScanEvidenceIdentity,
    build_scan_evidence,
    ingest_complete_scan,
)
from app.code_intelligence.graph import persist_code_graph  # noqa: E402
from app.code_intelligence.scanner import scan_repository  # noqa: E402

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
ADO = "https://dev.azure.com/role-isolation-test"


def _conninfo(prefix: str) -> str:
    return make_conninfo(
        "",
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ[f"DB_{prefix}_USER"],
        password=os.environ[f"DB_{prefix}_PASSWORD"],
    )


class _Archive:
    def store(self, evidence) -> str:
        return f"gs://isolation-test/{evidence.item_key}"


def _repo(tmp_path: Path) -> tuple[Path, str]:
    root = tmp_path / "repo"
    root.mkdir()
    (root / "app.py").write_text("def handler():\n    return 1\n", encoding="utf-8")
    env = {
        **os.environ,
        "GIT_AUTHOR_NAME": "t",
        "GIT_AUTHOR_EMAIL": "t@t",
        "GIT_COMMITTER_NAME": "t",
        "GIT_COMMITTER_EMAIL": "t@t",
    }
    for args in (["init", "-q"], ["add", "."], ["commit", "-qm", "init"]):
        subprocess.run(["git", "-C", str(root), *args], check=True, env=env)
    sha = subprocess.run(
        ["git", "-C", str(root), "rev-parse", "HEAD"], capture_output=True, text=True, check=True
    ).stdout.strip()
    return root, sha


def _ado_commit_item(project: str, repository: str, sha: str) -> str:
    """Create an ADO commit evidence row as the API role (the owner of ADO evidence)."""
    key = uuid.uuid4().hex + uuid.uuid4().hex
    with psycopg.connect(_conninfo("API")) as connection:
        connection.execute(
            """INSERT INTO evidence.source_items (item_key, provider, source_instance, project_id,
               repository_id, kind, external_id, first_observed_at, last_observed_at)
               VALUES (%s, 'azure_devops', %s, %s, %s, 'commit', %s, now(), now())""",
            (key, ADO, project, repository, sha),
        )
        connection.execute(
            """INSERT INTO evidence.source_revisions (item_key, provider_revision_id,
               source_updated_at, observed_at, content_sha256, source_url,
               retrieved_under_principal, redaction_version, normalizer_version, raw_snapshot_uri)
               VALUES (%s, %s, now(), now(), repeat('0', 64), 'https://x', 't', 'v', 'v', 'gs://t/x')""",
            (key, sha),
        )
        connection.execute(
            "UPDATE evidence.source_items SET latest_revision_id = %s, "
            "latest_source_updated_at = now() WHERE item_key = %s",
            (sha, key),
        )
    return key


def test_scanner_login_runs_the_full_pipeline_including_the_ado_bridge(tmp_path: Path) -> None:
    root, sha = _repo(tmp_path)
    project, repository = "proj-" + uuid.uuid4().hex[:8], "repo-" + uuid.uuid4().hex[:8]
    _ado_commit_item(project, repository, sha)
    scan = scan_repository(root)
    identity = ScanEvidenceIdentity("https://example.com/r.git", project, repository)
    committed = datetime.now(UTC)
    intel = _conninfo("INTELLIGENCE")

    status = ingest_complete_scan(
        scan,
        identity,
        "https://example.com/r.git",
        committed,
        _Archive(),
        PostgresScanEvidenceStore(intel),
    )
    evidence = build_scan_evidence(scan, identity, "https://example.com/r.git", committed)
    edges = persist_code_graph(scan, evidence, intel, approved_ado_instance=ADO)

    assert status == "CREATED"
    assert edges >= 2  # commit->file from the scan, plus the bridged ADO commit->file


@pytest.mark.parametrize(
    "statement",
    [
        """INSERT INTO evidence.source_items (item_key, provider, source_instance, project_id,
           kind, external_id, first_observed_at, last_observed_at)
           VALUES (repeat('e', 64), 'azure_devops', 'x', 'p', 'work_item_current', '1',
           now(), now())""",
        "INSERT INTO evidence.connector_checkpoints VALUES ('x', 'y', 'z', now())",
        "DELETE FROM evidence.source_items",
    ],
)
def test_scanner_login_is_refused_outside_code_scan_evidence(statement: str) -> None:
    with (
        psycopg.connect(_conninfo("INTELLIGENCE")) as connection,
        pytest.raises(psycopg.errors.InsufficientPrivilege),
    ):
        connection.execute(statement)


def test_scanner_login_cannot_modify_ado_evidence() -> None:
    key = _ado_commit_item("p-" + uuid.uuid4().hex[:6], "r-" + uuid.uuid4().hex[:6], "f" * 40)
    with psycopg.connect(_conninfo("INTELLIGENCE")) as connection:
        changed = connection.execute(
            "UPDATE evidence.source_items SET last_observed_at = now() WHERE item_key = %s", (key,)
        ).rowcount
    assert changed == 0  # row-level security hides ADO rows from the scanner's updates
