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


def stored_access_only(item_key: str) -> bool:
    """The brief's live access check.

    A brief has no human asker: it is written for the ticket's project and limited to evidence
    whose stored access list names that project's scope. Intelligence cannot ask ADO or Confluence
    whether that is still true right now, so this accepts what the stored list says. Retrieval
    still drops anything deleted, re-scoped or no longer verified since it was indexed.
    """
    return True


def build_analysis_worker(settings: Settings, context: EventContext):
    """The BRF-03 worker with its real dependencies (database, embedder, model gateway)."""
    from app.brief.history import PostgresBriefSources
    from app.brief.pipeline import BriefSettings
    from app.brief.worker import AnalysisWorker
    from app.db import connect
    from app.knowledge import embeddings
    from app.llm.factory import build_gateway
    from app.llm.types import DataClassification

    embedder = embeddings.from_environment()

    def open_connection():
        return connect(settings)

    return AnalysisWorker(
        open_connection,
        lambda scope: PostgresBriefSources(open_connection, embedder, [scope], stored_access_only),
        build_gateway(settings),
        BriefSettings(
            classification=DataClassification(settings.llm_data_classification),
            evidence_limit=settings.brief_evidence_limit,
            risk_window_days=settings.brief_risk_window_days,
            max_calls=settings.llm_max_calls_per_run,
            max_tokens=settings.llm_max_tokens_per_run,
            max_seconds=settings.llm_max_seconds_per_run,
        ),
        context,
    )


def build_fix_worker(settings: Settings, context: EventContext):
    """The DEF-01 worker with its real dependencies (database, policy, runner, model gateway)."""
    import tempfile
    from pathlib import Path

    from app.brief.history import PostgresBriefSources
    from app.db import connect
    from app.defect.pipeline import FixSettings
    from app.defect.policy import load_policy
    from app.defect.sources import PostgresFixSources
    from app.defect.worker import FixWorker
    from app.knowledge import embeddings
    from app.llm.factory import build_gateway
    from app.llm.types import DataClassification

    if settings.defect_policy_file is None:
        raise ValueError("DEFECT_POLICY_FILE is required when DEFECT_PIPELINE_ENABLED is true.")
    policy = load_policy(settings.defect_policy_file)
    # Refused unless it was allowed on purpose: a forgotten APP_ENV must not be enough to run
    # model-written code on the host.
    production = settings.app_env == "production" or not settings.defect_allow_local_runner
    if production and any(repo.runner.kind == "local" for repo in policy.repositories):
        raise ValueError(
            "The local runner has no network isolation. Use a docker runner, or set "
            "DEFECT_ALLOW_LOCAL_RUNNER=true on a developer machine (never in production)."
        )
    embedder = embeddings.from_environment()
    work_dir = settings.defect_work_dir or Path(tempfile.gettempdir()) / "visioniq-fix"

    def open_connection():
        return connect(settings)

    def sources_for(scope: str):
        brief_sources = PostgresBriefSources(open_connection, embedder, [scope], stored_access_only)
        return PostgresFixSources(open_connection, brief_sources, [scope])

    return FixWorker(
        open_connection,
        sources_for,
        build_gateway(settings),
        policy,
        FixSettings(
            work_dir=work_dir / "work",
            artifact_dir=settings.defect_artifact_dir or work_dir / "artifacts",
            classification=DataClassification(settings.llm_data_classification),
            max_calls=settings.defect_max_calls_per_run,
            max_tokens=settings.defect_max_tokens_per_run,
            max_seconds=settings.defect_max_seconds_per_run,
            keep_workspace=settings.defect_keep_workspaces,
            bot_name=settings.defect_bot_name,
            bot_email=settings.defect_bot_email,
            artifact_base_uri=settings.defect_artifact_base_uri,
            production=production,
            repo_access_token=(
                settings.defect_repo_read_pat.get_secret_value()
                if settings.defect_repo_read_pat
                else None
            ),
            repo_token_hosts=tuple(
                host.strip() for host in settings.defect_repo_token_hosts.split(",") if host.strip()
            ),
        ),
        context,
    )


@asynccontextmanager
async def lifespan(app: FastAPI) -> AsyncIterator[None]:
    """Startup and shutdown hooks. Background work is opt-in: the outbox dispatcher contacts
    Pub/Sub, the analysis worker contacts the model providers, the fix worker also runs the
    sandbox, the hotspot schedule only the database."""
    settings: Settings = app.state.settings
    settings.require_runtime_database_identity()
    configure_logging(settings.app_log_level)
    log = structlog.get_logger()
    log.info("service_starting", service=settings.service_name, environment=settings.app_env)
    tasks: list[asyncio.Task[None]] = []
    if settings.events_dispatch_enabled:
        if not settings.pubsub_project_id:
            raise ValueError("PUBSUB_PROJECT_ID is required when EVENTS_DISPATCH_ENABLED is true.")
        publisher = PubSubPublisher(settings.pubsub_project_id)
        tasks.append(asyncio.create_task(run_outbox_dispatcher(settings, publisher)))
    if settings.brief_pipeline_enabled:
        from app.brief.worker import run_analysis_worker

        worker = build_analysis_worker(settings, app.state.event_context)
        tasks.append(
            asyncio.create_task(run_analysis_worker(worker, settings.brief_worker_interval_seconds))
        )
    if settings.defect_pipeline_enabled:
        from app.defect.worker import run_fix_worker

        fix_worker = build_fix_worker(settings, app.state.event_context)
        tasks.append(
            asyncio.create_task(run_fix_worker(fix_worker, settings.defect_worker_interval_seconds))
        )
    else:
        # Deep requests accepted while the pipeline was on would wait for ever: answer them.
        from app.defect.worker import answer_stranded_requests

        tasks.append(
            asyncio.create_task(
                asyncio.to_thread(answer_stranded_requests, settings, app.state.event_context)
            )
        )
    if settings.hotspot_sweep_interval_hours > 0:
        from app.hotspots.cli import run_scheduled_sweeps

        tasks.append(asyncio.create_task(run_scheduled_sweeps(settings)))
    yield
    for task in tasks:
        task.cancel()
    for task in tasks:
        with contextlib.suppress(asyncio.CancelledError):
            await task
    log.info("service_stopping", service=settings.service_name)


def create_app(settings: Settings | None = None) -> FastAPI:
    """Build the application. Tests pass their own settings so no local .env is needed."""
    resolved = settings or get_settings()
    app = FastAPI(title="VisionIQ Intelligence", version="0.1.0", lifespan=lifespan)
    app.state.settings = resolved
    contracts = get_contracts(resolved.event_contracts_dir)
    app.state.event_context = EventContext(
        contracts,
        load_topology(contracts.directory),
        resolved.brief_pipeline_enabled,
        resolved.defect_pipeline_enabled,
    )
    app.state.event_processor = PostgresEventProcessor(resolved, app.state.event_context)
    app.dependency_overrides[get_settings] = lambda: resolved
    app.include_router(health.router)
    app.include_router(readiness.router)
    app.include_router(events.router)
    return app


app = create_app()
