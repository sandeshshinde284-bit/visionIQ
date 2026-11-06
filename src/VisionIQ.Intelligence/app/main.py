"""FastAPI application entry point.

Run locally (from src/VisionIQ.Intelligence):
    python -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --reload
"""

import asyncio
import contextlib
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

import structlog
from fastapi import FastAPI

from app.api import events, health, readiness
from app.config import Settings, get_settings
from app.events.contracts import get_contracts
from app.events.processing import EventContext, PostgresEventProcessor
from app.events.publisher import PubSubPublisher, run_outbox_dispatcher
from app.events.topology import load_topology
from app.logging_config import configure_logging


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    """Startup and shutdown hooks. Only the opt-in outbox dispatcher contacts Pub/Sub."""
    settings: Settings = app.state.settings
    settings.require_runtime_database_identity()
    configure_logging(settings.app_log_level)
    log = structlog.get_logger()
    log.info("service_starting", service=settings.service_name, environment=settings.app_env)
    dispatcher: asyncio.Task[None] | None = None
    if settings.events_dispatch_enabled:
        if not settings.pubsub_project_id:
            raise ValueError("PUBSUB_PROJECT_ID is required when EVENTS_DISPATCH_ENABLED is true.")
        publisher = PubSubPublisher(settings.pubsub_project_id)
        dispatcher = asyncio.create_task(run_outbox_dispatcher(settings, publisher))
    yield
    if dispatcher is not None:
        dispatcher.cancel()
        with contextlib.suppress(asyncio.CancelledError):
            await dispatcher
    log.info("service_stopping", service=settings.service_name)


def create_app(settings: Settings | None = None) -> FastAPI:
    """Build the application. Tests pass their own settings so no local .env is needed."""
    resolved = settings or get_settings()
    app = FastAPI(title="VisionIQ Intelligence", version="0.1.0", lifespan=lifespan)
    app.state.settings = resolved
    contracts = get_contracts(resolved.event_contracts_dir)
    app.state.event_context = EventContext(contracts, load_topology(contracts.directory))
    app.state.event_processor = PostgresEventProcessor(resolved, app.state.event_context)
    app.dependency_overrides[get_settings] = lambda: resolved
    app.include_router(health.router)
    app.include_router(readiness.router)
    app.include_router(events.router)
    return app


app = create_app()
