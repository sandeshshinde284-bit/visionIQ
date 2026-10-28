"""Application settings.

Values come from environment variables. For local runs on the host, a repository-root ``.env``
file is also read if it exists; real environment variables always win. Inside Docker no ``.env``
is copied into the image, so the container environment (set in docker-compose.yml) is the only
source.
"""

from functools import lru_cache
from pathlib import Path
from typing import Literal

from pydantic import Field, SecretStr, field_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

LogLevel = Literal["DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"]
SslMode = Literal["disable", "allow", "prefer", "require", "verify-ca", "verify-full"]


def find_env_file(start: Path | None = None) -> Path | None:
    """Walk up from this file to the repository root and return the first ``.env`` found."""
    for directory in (start or Path(__file__).resolve()).parents:
        candidate = directory / ".env"
        if candidate.is_file():
            return candidate
        if (directory / ".git").exists():
            return None
    return None


class Settings(BaseSettings):
    """Typed, validated configuration for the Intelligence service."""

    model_config = SettingsConfigDict(
        env_file=find_env_file(),
        env_file_encoding="utf-8",
        extra="ignore",  # the shared .env contains settings for other services
        env_ignore_empty=True,  # a blank value in .env means "use the default"
        case_sensitive=False,
    )

    service_name: str = "VisionIQ.Intelligence"
    app_env: Literal["development", "test", "production"] = "development"
    app_log_level: LogLevel = "INFO"
    python_api_host: str = "127.0.0.1"
    python_api_port: int = Field(default=8000, ge=1, le=65535)
    request_timeout_seconds: int = Field(default=30, ge=1, le=600)

    db_host: str = "localhost"
    db_port: int = Field(default=5432, ge=1, le=65535)
    db_name: str = "visioniq"
    db_user: str = "visioniq"
    db_password: SecretStr | None = None
    db_ssl_mode: SslMode = "disable"

    @field_validator("app_env", "db_ssl_mode", mode="before")
    @classmethod
    def _lowercase(cls, value: object) -> object:
        return value.lower() if isinstance(value, str) else value

    @field_validator("app_log_level", mode="before")
    @classmethod
    def _uppercase(cls, value: object) -> object:
        return value.upper() if isinstance(value, str) else value


@lru_cache
def get_settings() -> Settings:
    """Return the process-wide settings (cached)."""
    return Settings()
