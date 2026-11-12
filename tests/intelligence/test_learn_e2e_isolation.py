"""LRN-08 regression: synthetic/hashing E2E fixtures must never touch the real canonical database.

A previous local run let a HashingEmbedder test snapshot get published into the main database,
blocking real Vertex indexing until it was manually diagnosed. isolated_fixture_database() in
scripts/learn_e2e_stages.py is the fix: every synthetic run gets its own throwaway database. These
tests prove the guard rails hold, not just that the helper exists.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
if str(ROOT / "scripts") not in sys.path:
    sys.path.insert(0, str(ROOT / "scripts"))

from learn_e2e_stages import isolated_fixture_database  # noqa: E402

psycopg = pytest.importorskip("psycopg")

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)


class _StubRunner:
    """Only run_command is exercised: it just needs to report a successful migrate/roles pass."""

    def run_command(self, command, env, log_path):
        log_path.write_text("stub migration ok\n", encoding="utf-8")
        return 0


def _base_env() -> dict:
    return {
        "DB_TARGET": "local",
        "DB_HOST": os.environ.get("DB_HOST", "localhost"),
        "DB_PORT": os.environ.get("DB_PORT", "5432"),
        "DB_LOCAL_USER": os.environ.get("DB_LOCAL_USER") or os.environ.get("DB_API_USER", ""),
        "DB_LOCAL_PASSWORD": (
            os.environ.get("DB_LOCAL_PASSWORD") or os.environ.get("DB_API_PASSWORD", "")
        ),
    }


@pytest.mark.parametrize(
    "overrides,expected_message",
    [
        ({"DB_TARGET": "remote"}, "local-only"),
        ({"DB_HOST": "db.example.internal"}, "loopback"),
        ({"DB_LOCAL_USER": "", "DB_LOCAL_PASSWORD": ""}, "DB_LOCAL_USER"),
    ],
)
def test_isolation_refuses_anything_that_is_not_a_local_throwaway_database(
    overrides: dict, expected_message: str, tmp_path: Path
) -> None:
    env = {**_base_env(), **overrides}
    with (
        pytest.raises(ValueError, match=expected_message),
        isolated_fixture_database(env, tmp_path, _StubRunner(), "probe"),
    ):
        pytest.fail("must never yield when a guard rejects the environment")


def test_isolated_database_is_a_fresh_throwaway_name_distinct_from_the_real_database(
    tmp_path: Path,
) -> None:
    env = _base_env()
    real_dbname = os.environ.get("DB_NAME", "visioniq")
    with isolated_fixture_database(env, tmp_path, _StubRunner(), "probe") as test_env:
        assert test_env["DB_NAME"] != real_dbname
        assert test_env["DB_NAME"].startswith("visioniq_e2e_")


def test_leaving_the_context_drops_only_the_database_it_created(tmp_path: Path) -> None:
    env = _base_env()
    with isolated_fixture_database(env, tmp_path, _StubRunner(), "probe") as test_env:
        created_name = test_env["DB_NAME"]
        admin_conn = psycopg.connect(
            host=env["DB_HOST"],
            port=int(env["DB_PORT"]),
            dbname="postgres",
            user=env["DB_LOCAL_USER"],
            password=env["DB_LOCAL_PASSWORD"],
            autocommit=True,
        )
        with admin_conn:
            exists_during = admin_conn.execute(
                "SELECT 1 FROM pg_database WHERE datname = %s", (created_name,)
            ).fetchone()
    assert exists_during is not None

    admin_conn = psycopg.connect(
        host=env["DB_HOST"],
        port=int(env["DB_PORT"]),
        dbname="postgres",
        user=env["DB_LOCAL_USER"],
        password=env["DB_LOCAL_PASSWORD"],
        autocommit=True,
    )
    with admin_conn:
        exists_after = admin_conn.execute(
            "SELECT 1 FROM pg_database WHERE datname = %s", (created_name,)
        ).fetchone()
    assert exists_after is None


def test_real_pointer_is_untouched_by_a_full_isolated_fixture_run(tmp_path: Path) -> None:
    """End-to-end regression for the exact incident: run_pytest's isolation must not leak out.

    Runs the real learn_e2e_stages.run_pytest() against test_snapshots_db.py (which calls
    snapshots.refresh with HashingEmbedder and publishes synthetic snapshots) and asserts the
    real database's published pointer is unchanged before and after.
    """
    from learn_e2e_stages import run_pytest
    from psycopg.conninfo import make_conninfo

    from app.knowledge import snapshots

    real_conninfo = make_conninfo(
        "",
        host=os.environ["DB_HOST"],
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ["DB_NAME"],
        user=os.environ["DB_INTELLIGENCE_USER"],
        password=os.environ["DB_INTELLIGENCE_PASSWORD"],
    )
    with psycopg.connect(real_conninfo) as conn:
        before = snapshots.current(conn)

    class _RealRunner:
        @staticmethod
        def result_add(results, name, status, detail):
            results.append((name, status, detail))

        @staticmethod
        def run_command(command, env, log_path):
            import subprocess

            with open(log_path, "w", encoding="utf-8") as fh:
                proc = subprocess.run(
                    command, env={**os.environ, **env}, stdout=fh, stderr=subprocess.STDOUT
                )
            return proc.returncode

    env = {
        **os.environ,
        **_base_env(),
        "DB_INTELLIGENCE_USER": os.environ["DB_INTELLIGENCE_USER"],
        "DB_INTELLIGENCE_PASSWORD": os.environ["DB_INTELLIGENCE_PASSWORD"],
        "PYTHONPATH": str(ROOT / "src" / "VisionIQ.Intelligence"),
    }
    results: list = []
    run_pytest(
        "tests/intelligence/test_snapshots_db.py", env, tmp_path, _RealRunner(), "probe", results
    )

    with psycopg.connect(real_conninfo) as conn:
        after = snapshots.current(conn)
    assert after == before, (
        "The real published snapshot pointer moved during an isolated synthetic pytest run: "
        f"before={before}, after={after}"
    )
