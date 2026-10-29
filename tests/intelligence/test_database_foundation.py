"""Isolated unit checks and opt-in real PostgreSQL permission test."""
import os

import pytest

from db.manage import LocalConfig, verify


def test_requires_distinct_runtime_users() -> None:
    settings = LocalConfig(
        _env_file=None,
        db_target="local",
        db_local_user="visioniq",
        db_local_password="admin-secret",
        db_api_user="visioniq_api",
        db_api_password="api-secret",
        db_intelligence_user="visioniq_intelligence",
        db_intelligence_password="intelligence-secret",
    )
    settings.check()
    assert settings.url().username == "visioniq"
    assert "admin-secret" not in str(settings.url())


def test_rejects_password_reuse() -> None:
    settings = LocalConfig(
        _env_file=None,
        db_target="local",
        db_local_password="same",
        db_api_password="same",
        db_intelligence_password="different",
    )
    with pytest.raises(ValueError, match="different"):
        settings.check()


@pytest.mark.skipif(
    os.getenv("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
def test_live_permission_isolation() -> None:
    verify(LocalConfig())
