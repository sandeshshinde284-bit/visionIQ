"""Builds the model gateway from settings: which providers exist and what data each may see."""

from __future__ import annotations

import json
from collections.abc import Callable

import structlog

from app.config import Settings
from app.llm.gateway import ModelGateway
from app.llm.providers.offline import OfflineProvider
from app.llm.routing import ROUTING_REVISION, RoutingPolicy
from app.llm.types import DataClassification, ModelProvider


def _gemini(settings: Settings) -> ModelProvider:
    from app.llm.providers.gemini import GeminiProvider

    assert settings.gcp_project_id is not None
    return GeminiProvider(settings.gcp_project_id, settings.vertex_location, settings.gemini_model)


def _claude(settings: Settings) -> ModelProvider | None:
    from app.llm.providers.claude import ClaudeProvider

    if settings.claude_route == "vertex" and settings.gcp_project_id:
        return ClaudeProvider.for_vertex(
            settings.gcp_project_id,
            settings.claude_vertex_region,
            settings.claude_model,
            settings.claude_effort,
        )
    if settings.claude_route == "anthropic":
        return ClaudeProvider.for_anthropic_api(
            settings.claude_model, settings.claude_effort, settings.claude_refusal_fallback
        )
    return None


def _build(name: str, make: Callable[[], ModelProvider | None]) -> ModelProvider | None:
    """A provider, or None (logged) when it cannot be set up, e.g. missing credentials."""
    try:
        return make()
    except Exception as error:
        # Type only: credential errors can quote paths and account names.
        structlog.get_logger().error(
            "llm_provider_unavailable", provider=name, error_type=type(error).__name__
        )
        return None


def _offline_code_script(settings: Settings) -> dict | None:
    """Prepared answers for the code tasks of an offline demo (DEFECT_OFFLINE_SCRIPT)."""
    path = settings.defect_offline_script
    if path is None:
        return None
    try:
        script = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        structlog.get_logger().error(
            "defect_offline_script_unreadable", error_type=type(error).__name__
        )
        return None
    return script if isinstance(script, dict) else None


def build_gateway(settings: Settings) -> ModelGateway:
    """A provider that cannot be configured is left out; routing then reports no allowed
    provider and the brief is PARTIAL, rather than the service failing to start."""
    log = structlog.get_logger()
    providers: dict[str, ModelProvider] = {}
    access: dict[str, DataClassification] = {}
    override: ModelProvider | None = None

    if settings.llm_mode == "offline":
        override = OfflineProvider(_offline_code_script(settings))
    else:
        if settings.gcp_project_id:
            gemini = _build("gemini", lambda: _gemini(settings))
            if gemini is not None:
                providers["gemini"] = gemini
                access["gemini"] = DataClassification(settings.llm_gemini_max_classification)
        # D04: Claude receives data only when a route AND a classification limit are both set.
        if settings.claude_route != "off" and settings.llm_claude_max_classification:
            claude = _build("claude", lambda: _claude(settings))
            if claude is not None:
                providers["claude"] = claude
                access["claude"] = DataClassification(settings.llm_claude_max_classification)

    log.info(
        "llm_gateway_ready",
        mode=settings.llm_mode,
        routing_revision=ROUTING_REVISION,
        providers={
            name: f"{p.model_id} (up to {access[name].value})" for name, p in providers.items()
        },
        claude_route=settings.claude_route,
    )
    return ModelGateway(
        RoutingPolicy(providers, access, override=override),
        call_timeout_seconds=settings.llm_call_timeout_seconds,
        max_output_tokens=settings.llm_max_output_tokens,
    )
