"""The authorization policy of the fix pipeline (master design 8.6, 8.6.4): a JSON file.

It names the repositories VisionIQ may work on and, for each one, the runner, the allowlisted
build and test commands, where a test may be added, which paths may be changed, which areas stop
for an engineer, and the size and iteration budgets. A model never chooses a command or a path
outside this file. An example is in ``config/defect-policy.example.json``.
"""

from __future__ import annotations

import json
import re
from collections.abc import Iterable, Sequence
from functools import lru_cache
from pathlib import Path
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

from app.knowledge.redaction import redact

RestrictedAreaName = Literal["security", "payment", "identity", "schema", "hardware"]

# A setting whose name looks like a credential is never passed to the runner.
_SECRET_NAME = re.compile(r"(?i)(password|passwd|secret|token|credential|api_?key|_pat$|_key$)")
_PLACEHOLDER = re.compile(r"\{([a-z_]+)\}")
RECIPE_PLACEHOLDERS = {
    "test_file": {"test_path", "test_name", "junit"},
    "suite": {"junit"},
    "benchmark": {"output"},
}


class _Model(BaseModel):
    model_config = ConfigDict(extra="forbid", frozen=True)


class RunnerPolicy(_Model):
    """Where commands run. ``local`` has no network isolation: development and tests only."""

    kind: Literal["local", "docker"] = "docker"
    # docker: an image pinned by digest, e.g. "python@sha256:...". A tag is refused.
    image: str | None = None
    # Names copied from the service environment (local runner) and fixed non-secret values.
    env_allow: tuple[str, ...] = ()
    env: dict[str, str] = Field(default_factory=dict)
    memory: str = "2g"
    cpus: float = Field(default=2.0, gt=0, le=64)
    pids_limit: int = Field(default=512, ge=16, le=65536)
    test_timeout_seconds: float = Field(default=300.0, gt=0, le=7200)
    suite_timeout_seconds: float = Field(default=1800.0, gt=0, le=14400)
    benchmark_timeout_seconds: float = Field(default=600.0, gt=0, le=7200)

    @model_validator(mode="after")
    def _checked(self) -> RunnerPolicy:
        if self.kind == "docker" and (
            not self.image or not re.search(r"@sha256:[0-9a-f]{64}$", self.image)
        ):
            raise ValueError("a docker runner needs an image pinned by digest (name@sha256:...)")
        for name in (*self.env_allow, *self.env):
            if _SECRET_NAME.search(name):
                raise ValueError(f"runner environment name looks like a credential: {name}")
        for name, value in self.env.items():
            if redact(value).text != value:
                raise ValueError(f"runner environment value for {name} looks like a credential")
        return self


class RestrictedArea(_Model):
    area: RestrictedAreaName
    patterns: tuple[str, ...] = Field(min_length=1)


class Limits(_Model):
    """8.6.4: budgets are per repository, set with its owners; there is no universal number."""

    max_test_attempts: int = Field(default=2, ge=1, le=5)
    max_fix_iterations: int = Field(default=3, ge=1, le=10)
    max_changed_files: int = Field(default=8, ge=1, le=200)
    max_changed_lines: int = Field(default=400, ge=1, le=20000)
    max_context_files: int = Field(default=6, ge=1, le=30)
    max_file_bytes: int = Field(default=60_000, ge=1_000, le=1_000_000)
    max_regression_tests: int = Field(default=10, ge=0, le=100)
    # 8.6.3 flakiness policy: reruns of a test that failed where a pass was expected, how often
    # a red is confirmed, and retries of a run the environment broke.
    flaky_reruns: int = Field(default=2, ge=0, le=5)
    red_confirmations: int = Field(default=1, ge=0, le=5)
    environment_retries: int = Field(default=1, ge=0, le=3)


class BenchmarkPolicy(_Model):
    """8.7: which changes have a workload to measure, and the owner-reviewed thresholds."""

    applies_to: tuple[str, ...] = Field(min_length=1)
    pairs: int = Field(default=5, ge=3, le=50)
    warmup_pairs: int = Field(default=1, ge=0, le=10)
    max_regression_percent: float = Field(default=10.0, gt=0, le=1000)
    max_noise_percent: float = Field(default=15.0, gt=0, le=1000)


class RepositoryPolicy(_Model):
    repository_id: str = Field(pattern=r"^[A-Za-z0-9_.:/-]{1,256}$")
    # Where the workspace is cloned from: a local path or a URL without credentials.
    source: str = Field(min_length=1)
    target_branch: str = "main"
    runner: RunnerPolicy = Field(default_factory=RunnerPolicy)
    # Allowlisted commands (argv, never a shell line). test_file runs one test file, named by
    # {test_path} or by {test_name} (the file name without its extension, for frameworks that
    # filter by class), and writes a JUnit XML or TRX report to {junit}; suite runs the touched
    # project's tests; benchmark writes {"metric", "unit", "value"} (lower is better) to {output}.
    recipes: dict[str, tuple[str, ...]]
    # Commands whose first output line is recorded as a tool version, e.g. {"python": [...]}.
    toolchain: dict[str, tuple[str, ...]] = Field(default_factory=dict)
    dependency_restore: str = "none"
    new_test_paths: tuple[str, ...] = Field(min_length=1)
    test_file_patterns: tuple[str, ...] = Field(min_length=1)
    writable_paths: tuple[str, ...] = Field(min_length=1)
    secret_paths: tuple[str, ...] = (
        ".env",
        ".env.*",
        "**/.env",
        "**/.env.*",
        "**/*.pem",
        "**/*.pfx",
        "**/*.key",
        "**/secrets/**",
        "**/*secret*",
    )
    dependency_files: tuple[str, ...] = (
        "**/requirements*.txt",
        "**/requirements*.in",
        "**/requirements/**",
        "**/constraints*.txt",
        "**/pyproject.toml",
        "**/setup.py",
        "**/setup.cfg",
        "**/poetry.lock",
        "**/Pipfile",
        "**/Pipfile.lock",
        "**/uv.lock",
        "**/package.json",
        "**/package-lock.json",
        "**/yarn.lock",
        "**/pnpm-lock.yaml",
        "**/*.csproj",
        "**/*.fsproj",
        "**/*.vbproj",
        "**/Directory.Packages.props",
        "**/Directory.Build.props",
        "**/Directory.Build.targets",
        "**/packages.lock.json",
        "**/packages.config",
        "**/nuget.config",
        "**/NuGet.Config",
        "**/global.json",
        "**/go.mod",
        "**/go.sum",
        "**/Cargo.toml",
        "**/Cargo.lock",
        "**/Gemfile",
        "**/Gemfile.lock",
        "**/pom.xml",
        "**/build.gradle*",
    )
    generated_paths: tuple[str, ...] = (
        "**/bin/**",
        "**/obj/**",
        "**/dist/**",
        "**/build/**",
        "**/node_modules/**",
        "**/__pycache__/**",
        "**/*.min.js",
        "**/*.dll",
        "**/*.exe",
        "**/*.pyc",
    )
    restricted_areas: tuple[RestrictedArea, ...] = ()
    lock_files: tuple[str, ...] = ()
    fixture_paths: tuple[str, ...] = ()
    limits: Limits = Field(default_factory=Limits)
    benchmark: BenchmarkPolicy | None = None
    # 8.6.4: a draft PR with accurately reported failed or incomplete checks, when owners allow.
    draft_pr_on_incomplete: bool = False

    @field_validator("recipes")
    @classmethod
    def _recipes(cls, recipes: dict[str, tuple[str, ...]]) -> dict[str, tuple[str, ...]]:
        if "test_file" not in recipes:
            raise ValueError("recipes must define test_file")
        for name, argv in recipes.items():
            allowed = RECIPE_PLACEHOLDERS.get(name)
            if allowed is None:
                raise ValueError(f"unknown recipe: {name}")
            if not argv:
                raise ValueError(f"recipe {name} is empty")
            used = {match for part in argv for match in _PLACEHOLDER.findall(part)}
            if used - allowed:
                raise ValueError(
                    f"recipe {name} uses unknown placeholders: {sorted(used - allowed)}"
                )
            if name == "test_file":
                complete = "junit" in used and used & {"test_path", "test_name"}
            else:
                complete = used == allowed
            if not complete:
                raise ValueError(f"recipe {name} must use {sorted(allowed)}")
        return recipes

    @model_validator(mode="after")
    def _benchmark_has_a_recipe(self) -> RepositoryPolicy:
        if self.benchmark is not None and "benchmark" not in self.recipes:
            raise ValueError("a benchmark policy needs a benchmark recipe")
        return self


class DefectPolicy(_Model):
    revision: str = Field(pattern=r"^[A-Za-z0-9_.:-]{1,80}$")
    repositories: tuple[RepositoryPolicy, ...] = ()

    def repository(self, repository_id: str) -> RepositoryPolicy | None:
        return next((r for r in self.repositories if r.repository_id == repository_id), None)


def load_policy(path: Path) -> DefectPolicy:
    """Read a policy file. Whole-line ``//`` comments are allowed, as in the other config files."""
    lines = path.read_text(encoding="utf-8").splitlines()
    text = "\n".join(line for line in lines if not line.lstrip().startswith("//"))
    return DefectPolicy.model_validate(json.loads(text))


@lru_cache(maxsize=512)
def _pattern(glob: str) -> re.Pattern[str]:
    """``**`` crosses directories, ``*`` and ``?`` stay inside one path segment."""
    parts: list[str] = []
    index = 0
    while index < len(glob):
        if glob.startswith("**/", index):
            parts.append("(?:.*/)?")
            index += 3
        elif glob.startswith("**", index):
            parts.append(".*")
            index += 2
        elif glob[index] == "*":
            parts.append("[^/]*")
            index += 1
        elif glob[index] == "?":
            parts.append("[^/]")
            index += 1
        else:
            parts.append(re.escape(glob[index]))
            index += 1
    return re.compile("".join(parts) + r"\Z", re.IGNORECASE)


def matches(path: str, patterns: Iterable[str]) -> bool:
    """Does the repository-relative ``path`` (forward slashes) match any glob?"""
    return any(_pattern(glob).match(path) for glob in patterns)


def render(argv: Sequence[str], values: dict[str, str]) -> list[str]:
    """Fill a recipe's placeholders. Values are arguments, never shell text."""
    return [_PLACEHOLDER.sub(lambda m: values.get(m.group(1), m.group(0)), part) for part in argv]
