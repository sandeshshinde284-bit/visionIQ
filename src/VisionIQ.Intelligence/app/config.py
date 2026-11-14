"""Application settings.

Values come from environment variables. For local runs on the host, a repository-root ``.env``
file is also read if it exists; real environment variables always win. Inside Docker no ``.env``
is copied into the image, so the container environment (set in docker-compose.yml) is the only
source.
"""

import re
from functools import lru_cache
from pathlib import Path
from typing import Literal

from pydantic import Field, SecretStr, field_validator, model_validator
from pydantic_settings import BaseSettings, SettingsConfigDict

LogLevel = Literal["DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"]
SslMode = Literal["disable", "allow", "prefer", "require", "verify-ca", "verify-full"]
DataClass = Literal["PUBLIC", "INTERNAL", "RESTRICTED"]


def find_env_file(start: Path | None = None) -> Path | None:
    """Walk up from this file to the repository root and return the first ``.env`` found."""
    for directory in (start or Path(__file__).resolve()).parents:
        candidate = directory / ".env"
        if candidate.is_file():
            return candidate
        if (directory / ".git").exists():
            return None
    return None


class Settings(BaseSettings):
    """Typed, validated configuration for the Intelligence service."""

    model_config = SettingsConfigDict(
        env_file=find_env_file(),
        env_file_encoding="utf-8",
        extra="ignore",  # the shared .env contains settings for other services
        env_ignore_empty=True,  # a blank value in .env means "use the default"
        case_sensitive=False,
    )

    service_name: str = "VisionIQ.Intelligence"
    app_env: Literal["development", "test", "production"] = "development"
    app_log_level: LogLevel = "INFO"
    python_api_host: str = "127.0.0.1"
    python_api_port: int = Field(default=8000, ge=1, le=65535)
    request_timeout_seconds: int = Field(default=30, ge=1, le=600)

    db_host: str = "localhost"
    db_port: int = Field(default=5432, ge=1, le=65535)
    db_name: str = "visioniq"
    db_user: str | None = None
    db_password: SecretStr | None = None
    # M-14: same default everywhere; encrypt when the server supports it (.env.example sets
    # "disable" for the local Docker database, the Cloud Run deploy sets "require").
    db_ssl_mode: SslMode = "prefer"
    db_intelligence_user: str | None = None
    db_intelligence_password: SecretStr | None = None

    # FND-05 eventing. Publishing needs a project; PUBSUB_EMULATOR_HOST (read by the Google client
    # itself) points it at the local emulator instead of Google Cloud.
    tenant_id: str = Field(default="visioniq-dev", pattern=r"^[A-Za-z0-9_.:-]{1,128}$")
    pubsub_project_id: str | None = None
    events_dispatch_enabled: bool = False
    events_dispatch_interval_seconds: float = Field(default=2.0, gt=0, le=60)
    event_contracts_dir: Path | None = None

    # BRF-03 brief pipeline. Off by default: requests are then answered BLOCKED, as before.
    brief_pipeline_enabled: bool = False
    brief_worker_interval_seconds: float = Field(default=1.0, gt=0, le=60)
    brief_evidence_limit: int = Field(default=8, ge=1, le=20)
    brief_risk_window_days: int = Field(default=90, ge=1, le=3650)

    # BRF-02 model use. LLM_MODE=offline replaces every model with a deterministic, extractive
    # stand-in (tests and local demos only; refused in production).
    llm_mode: Literal["live", "offline"] = "live"
    gcp_project_id: str | None = None
    vertex_location: str = "europe-west3"
    gemini_model: str = "gemini-2.5-flash"
    # Decision D04: where Claude runs and what data it may receive. Off until decided.
    claude_route: Literal["off", "vertex", "anthropic"] = "off"
    claude_model: str = "claude-opus-5-5"
    claude_effort: Literal["low", "medium", "high", "xhigh", "max"] = "medium"
    claude_vertex_region: str = "global"
    claude_refusal_fallback: bool = True
    # The classification of ticket and evidence text, and the highest each provider may see.
    llm_data_classification: DataClass = "INTERNAL"
    llm_gemini_max_classification: DataClass = "RESTRICTED"
    llm_claude_max_classification: DataClass | None = None
    # Per-run budget (v1.11 10.9 rule 3): calls, tokens (input + output) and wall-clock seconds.
    llm_max_calls_per_run: int = Field(default=3, ge=1, le=10)
    llm_max_tokens_per_run: int = Field(default=60_000, ge=1_000, le=2_000_000)
    llm_max_seconds_per_run: float = Field(default=90.0, gt=0, le=600)
    llm_call_timeout_seconds: float = Field(default=45.0, gt=0, le=300)
    # Thinking tokens count against this cap on both providers, so it is well above the
    # size of a brief.
    llm_max_output_tokens: int = Field(default=16_000, ge=256, le=64_000)

    # BRF-08 hotspot sweep. 0 = no in-process schedule: run `python -m app.hotspots.cli` from
    # Cloud Scheduler (or by hand) instead.
    hotspot_sweep_interval_hours: float = Field(default=0.0, ge=0, le=720)
    hotspot_window_days: int = Field(default=180, ge=1, le=3650)
    hotspot_area_depth: int = Field(default=2, ge=1, le=10)

    # DEF-01 to DEF-06 fix pipeline (deep analysis). Off by default: a deep request is then
    # answered BLOCKED, as before. DEFECT_POLICY_FILE names the repositories, runner and budgets.
    defect_pipeline_enabled: bool = False
    defect_policy_file: Path | None = None
    defect_worker_interval_seconds: float = Field(default=2.0, gt=0, le=60)
    # The local runner executes model-written code on this host with network access. It is for a
    # developer's machine only and must be allowed explicitly; it is never allowed in production.
    defect_allow_local_runner: bool = False
    # Throwaway workspaces and the run evidence (manifests, logs, bundle). Default: the system
    # temporary directory.
    defect_work_dir: Path | None = None
    defect_artifact_dir: Path | None = None
    defect_artifact_base_uri: str = ""
    defect_keep_workspaces: bool = False
    defect_bot_name: str = "VisionIQ Bot"
    defect_bot_email: str = "visioniq-bot@users.noreply.invalid"
    # Read-only (Code: Read) token used ONLY to clone a private repository for a fix run, and the
    # hosts it may be sent to. Without it only a local path or a public repository can be cloned.
    defect_repo_read_pat: SecretStr | None = None
    defect_repo_token_hosts: str = "dev.azure.com"
    # A local mirror of each cloned repository: the first fix run downloads it, later runs only
    # fetch new commits. Every run still works on its own fresh copy. Unset: clone every time.
    defect_repo_cache_dir: Path | None = None
    # How much of the repository a fix run downloads. "full" (default) clones it with its
    # history. "commit" downloads only the commit the run is pinned to, which is all a run uses;
    # if the server does not hand out a single commit, the repository is cloned as with "full".
    defect_repo_fetch: Literal["full", "commit"] = "full"
    # Model selection by complexity for the two code tasks of a fix run (the reproduction test and
    # the fix). "fixed" (default) follows the routing table, as before. "by_complexity" rates each
    # run simple / standard / complex from its brief and sends the code tasks to that tier's model;
    # a failed attempt moves one tier up. Each tier is "<provider>" or "<provider>:<model>", with
    # provider gemini or claude (e.g. "claude:claude-haiku-5-5"). An empty tier follows the table.
    defect_model_selection: Literal["fixed", "by_complexity"] = "fixed"
    defect_model_simple: str = ""
    defect_model_standard: str = ""
    defect_model_complex: str = ""
    # Simple: at most this many files in scope (and a LOW risk band, no restricted area).
    defect_simple_max_files: int = Field(default=2, ge=1, le=50)
    # Complex: at least this many files in scope (or a HIGH risk band, or a restricted area).
    defect_complex_min_files: int = Field(default=6, ge=2, le=200)
    # Per-run budget of a fix. The seconds cover the test runs as well as the model calls.
    defect_max_calls_per_run: int = Field(default=6, ge=1, le=30)
    defect_max_tokens_per_run: int = Field(default=400_000, ge=1_000, le=5_000_000)
    defect_max_seconds_per_run: float = Field(default=1800.0, gt=0, le=14_400)
    # LLM_MODE=offline only: prepared test and patch answers for a demo (see app/defect/cli.py).
    defect_offline_script: Path | None = None

    @model_validator(mode="after")
    def select_runtime_role(self) -> "Settings":
        # Ignore generic DB_* credentials; these may belong to the migration account.
        self.db_user = self.db_intelligence_user
        self.db_password = self.db_intelligence_password
        if self.llm_mode == "offline" and self.app_env == "production":
            raise ValueError("LLM_MODE=offline is for tests and local demos, not production.")
        return self

    def require_runtime_database_identity(self) -> None:
        if (
            not self.db_intelligence_user
            or self.db_intelligence_password is None
            or not self.db_intelligence_password.get_secret_value()
        ):
            raise ValueError(
                "DB_INTELLIGENCE_USER and DB_INTELLIGENCE_PASSWORD are required "
                "for the Intelligence runtime."
            )

    @field_validator(
        "app_env",
        "db_ssl_mode",
        "llm_mode",
        "claude_route",
        "claude_effort",
        "defect_model_selection",
        "defect_repo_fetch",
        mode="before",
    )
    @classmethod
    def _lowercase(cls, value: object) -> object:
        return value.lower() if isinstance(value, str) else value

    @field_validator("defect_model_simple", "defect_model_standard", "defect_model_complex")
    @classmethod
    def _model_tier(cls, value: str) -> str:
        value = value.strip()
        if value and not re.fullmatch(
            r"(gemini|claude)(:[A-Za-z0-9][A-Za-z0-9._@-]{0,99})?", value
        ):
            raise ValueError(
                'A model tier is "gemini", "claude" or "<provider>:<model>", '
                'for example "claude:claude-haiku-5-5".'
            )
        return value

    @field_validator(
        "app_log_level",
        "llm_data_classification",
        "llm_gemini_max_classification",
        "llm_claude_max_classification",
        mode="before",
    )
    @classmethod
    def _uppercase(cls, value: object) -> object:
        return value.upper() if isinstance(value, str) else value


@lru_cache
def get_settings() -> Settings:
    """Return the process-wide settings (cached)."""
    return Settings()
