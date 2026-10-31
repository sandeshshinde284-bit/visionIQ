"""Restricted database readiness endpoint, separate from process liveness.

Cloud Run must enforce authenticated invocation and internal ingress.
Do not return PostgreSQL exceptions, connection strings or credentials.
"""

from typing import Annotated

import psycopg
from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel

from app.config import Settings, get_settings

router = APIRouter(tags=["health"])


class DatabaseReadinessResponse(BaseModel):
    """Minimal success state without internal connection metadata."""

    status: str


@router.get("/ready", response_model=DatabaseReadinessResponse)
def database_ready(
    settings: Annotated[Settings, Depends(get_settings)],
) -> DatabaseReadinessResponse:
    """Verify the configured Intelligence-only database role can execute SELECT 1."""
    try:
        settings.require_runtime_database_identity()
        assert settings.db_intelligence_password is not None
        with psycopg.connect(
            host=settings.db_host,
            port=settings.db_port,
            dbname=settings.db_name,
            user=settings.db_intelligence_user,
            password=settings.db_intelligence_password.get_secret_value(),
            sslmode=settings.db_ssl_mode,
            connect_timeout=3,
        ) as connection:
            result = connection.execute("SELECT 1").fetchone()
        if result == (1,):
            return DatabaseReadinessResponse(status="ready")
    except Exception:
        # Database/network/credential failures are intentionally indistinguishable.
        # Do not return provider details or log secret-bearing exceptions.
        pass
    raise HTTPException(status_code=503, detail="database_unavailable")
