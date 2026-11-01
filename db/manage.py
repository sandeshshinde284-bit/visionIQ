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
        if self.db_local_password is None:  # m-7: real check, not an assert
            raise ValueError("A required database password is missing.")
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
        if secret is None:  # m-7: real check, not an assert
            raise ValueError("A required database password is missing.")
        return psycopg.connect(
            host=self.db_host,
            port=self.db_port,
            dbname=self.db_local_name,
            user=username,
            password=secret.get_secret_value(),
            autocommit=True,
            connect_timeout=10,
        )


def migrate(settings: LocalConfig) -> None:
    settings.check()
    command.upgrade(Config(str(ROOT / "db" / "alembic.ini")), "head")


def bootstrap(settings: LocalConfig) -> None:
    """Rerunnable roles/grants, including default privileges on future migration-created tables."""
    settings.check()
    with (
        settings.connect() as connection,
        connection.cursor() as cur,
    ):
        runtime = (
            (settings.db_api_user, settings.db_api_password),
            (settings.db_intelligence_user, settings.db_intelligence_password),
        )
        for name, password in runtime:
            if password is None:  # m-7: real check, not an assert
                raise ValueError("A required database password is missing.")
            cur.execute("SELECT 1 FROM pg_roles WHERE rolname=%s", (name,))
            action = (
                "CREATE ROLE {} LOGIN PASSWORD {}"
                if cur.fetchone() is None
                else (
                    "ALTER ROLE {} LOGIN NOSUPERUSER NOCREATEDB "
                    "NOCREATEROLE NOREPLICATION PASSWORD {}"
                )
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
            cur.execute(sql.SQL("REVOKE ALL ON SCHEMA public FROM {}").format(sql.Identifier(name)))
            # LRN-08: pgvector's type, functions and operators live in public; the services may
            # USE them (vector columns, <=> search) but never CREATE objects there.
            cur.execute(sql.SQL("GRANT USAGE ON SCHEMA public TO {}").format(sql.Identifier(name)))

        admin = sql.Identifier(settings.db_local_user)
        for name in SCHEMAS:
            schema = sql.Identifier(name)
            owner_name = (
                settings.db_intelligence_user if name == "knowledge" else settings.db_api_user
            )
            other_name = (
                settings.db_api_user if name == "knowledge" else settings.db_intelligence_user
            )
            owner = sql.Identifier(owner_name)
            other = sql.Identifier(other_name)
            cur.execute(sql.SQL("REVOKE ALL ON SCHEMA {} FROM PUBLIC").format(schema))
            cur.execute(sql.SQL("REVOKE ALL ON ALL TABLES IN SCHEMA {} FROM PUBLIC").format(schema))
            cur.execute(
                sql.SQL("REVOKE ALL ON ALL SEQUENCES IN SCHEMA {} FROM PUBLIC").format(schema)
            )
            for role in (owner, other):
                cur.execute(sql.SQL("REVOKE ALL ON SCHEMA {} FROM {}").format(schema, role))
                cur.execute(
                    sql.SQL("REVOKE ALL ON ALL TABLES IN SCHEMA {} FROM {}").format(schema, role)
                )
                cur.execute(
                    sql.SQL("REVOKE ALL ON ALL SEQUENCES IN SCHEMA {} FROM {}").format(schema, role)
                )

            cur.execute(sql.SQL("GRANT USAGE ON SCHEMA {} TO {}, {}").format(schema, owner, other))
            cur.execute(
                sql.SQL("GRANT SELECT ON ALL TABLES IN SCHEMA {} TO {}, {}").format(
                    schema, owner, other
                )
            )
            cur.execute(
                sql.SQL("GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {} TO {}").format(
                    schema, owner
                )
            )
            cur.execute(
                sql.SQL("GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA {} TO {}").format(
                    schema, owner
                )
            )
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
        _grant_code_scan_writes(cur, settings)
    print("Database roles and schema permissions configured.")


CODE_SCAN_ROWS = "provider = 'git' AND kind = 'code_scan'"


def _grant_code_scan_writes(cur: psycopg.Cursor, settings: LocalConfig) -> None:
    """B-15: let the Intelligence role write ONLY its own code-scan evidence.

    The evidence schema is owned by the API role. Instead of borrowing API
    credentials, the Python scanner gets INSERT/UPDATE on four tables, and
    row-level security limits those writes to git code_scan rows and the graph
    rows they prove. Every other evidence row and table stays read-only for it.
    """
    cur.execute("SELECT to_regclass('evidence.graph_assertions') IS NOT NULL")
    if not cur.fetchone()[0]:
        return  # migrations have not created the tables yet ("roles" before "migrate")
    api = sql.Identifier(settings.db_api_user)
    intel = sql.Identifier(settings.db_intelligence_user)
    scan_items = f"SELECT item_key FROM evidence.source_items WHERE {CODE_SCAN_ROWS}"
    ado_commits = (
        "SELECT item_key FROM evidence.source_items "
        "WHERE provider = 'azure_devops' AND kind = 'commit'"
    )
    grants = {
        "source_items": "INSERT, UPDATE",
        "source_revisions": "INSERT",
        "graph_nodes": "INSERT",
        "graph_assertions": "INSERT",
    }
    policies = {
        "source_items": [
            ("intel_read", "FOR SELECT", "USING (true)"),
            ("intel_insert", "FOR INSERT", f"WITH CHECK ({CODE_SCAN_ROWS})"),
            (
                "intel_update",
                "FOR UPDATE",
                f"USING ({CODE_SCAN_ROWS}) WITH CHECK ({CODE_SCAN_ROWS})",
            ),
        ],
        "source_revisions": [
            ("intel_read", "FOR SELECT", "USING (true)"),
            ("intel_insert", "FOR INSERT", f"WITH CHECK (item_key IN ({scan_items}))"),
        ],
        "graph_nodes": [
            ("intel_read", "FOR SELECT", "USING (true)"),
            (
                "intel_insert",
                "FOR INSERT",
                f"WITH CHECK (anchor_item_key IN ({scan_items}) OR "
                f"(node_kind = 'commit' AND anchor_item_key IN ({ado_commits})))",
            ),
        ],
        "graph_assertions": [
            ("intel_read", "FOR SELECT", "USING (true)"),
            ("intel_insert", "FOR INSERT", f"WITH CHECK (provenance_item_key IN ({scan_items}))"),
        ],
    }
    for table, privileges in grants.items():
        qualified = sql.SQL("evidence.{}").format(sql.Identifier(table))
        cur.execute(sql.SQL("GRANT {} ON {} TO {}").format(sql.SQL(privileges), qualified, intel))
        cur.execute(sql.SQL("ALTER TABLE {} ENABLE ROW LEVEL SECURITY").format(qualified))
        # The API role owns this schema's data: keep its full access explicit under RLS.
        cur.execute(sql.SQL("DROP POLICY IF EXISTS api_all ON {}").format(qualified))
        cur.execute(
            sql.SQL("CREATE POLICY api_all ON {} TO {} USING (true) WITH CHECK (true)").format(
                qualified, api
            )
        )
        for name, operation, rule in policies[table]:
            cur.execute(
                sql.SQL("DROP POLICY IF EXISTS {} ON {}").format(sql.Identifier(name), qualified)
            )
            cur.execute(
                # operation and rule are fixed constants above, never input (second-pass review).
                sql.SQL("CREATE POLICY {} ON {} {} TO {} {}").format(
                    sql.Identifier(name), qualified, sql.SQL(operation), intel, sql.SQL(rule)
                )
            )


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
                            sql.SQL("SELECT count(*) FROM {}").format(sql.Identifier(schema, name))
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
                    sql.SQL("DROP TABLE IF EXISTS {}").format(sql.Identifier(schema, name))
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
