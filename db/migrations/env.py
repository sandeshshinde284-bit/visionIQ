"""Alembic environment: one controlled migration chain for the three schemas."""

import os

from alembic import context
from db.manage import LocalConfig
from sqlalchemy import create_engine, pool


def select_settings():
    if os.getenv("VISIONIQ_MIGRATION_CONTEXT") == "cloud":
        from db.cloud_manage import CloudConfig

        return CloudConfig()
    return LocalConfig()


config = context.config


def upgrade_online() -> None:
    cfg = select_settings()
    cfg.check()
    engine = create_engine(cfg.url(), poolclass=pool.NullPool)
    try:
        with engine.connect() as connection:
            context.configure(
                connection=connection,
                target_metadata=None,
                version_table_schema="public",
            )
            with context.begin_transaction():
                context.run_migrations()
    finally:
        engine.dispose()


def upgrade_offline() -> None:
    cfg = select_settings()
    cfg.check()
    context.configure(
        url=cfg.url().render_as_string(hide_password=False),
        target_metadata=None,
        literal_binds=True,
        version_table_schema="public",
    )
    with context.begin_transaction():
        context.run_migrations()


if context.is_offline_mode():
    upgrade_offline()
else:
    upgrade_online()
