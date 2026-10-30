import pytest
from pydantic import ValidationError

from app.config import Settings, find_env_file
from app.logging_config import configure_logging


def settings(**overrides: object) -> Settings:
    values: dict[str, object] = {
        "db_intelligence_user": "visioniq_intelligence",
        "db_intelligence_password": "test-runtime-secret",
    }
    values.update(overrides)
    return Settings(_env_file=None, **values)


def test_defaults_match_the_documented_local_setup() -> None:
    s = settings()

    assert (s.python_api_host, s.python_api_port) == ("127.0.0.1", 8000)
    assert (s.db_host, s.db_port, s.db_ssl_mode) == ("localhost", 5432, "disable")
    assert s.db_user == "visioniq_intelligence"
    assert s.db_password is not None
    assert s.db_password.get_secret_value() == "test-runtime-secret"


def test_environment_variables_override_defaults(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("PYTHON_API_PORT", "9100")
    monkeypatch.setenv("APP_LOG_LEVEL", "debug")
    monkeypatch.setenv("DB_HOST", "postgres")

    s = settings()

    assert s.python_api_port == 9100
    assert s.app_log_level == "DEBUG"
    assert s.db_host == "postgres"


def test_blank_values_fall_back_to_defaults(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("DB_PORT", "")

    assert settings().db_port == 5432


@pytest.mark.parametrize(
    "variable, value",
    [
        ("PYTHON_API_PORT", "0"),
        ("PYTHON_API_PORT", "70000"),
        ("DB_PORT", "not-a-number"),
        ("APP_LOG_LEVEL", "LOUD"),
        ("DB_SSL_MODE", "maybe"),
        ("APP_ENV", "staging"),
    ],
)
def test_invalid_values_are_rejected(
    monkeypatch: pytest.MonkeyPatch, variable: str, value: str
) -> None:
    monkeypatch.setenv(variable, value)

    with pytest.raises(ValidationError):
        settings()


def test_admin_fallback_credentials_are_ignored(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("DB_USER", "administrator")
    monkeypatch.setenv("DB_PASSWORD", "admin-secret-value")

    s = settings()

    assert s.db_user == "visioniq_intelligence"
    assert s.db_password is not None
    assert s.db_password.get_secret_value() == "test-runtime-secret"
    assert "test-runtime-secret" not in repr(s)
    assert "admin-secret-value" not in repr(s)


def test_missing_runtime_password_is_rejected() -> None:
    s = settings(db_intelligence_password=None, db_password="admin-secret")
    with pytest.raises(ValueError, match="DB_INTELLIGENCE_PASSWORD"):
        s.require_runtime_database_identity()


def test_missing_runtime_user_is_rejected() -> None:
    s = settings(db_intelligence_user=None, db_user="administrator")
    with pytest.raises(ValueError, match="DB_INTELLIGENCE_USER"):
        s.require_runtime_database_identity()


def test_find_env_file_returns_none_when_no_env_exists(tmp_path) -> None:
    nested = tmp_path / "a" / "b" / "file.py"
    nested.parent.mkdir(parents=True)
    (tmp_path / ".git").mkdir()

    assert find_env_file(nested) is None


def test_find_env_file_finds_the_nearest_env(tmp_path) -> None:
    nested = tmp_path / "a" / "b" / "file.py"
    nested.parent.mkdir(parents=True)
    env_file = tmp_path / ".env"
    env_file.write_text("DB_HOST=localhost\n", encoding="utf-8")

    assert find_env_file(nested) == env_file


def test_logging_configuration_accepts_known_and_unknown_levels() -> None:
    configure_logging("INFO")
    configure_logging("not-a-level")  # falls back to INFO instead of failing at startup
