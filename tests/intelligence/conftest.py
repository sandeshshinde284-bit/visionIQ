"""Shared fixtures. Tests never read a developer's real .env and never contact external services."""

from collections.abc import Iterator

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
    return Settings(_env_file=None, app_env="test")


@pytest.fixture
def client(test_settings: Settings) -> Iterator[TestClient]:
    # The context manager runs the application's startup and shutdown hooks.
    with TestClient(create_app(test_settings)) as test_client:
        yield test_client
