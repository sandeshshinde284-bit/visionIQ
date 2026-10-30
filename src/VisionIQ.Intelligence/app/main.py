"""FastAPI application entry point.

Run locally (from src/VisionIQ.Intelligence):
    python -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --reload
"""

from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

import structlog
from fastapi import FastAPI

from app.api import health
from app.config import Settings, get_settings
from app.logging_config import configure_logging


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    """Startup and shutdown hooks. No external provider is contacted here."""
    settings: Settings = app.state.settings
    settings.require_runtime_database_identity()
    configure_logging(settings.app_log_level)
    log = structlog.get_logger()
    log.info("service_starting", service=settings.service_name, environment=settings.app_env)
    yield
    log.info("service_stopping", service=settings.service_name)


def create_app(settings: Settings | None = None) -> FastAPI:
    """Build the application. Tests pass their own settings so no local .env is needed."""
    resolved = settings or get_settings()
    app = FastAPI(title="VisionIQ Intelligence", version="0.1.0", lifespan=lifespan)
    app.state.settings = resolved
    app.dependency_overrides[get_settings] = lambda: resolved
    app.include_router(health.router)
    return app


app = create_app()
