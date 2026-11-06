"""Connections under the Intelligence runtime identity (never the migration account)."""

import psycopg

from app.config import Settings


def connect(settings: Settings, *, connect_timeout: int = 5) -> psycopg.Connection:
    settings.require_runtime_database_identity()
    assert settings.db_intelligence_password is not None
    return psycopg.connect(
        host=settings.db_host,
        port=settings.db_port,
        dbname=settings.db_name,
        user=settings.db_intelligence_user,
        password=settings.db_intelligence_password.get_secret_value(),
        sslmode=settings.db_ssl_mode,
        connect_timeout=connect_timeout,
    )
