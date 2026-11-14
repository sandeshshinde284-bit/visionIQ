"""Shared fixtures. Tests never read a developer's real .env and never contact external services."""

import os
import subprocess
import sys
import uuid
import warnings
from collections.abc import Iterator
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from app.config import Settings, get_settings
from app.main import create_app


@pytest.fixture(autouse=True)
def _fresh_settings_cache() -> Iterator[None]:
    get_settings.cache_clear()
    yield
    get_settings.cache_clear()


@pytest.fixture
def test_settings() -> Settings:
    return Settings(
        _env_file=None,
        app_env="test",
        db_intelligence_user="visioniq_intelligence",
        db_intelligence_password="test-only-intelligence-password",
    )


@pytest.fixture
def client(test_settings: Settings) -> Iterator[TestClient]:
    # The context manager runs the application's startup and shutdown hooks.
    with TestClient(create_app(test_settings)) as test_client:
        yield test_client


# ---- One throwaway database per test for the knowledge-index tests ------------------------------
#
# A full-scope snapshot is published only when it covers at least 95% of ALL eligible evidence in
# the database (app.knowledge.snapshots.MIN_FULL_COVERAGE). That is right for the product and
# makes these tests depend on every row another test left behind: run after other database tests,
# their snapshots are rejected and they find nothing served.
#
# So each test of the modules below runs in its own database, cloned from a template that is
# bootstrapped once per session exactly as a real one is (python -m db.manage all). Only databases
# created here are ever dropped: existing data is never emptied or changed. Without the
# administrator's credentials (DB_LOCAL_USER / DB_LOCAL_PASSWORD) nothing is isolated and the
# tests use DB_NAME as before.

ISOLATED_MODULES = frozenset(
    {
        "test_search_index_db",
        "test_retrieval_db",
        "test_kb_readiness_db",
        "test_lrn13_db",
        "test_lrn13_manual_db",
        "test_snapshots_db",
        "test_lessons_indexed_db",
        "test_brief_pipeline_db",
    }
)
REPOSITORY_ROOT = Path(__file__).resolve().parents[2]


def _administrator():
    """A connection to the server's maintenance database, or None without credentials."""
    password = os.environ.get("DB_LOCAL_PASSWORD")
    if os.environ.get("VISIONIQ_DB_INTEGRATION") != "1" or not password:
        return None
    import psycopg
    from psycopg.conninfo import make_conninfo

    return psycopg.connect(
        make_conninfo(
            "",
            host=os.environ.get("DB_HOST", "127.0.0.1"),
            port=int(os.environ.get("DB_PORT", "5432")),
            dbname="postgres",
            user=os.environ.get("DB_LOCAL_USER", "visioniq"),
            password=password,
        ),
        autocommit=True,
    )


def _drop(name: str) -> None:
    from psycopg import sql

    connection = _administrator()
    if connection is not None:
        with connection:
            connection.execute(
                sql.SQL("DROP DATABASE IF EXISTS {} WITH (FORCE)").format(sql.Identifier(name))
            )


@pytest.fixture(scope="session")
def knowledge_database_template() -> Iterator[str | None]:
    """A freshly bootstrapped, empty database to clone; None when isolation is not possible."""
    connection = _administrator()
    if connection is None:
        yield None
        return
    from psycopg import sql

    name = f"visioniq_tpl_{uuid.uuid4().hex[:12]}"
    with connection:
        connection.execute(sql.SQL("CREATE DATABASE {}").format(sql.Identifier(name)))
    bootstrap = subprocess.run(
        [sys.executable, "-m", "db.manage", "all"],
        cwd=REPOSITORY_ROOT,
        env={**os.environ, "DB_TARGET": "local", "DB_LOCAL_NAME": name},
        capture_output=True,
        text=True,
        check=False,
    )
    if bootstrap.returncode != 0:
        _drop(name)
        raise RuntimeError(
            "The test database template could not be bootstrapped (python -m db.manage all): "
            + (bootstrap.stderr or bootstrap.stdout)[-600:]
        )
    try:
        yield name
    finally:
        _drop(name)


@pytest.fixture(autouse=True)
def _own_knowledge_database(
    request: pytest.FixtureRequest, monkeypatch: pytest.MonkeyPatch
) -> Iterator[None]:
    module = getattr(request.module, "__name__", "").rsplit(".", 1)[-1]
    if module not in ISOLATED_MODULES:
        yield
        return
    template = request.getfixturevalue("knowledge_database_template")
    if template is None:
        if os.environ.get("VISIONIQ_DB_INTEGRATION") == "1":
            warnings.warn(
                "Knowledge-index database tests are NOT isolated: set DB_LOCAL_USER and "
                "DB_LOCAL_PASSWORD (the database administrator) so each test gets its own "
                "database. In a shared database they fail once other tests have left rows.",
                stacklevel=1,
            )
        yield
        return
    from psycopg import sql

    name = f"visioniq_t_{uuid.uuid4().hex[:12]}"
    with _administrator() as connection:
        connection.execute(
            sql.SQL("CREATE DATABASE {} TEMPLATE {}").format(
                sql.Identifier(name), sql.Identifier(template)
            )
        )
    monkeypatch.setenv("DB_NAME", name)
    monkeypatch.setenv("DB_LOCAL_NAME", name)
    try:
        yield
    finally:
        _drop(name)
