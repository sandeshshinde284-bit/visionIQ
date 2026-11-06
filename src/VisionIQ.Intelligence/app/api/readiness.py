"""Restricted database readiness endpoint, separate from process liveness.

Cloud Run must enforce authenticated invocation and internal ingress.
Do not return PostgreSQL exceptions, connection strings or credentials.
"""

from typing import Annotated

from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel

from app.config import Settings, get_settings
from app.db import connect

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
        with connect(settings, connect_timeout=3) as connection:
            result = connection.execute("SELECT 1").fetchone()
        if result == (1,):
            return DatabaseReadinessResponse(status="ready")
    except Exception:
        # Database/network/credential failures are intentionally indistinguishable.
        # Do not return provider details or log secret-bearing exceptions.
        pass
    raise HTTPException(status_code=503, detail="database_unavailable")
