"""Migrate, configure, and verify the LOCAL VisionIQ PostgreSQL database.

Run from the repository root: python -m db.manage all
Cloud SQL setup is intentionally NOT implemented by this tool.
"""
from __future__ import annotations

import argparse
import re
import uuid
from pathlib import Path
from typing import Literal

import psycopg
from alembic import command
from alembic.config import Config
from psycopg import sql
from pydantic import SecretStr
from pydantic_settings import BaseSettings, SettingsConfigDict
from sqlalchemy.engine import URL

ROOT = Path(__file__).resolve().parents[1]
SCHEMAS = ("workflow", "evidence", "knowledge")


class LocalConfig(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=ROOT / ".env",
        env_file_encoding="utf-8",
        extra="ignore",
        env_ignore_empty=True,
        case_sensitive=False,
    )
    db_target: Literal["local", "cloud"] = "local"
    db_host: str = "localhost"
    db_port: int = 5432
    db_local_name: str = "visioniq"
    db_local_user: str = "visioniq"
    db_local_password: SecretStr | None = None
    db_api_user: str = "visioniq_api"
    db_api_password: SecretStr | None = None
    db_intelligence_user: str = "visioniq_intelligence"
    db_intelligence_password: SecretStr | None = None

    def check(self) -> None:
        if self.db_target != "local":
            raise ValueError("Refusing to modify a cloud database. Select local first.")
        users = (self.db_local_user, self.db_api_user, self.db_intelligence_user)
        if len(set(users)) != 3 or any(
            not re.fullmatch(r"[a-z_][a-z0-9_]*", item) for item in users
        ):
            raise ValueError("Three distinct lowercase PostgreSQL role names are required.")
        secrets = (self.db_local_password, self.db_api_password, self.db_intelligence_password)
        if any(secret is None or not secret.get_secret_value() for secret in secrets):
            raise ValueError(
                "Set DB_LOCAL_PASSWORD, DB_API_PASSWORD and DB_INTELLIGENCE_PASSWORD in .env."
            )
        values = [secret.get_secret_value() for secret in secrets if secret is not None]
        if len(set(values)) != 3:
            raise ValueError("Use three different database passwords.")

    def url(self) -> URL:
        self.check()
        assert self.db_local_password is not None
        return URL.create(
            "postgresql+psycopg",
            username=self.db_local_user,
            password=self.db_local_password.get_secret_value(),
            host=self.db_host,
            port=self.db_port,
            database=self.db_local_name,
        )

    def connect(self, target: str = "admin") -> psycopg.Connection:
        self.check()
        identities = {
            "admin": (self.db_local_user, self.db_local_password),
            "api": (self.db_api_user, self.db_api_password),
            "intelligence": (self.db_intelligence_user, self.db_intelligence_password),
        }
        if target not in identities:
            raise ValueError("Unknown database identity")
        username, secret = identities[target]
        assert secret is not None
        return psycopg.connect(
            host=self.db_host, port=self.db_port, dbname=self.db_local_name,
            user=username, password=secret.get_secret_value(),
            autocommit=True, connect_timeout=10,
        )


def migrate(settings: LocalConfig) -> None:
    settings.check()
    command.upgrade(Config(str(ROOT / "db" / "alembic.ini")), "head")


def bootstrap(settings: LocalConfig) -> None:
    """Rerunnable roles/grants, including default privileges on future migration-created tables."""
    settings.check()
    with settings.connect() as connection:
        with connection.cursor() as cur:
            runtime = (
                (settings.db_api_user, settings.db_api_password),
                (settings.db_intelligence_user, settings.db_intelligence_password),
            )
            for name, password in runtime:
                assert password is not None
                cur.execute("SELECT 1 FROM pg_roles WHERE rolname=%s", (name,))
                action = "CREATE ROLE {} LOGIN PASSWORD {}" if cur.fetchone() is None else (
                    "ALTER ROLE {} LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD {}"
                )
                cur.execute(
                    sql.SQL(action).format(
                        sql.Identifier(name), sql.Literal(password.get_secret_value())
                    )
                )

            cur.execute(
                sql.SQL("REVOKE ALL ON DATABASE {} FROM PUBLIC").format(
                    sql.Identifier(settings.db_local_name)
                )
            )
            cur.execute(
                sql.SQL("GRANT CONNECT ON DATABASE {} TO {}, {}").format(
                    sql.Identifier(settings.db_local_name),
                    sql.Identifier(settings.db_api_user),
                    sql.Identifier(settings.db_intelligence_user),
                )
            )
            cur.execute("REVOKE ALL ON SCHEMA public FROM PUBLIC")
            for name, _ in runtime:
                cur.execute(
                    sql.SQL("REVOKE ALL ON SCHEMA public FROM {}").format(sql.Identifier(name))
                )

            admin = sql.Identifier(settings.db_local_user)
            for name in SCHEMAS:
                schema = sql.Identifier(name)
                owner_name = settings.db_intelligence_user if name == "knowledge" else settings.db_api_user
                other_name = settings.db_api_user if name == "knowledge" else settings.db_intelligence_user
                owner = sql.Identifier(owner_name)
                other = sql.Identifier(other_name)
                cur.execute(sql.SQL("REVOKE ALL ON SCHEMA {} FROM PUBLIC").format(schema))
                cur.execute(sql.SQL("REVOKE ALL ON ALL TABLES IN SCHEMA {} FROM PUBLIC").format(schema))
                cur.execute(sql.SQL("REVOKE ALL ON ALL SEQUENCES IN SCHEMA {} FROM PUBLIC").format(schema))
                for role in (owner, other):
                    cur.execute(sql.SQL("REVOKE ALL ON SCHEMA {} FROM {}").format(schema, role))
                    cur.execute(sql.SQL("REVOKE ALL ON ALL TABLES IN SCHEMA {} FROM {}").format(schema, role))
                    cur.execute(sql.SQL("REVOKE ALL ON ALL SEQUENCES IN SCHEMA {} FROM {}").format(schema, role))

                cur.execute(sql.SQL("GRANT USAGE ON SCHEMA {} TO {}, {}").format(schema, owner, other))
                cur.execute(sql.SQL("GRANT SELECT ON ALL TABLES IN SCHEMA {} TO {}, {}").format(schema, owner, other))
                cur.execute(sql.SQL("GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {} TO {}").format(schema, owner))
                cur.execute(sql.SQL("GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA {} TO {}").format(schema, owner))
                # Defaults apply only to objects later created by the named migration account.
                cur.execute(
                    sql.SQL(
                        "ALTER DEFAULT PRIVILEGES FOR ROLE {} IN SCHEMA {} "
                        "REVOKE INSERT, UPDATE, DELETE ON TABLES FROM {}"
                    ).format(admin, schema, other)
                )
                cur.execute(
                    sql.SQL(
                        "ALTER DEFAULT PRIVILEGES FOR ROLE {} IN SCHEMA {} "
                        "GRANT SELECT ON TABLES TO {}, {}"
                    ).format(admin, schema, owner, other)
                )
                cur.execute(
                    sql.SQL(
                        "ALTER DEFAULT PRIVILEGES FOR ROLE {} IN SCHEMA {} "
                        "GRANT INSERT, UPDATE, DELETE ON TABLES TO {}"
                    ).format(admin, schema, owner)
                )
                cur.execute(
                    sql.SQL(
                        "ALTER DEFAULT PRIVILEGES FOR ROLE {} IN SCHEMA {} "
                        "GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {}"
                    ).format(admin, schema, owner)
                )
    print("Database roles and schema permissions configured.")


def verify(settings: LocalConfig) -> None:
    """Check PostgreSQL features, actual authorized writes, cross-schema denial and cleanup."""
    settings.check()
    name = "fnd03_probe_" + uuid.uuid4().hex[:12]
    with settings.connect() as admin:
        extension = admin.execute(
            "SELECT extname FROM pg_extension WHERE extname='vector'"
        ).fetchone()
        if extension is None:
            raise AssertionError("pgvector extension is missing")
        fulltext = admin.execute(
            "SELECT to_tsvector('english', 'historical defect regression') "
            "@@ plainto_tsquery('english', 'defect')"
        ).fetchone()
        if fulltext != (True,):
            raise AssertionError("PostgreSQL full-text search failed")

        created = []
        try:
            for schema in SCHEMAS:
                admin.execute(
                    sql.SQL("CREATE TABLE {} (note text NOT NULL)").format(
                        sql.Identifier(schema, name)
                    )
                )
                created.append(schema)
            for role, allowed, forbidden in (
                ("api", ("workflow", "evidence"), ("knowledge",)),
                ("intelligence", ("knowledge",), ("workflow", "evidence")),
            ):
                with settings.connect(role) as user:
                    for schema in allowed:
                        user.execute(
                            sql.SQL("INSERT INTO {} (note) VALUES (%s)").format(
                                sql.Identifier(schema, name)
                            ),
                            (role,),
                        )
                    for schema in SCHEMAS:
                        user.execute(
                            sql.SQL("SELECT count(*) FROM {}").format(
                                sql.Identifier(schema, name)
                            )
                        ).fetchone()
                    for schema in forbidden:
                        try:
                            user.execute(
                                sql.SQL("INSERT INTO {} (note) VALUES ('denied')").format(
                                    sql.Identifier(schema, name)
                                )
                            )
                        except psycopg.errors.InsufficientPrivilege:
                            pass
                        else:
                            raise AssertionError(
                                f"{role} was able to write to non-owned schema {schema}"
                            )
                    try:
                        user.execute(
                            sql.SQL("CREATE TABLE {} (id integer)").format(
                                sql.Identifier(allowed[0], "fnd03_forbidden_create")
                            )
                        )
                    except psycopg.errors.InsufficientPrivilege:
                        pass
                    else:
                        raise AssertionError(f"{role} could create schema objects")
        finally:
            for schema in created:
                admin.execute(
                    sql.SQL("DROP TABLE IF EXISTS {}").format(
                        sql.Identifier(schema, name)
                    )
                )
    print("pgvector, full-text search and role isolation verified.")


def main() -> None:
    parser = argparse.ArgumentParser(description="VisionIQ local database migration/ACL tool")
    parser.add_argument("action", choices=("migrate", "roles", "verify", "all"))
    action = parser.parse_args().action
    settings = LocalConfig()
    settings.check()
    if action in ("migrate", "all"):
        migrate(settings)
    if action in ("roles", "all"):
        bootstrap(settings)
    if action in ("verify", "all"):
        verify(settings)


if __name__ == "__main__":
    main()
