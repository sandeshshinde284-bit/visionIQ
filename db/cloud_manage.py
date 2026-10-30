"""Cloud SQL DEVELOPMENT migration and schema access verification.

One-shot Cloud Run Job, routed to Cloud SQL private IP through Direct VPC egress.
Never invoked by normal docker compose startup.

Usage:
  python -m db.cloud_manage all --instance PROJECT:REGION:INSTANCE
"""

from __future__ import annotations

import argparse
import ipaddress
import os
import re
import socket
import time
from typing import Literal

import psycopg
from pydantic import SecretStr, model_validator
from sqlalchemy.engine import URL

from db.manage import LocalConfig, bootstrap, migrate, verify


class CloudConfig(LocalConfig):
    """Reuse local migrations/grants with explicit, separately supplied CLOUD credentials."""

    db_target: Literal["cloud"] = "cloud"
    gcp_project_id: str = ""
    db_cloud_instance: str = ""
    db_cloud_name: str = ""
    db_cloud_user: str = ""
    db_cloud_password: SecretStr | None = None
    db_cloud_api_password: SecretStr | None = None
    db_cloud_intelligence_password: SecretStr | None = None

    @model_validator(mode="after")
    def map_cloud_credentials(self) -> CloudConfig:
        # The existing migration/grant functions expect these attribute names.
        # Never fall back to local passwords when running a cloud migration.
        self.db_local_name = self.db_cloud_name
        self.db_local_user = self.db_cloud_user
        self.db_local_password = self.db_cloud_password
        self.db_api_password = self.db_cloud_api_password
        self.db_intelligence_password = self.db_cloud_intelligence_password
        return self

    def check(self) -> None:
        if self.db_target != "cloud":
            raise ValueError("This runner requires DB_TARGET=cloud.")
        parts = self.db_cloud_instance.split(":")
        if len(parts) != 3 or not all(parts):
            raise ValueError("DB_CLOUD_INSTANCE must be project:region:instance.")
        if not self.gcp_project_id or self.gcp_project_id != parts[0]:
            raise ValueError("GCP_PROJECT_ID must equal the Cloud SQL instance project.")
        if self.db_port != 5432:
            raise ValueError("The private SQL connection must use DB_PORT=5432.")
        try:
            addr = ipaddress.ip_address(self.db_host)
        except ValueError as exc:
            raise ValueError("DB_HOST must be the private Cloud SQL IP address.") from exc
        if not isinstance(addr, ipaddress.IPv4Address) or not addr.is_private or addr.is_loopback:
            raise ValueError("Cloud SQL requires a private, non-loopback IPv4 address.")
        if self.db_cloud_name in ("", "postgres", "template0", "template1"):
            raise ValueError("DB_CLOUD_NAME must be a dedicated application database.")
        users = (self.db_cloud_user, self.db_api_user, self.db_intelligence_user)
        # The existing Cloud SQL login may be mixed-case, e.g. "visionIQ".
        # Only runtime role names follow our lowercase naming convention.
        if not re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", self.db_cloud_user):
            raise ValueError("Invalid Cloud SQL administrator username.")
        runtime_users = (self.db_api_user, self.db_intelligence_user)
        if len(set(users)) != 3 or any(
            not re.fullmatch(r"[a-z_][a-z0-9_]*", name) for name in runtime_users
        ):
            raise ValueError("Provide three distinct, valid PostgreSQL account names.")
        passwords = (
            self.db_cloud_password,
            self.db_cloud_api_password,
            self.db_cloud_intelligence_password,
        )
        if any(item is None or not item.get_secret_value() for item in passwords):
            raise ValueError(
                "DB_CLOUD_PASSWORD, DB_CLOUD_API_PASSWORD and "
                "DB_CLOUD_INTELLIGENCE_PASSWORD must be populated from secrets."
            )
        secrets = [item.get_secret_value() for item in passwords if item is not None]
        if len(set(secrets)) != 3:
            raise ValueError("Cloud administrator/API/Intelligence passwords must be different.")

    def url(self) -> URL:
        self.check()
        assert self.db_cloud_password is not None
        return URL.create(
            "postgresql+psycopg",
            username=self.db_cloud_user,
            password=self.db_cloud_password.get_secret_value(),
            host=self.db_host,
            port=self.db_port,
            database=self.db_cloud_name,
            query={"sslmode": "require"},
        )

    def connect(self, target: str = "admin") -> psycopg.Connection:
        self.check()
        accounts = {
            "admin": (self.db_cloud_user, self.db_cloud_password),
            "api": (self.db_api_user, self.db_cloud_api_password),
            "intelligence": (self.db_intelligence_user, self.db_cloud_intelligence_password),
        }
        if target not in accounts:
            raise ValueError("Unknown database role.")
        user, secret = accounts[target]
        assert secret is not None
        return psycopg.connect(
            host=self.db_host,
            port=self.db_port,
            dbname=self.db_cloud_name,
            user=user,
            password=secret.get_secret_value(),
            sslmode="require",
            autocommit=True,
            connect_timeout=10,
        )


def wait_for_private_database(
    host: str,
    port: int,
    *,
    timeout_seconds: float = 180,
    probe_timeout_seconds: float = 5,
    interval_seconds: float = 5,
) -> None:
    """Wait for Direct VPC egress to become usable before modifying Cloud SQL.

    Cloud Run Direct VPC egress can take a minute or more to become available.
    A TCP check distinguishes startup/routing failures from authentication or
    migration errors. Do not log credentials.
    """
    deadline = time.monotonic() + timeout_seconds
    attempt = 0
    while True:
        attempt += 1
        try:
            with socket.create_connection((host, port), timeout=probe_timeout_seconds):
                pass
            print(
                f"Cloud SQL TCP endpoint {host}:{port} reachable after {attempt} attempt(s).",
                flush=True,
            )
            return
        except OSError as error:
            remaining = deadline - time.monotonic()
            print(
                f"Cloud SQL TCP attempt {attempt} failed ({type(error).__name__}); "
                f"{max(0, round(remaining))}s remaining.",
                flush=True,
            )
            if remaining <= 0:
                raise RuntimeError(
                    f"Cloud SQL private TCP endpoint {host}:{port} could not be reached "
                    f"within {timeout_seconds:g}s. Verify VPC egress and firewall policy."
                ) from error
            time.sleep(min(interval_seconds, remaining))


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Explicit VisionIQ DEVELOPMENT Cloud SQL migrations"
    )
    parser.add_argument("action", choices=("migrate", "roles", "verify", "all"))
    parser.add_argument(
        "--instance",
        required=True,
        help="Exact approved PROJECT:REGION:INSTANCE identifier",
    )
    args = parser.parse_args()
    settings = CloudConfig()
    settings.check()
    if settings.db_cloud_instance != args.instance:
        parser.error("The --instance argument must exactly match DB_CLOUD_INSTANCE.")
    if os.getenv("VISIONIQ_CLOUD_RUN_MIGRATIONS") != "approved":
        parser.error("Set VISIONIQ_CLOUD_RUN_MIGRATIONS=approved explicitly.")

    wait_for_private_database(settings.db_host, settings.db_port)

    # The Alembic environment selects CloudConfig only inside this guarded command.
    if args.action in ("migrate", "all"):
        os.environ["VISIONIQ_MIGRATION_CONTEXT"] = "cloud"
        try:
            migrate(settings)
        finally:
            os.environ.pop("VISIONIQ_MIGRATION_CONTEXT", None)
    if args.action in ("roles", "all"):
        bootstrap(settings)
    if args.action in ("verify", "all"):
        verify(settings)


if __name__ == "__main__":
    main()
