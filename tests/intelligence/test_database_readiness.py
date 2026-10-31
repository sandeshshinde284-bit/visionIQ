"""FND-04 readiness tests: real DB role selection and fail-closed errors."""

import os

import pytest
from fastapi.testclient import TestClient

from app.api import readiness
from app.config import Settings
from app.main import create_app


class FakeConnection:
    """A scoped in-memory SELECT 1 response with no provider calls."""

    def __enter__(self) -> "FakeConnection":
        """Act like a successful psycopg connection context."""
        return self

    def __exit__(self, *_args: object) -> None:
        """Release the fake connection without side effects."""

    def execute(self, query: str) -> "FakeConnection":
        """Allow only the literal read-only readiness statement."""
        assert query == "SELECT 1"
        return self

    def fetchone(self) -> tuple[int]:
        """Return the expected PostgreSQL scalar."""
        return (1,)


def test_intelligence_ready_uses_only_its_runtime_db_identity(
    monkeypatch: pytest.MonkeyPatch, test_settings: Settings
) -> None:
    """Readiness proves SELECT 1 without exposing a secret or migration login."""
    values: dict[str, object] = {}

    def fake_connect(**kwargs: object) -> FakeConnection:
        """Record the configured least-privileged identity for assertions."""
        values.update(kwargs)
        return FakeConnection()

    monkeypatch.setattr(readiness.psycopg, "connect", fake_connect)
    with TestClient(create_app(test_settings)) as client:
        response = client.get("/ready")

    assert response.status_code == 200
    assert response.json() == {"status": "ready"}
    assert values["user"] == "visioniq_intelligence"
    assert values["password"] == "test-only-intelligence-password"
    assert "test-only-intelligence-password" not in response.text


def test_database_failure_is_503_without_leaking_provider_errors(
    monkeypatch: pytest.MonkeyPatch, test_settings: Settings
) -> None:
    """Database errors remain indistinguishable to an authenticated caller."""

    def fake_failure(**_kwargs: object) -> FakeConnection:
        """Simulate a failure containing confidential provider details."""
        raise RuntimeError("password: should-never-be-exposed")

    monkeypatch.setattr(readiness.psycopg, "connect", fake_failure)
    with TestClient(create_app(test_settings)) as client:
        response = client.get("/ready")

    assert response.status_code == 503
    assert response.json() == {"detail": "database_unavailable"}
    assert "should-never-be-exposed" not in response.text


@pytest.mark.skipif(
    os.environ.get("VISIONIQ_READY_INTELLIGENCE_DB_INTEGRATION") != "1",
    reason="Only run against disposable CI PostgreSQL with isolated database roles",
)
def test_actual_intelligence_role_can_select_from_disposable_database() -> None:
    """Exercise the same /ready route with a real restricted PostgreSQL login."""
    password = os.environ["DB_INTELLIGENCE_PASSWORD"]
    settings = Settings(
        _env_file=None,
        db_host=os.environ.get("DB_HOST", "127.0.0.1"),
        db_port=5432,
        db_name=os.environ.get("DB_LOCAL_NAME", "visioniq"),
        db_intelligence_user=os.environ.get("DB_INTELLIGENCE_USER", "visioniq_intelligence"),
        db_intelligence_password=password,
        db_ssl_mode="disable",
    )
    with TestClient(create_app(settings)) as client:
        response = client.get("/ready")
    assert response.status_code == 200
    assert response.json() == {"status": "ready"}
