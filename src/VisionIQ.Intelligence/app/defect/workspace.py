"""S11-02: a safe, throwaway workspace pinned to the baseline commit.

Two trees of the same clone: ``base`` stays at the untouched baseline and ``candidate`` receives
the proposed change as a commit on a new branch. Nothing is ever pushed from here and the source
repository is never written to. Every path that came from a model is checked before it is used.

**No command is ever run inside these two trees.** They hold Git directories that the host uses,
and code that can write next to a Git directory can make the host's next ``git`` command run
anything (a hook, ``core.fsmonitor``). Every execution gets its own ``export`` instead: the files
of exactly one commit in a fresh directory with no Git directory in it, thrown away afterwards.
A run can therefore never see what an earlier run left behind.
"""

from __future__ import annotations

import base64
import contextlib
import hashlib
import os
import re
import shutil
import stat
import subprocess
import tarfile
import time
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Literal
from urllib.parse import urlsplit

SHA = re.compile(r"^[0-9a-f]{40}([0-9a-f]{24})?$")
# What a model may name: plain relative paths, no traversal, no drive, no shell characters.
SAFE_PATH = re.compile(r"^(?!.*(?:^|/)\.\.?(?:/|$))[A-Za-z0-9_.][A-Za-z0-9_./-]{0,399}$")
OUTPUT_DIR = ".visioniq-out"
BASELINE_NOT_PINNED = "baseline_not_pinned"
BASELINE_NOT_FOUND = "baseline_not_found"
REPOSITORY_UNAVAILABLE = "repository_unavailable"
GIT_TIMEOUT_SECONDS = 300
# Downloading a large repository for the first time takes longer than any local git command.
GIT_NETWORK_TIMEOUT_SECONDS = 1800

Tree = Literal["base", "candidate"]


class WorkspaceError(Exception):
    """The workspace could not be prepared. ``code`` is a fixed reason code, never git output."""

    def __init__(self, code: str) -> None:
        super().__init__(code)
        self.code = code


def is_safe_path(path: str) -> bool:
    return bool(SAFE_PATH.match(path)) and not path.endswith("/") and "//" not in path


def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _git_environment() -> dict[str, str]:
    """Git runs without the service's environment: no tokens, no prompts, no user config."""
    keep = ("PATH", "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "TEMP", "TMP", "PATHEXT", "COMSPEC")
    environment = {name: os.environ[name] for name in keep if name in os.environ}
    environment.update(
        GIT_TERMINAL_PROMPT="0",
        GIT_CONFIG_NOSYSTEM="1",
        GIT_CONFIG_GLOBAL=os.devnull,
        LC_ALL="C",
    )
    return environment


def clone_credentials(
    source: str, access_token: str | None, token_hosts: Sequence[str]
) -> dict[str, str]:
    """Extra git environment that authenticates ONE clone of a private HTTPS repository.

    The token is sent as an HTTP header through git's environment configuration: it is never on
    the command line (visible in process lists), never written to the clone's config and never
    used by a later command. It is sent only over HTTPS and only to an approved host, so a
    source URL in the policy file cannot redirect it elsewhere. A local path needs no token.
    """
    if not access_token:
        return {}
    parts = urlsplit(source)
    if parts.scheme != "https" or (parts.hostname or "").lower() not in {
        host.lower() for host in token_hosts
    }:
        return {}
    basic = base64.b64encode(f":{access_token}".encode()).decode("ascii")
    return {
        "GIT_CONFIG_COUNT": "1",
        "GIT_CONFIG_KEY_0": f"http.{parts.scheme}://{parts.hostname}/.extraHeader",
        "GIT_CONFIG_VALUE_0": f"Authorization: Basic {basic}",
    }


def _is_remote(source: str) -> bool:
    """True for a URL that is downloaded over the network (a local path is not cached)."""
    return urlsplit(source).scheme in {"https", "http", "ssh", "git"}


@contextlib.contextmanager
def _cache_lock(cache: Path, timeout_seconds: float):
    """One process at a time updates a repository's mirror. A lock left by a crashed run is
    taken over once it is older than the longest an update may take."""
    lock = cache.with_name(cache.name + ".lock")
    deadline = time.monotonic() + timeout_seconds
    while True:
        try:
            lock.mkdir()
            break
        except FileExistsError:
            with contextlib.suppress(OSError):
                if time.time() - lock.stat().st_mtime > timeout_seconds:
                    lock.rmdir()
                    continue
            if time.monotonic() > deadline:
                raise WorkspaceError(REPOSITORY_UNAVAILABLE) from None
            time.sleep(0.2)
    try:
        yield
    finally:
        with contextlib.suppress(OSError):
            lock.rmdir()


def update_cache(
    source: str, base_sha: str, cache_dir: Path, credentials: Mapping[str, str] | None = None
) -> Path:
    """A local bare mirror of ``source`` that contains ``base_sha``; returns its path.

    The first run downloads the repository once. A later run uses no network at all when its
    commit is already in the mirror, and otherwise fetches only what is new. Runs never work in
    the mirror: each clones its own copy from it, so one run cannot affect another. A damaged
    mirror is rebuilt; a mirror is never deleted because the network is unavailable.
    """
    cache_dir.mkdir(parents=True, exist_ok=True)
    cache = cache_dir / f"{hashlib.sha256(source.encode()).hexdigest()[:24]}.git"
    with _cache_lock(cache, GIT_NETWORK_TIMEOUT_SECONDS + GIT_TIMEOUT_SECONDS):
        if cache.exists():
            intact = run_git(cache, ["rev-parse", "--is-bare-repository"], check=False).strip()
            if intact != "true":
                shutil.rmtree(cache, onerror=_remove_readonly)
        if cache.exists():
            if run_git(cache, ["cat-file", "-t", base_sha], check=False).strip() == "commit":
                return cache
            run_git(
                cache,
                ["fetch", "--quiet", "--prune", source, "+refs/heads/*:refs/heads/*"],
                extra_environment=credentials,
                timeout_seconds=GIT_NETWORK_TIMEOUT_SECONDS,
            )
            return cache
        run_git(
            cache_dir,
            ["clone", "--quiet", "--bare", source, cache.name],
            extra_environment=credentials,
            timeout_seconds=GIT_NETWORK_TIMEOUT_SECONDS,
        )
    return cache


def _remove_readonly(function, path, _error) -> None:
    os.chmod(path, stat.S_IWRITE)  # git marks pack files read-only on Windows
    function(path)


def _remove_readonly_quietly(function, path, error) -> None:
    with contextlib.suppress(OSError):  # best effort: see Workspace.cleanup
        _remove_readonly(function, path, error)


@dataclass
class Workspace:
    root: Path
    base_sha: str
    bot_name: str
    bot_email: str

    @property
    def base(self) -> Path:
        return self.root / "base"

    @property
    def candidate(self) -> Path:
        return self.root / "candidate"

    def tree(self, tree: Tree) -> Path:
        return self.base if tree == "base" else self.candidate

    # ---- git ---------------------------------------------------------------------------

    def _git(self, tree: Tree, *arguments: str, check: bool = True) -> str:
        return run_git(self.tree(tree), arguments, check=check)

    def files(self) -> list[str]:
        """Every file tracked at the baseline commit."""
        return self._git("base", "ls-tree", "-r", "--name-only", "-z", self.base_sha).split("\0")[
            :-1
        ]

    def read(self, path: str, tree: Tree = "base", limit: int | None = None) -> str | None:
        """A text file of a tree, or None when it is missing, unsafe or not text."""
        target = self._resolve(tree, path)
        if target is None or not target.is_file():
            return None
        try:
            if limit is None:
                data = target.read_bytes()
            else:
                with target.open("rb") as handle:
                    data = handle.read(limit)
            if b"\0" in data:
                return None
            # A cut at the byte limit may split a character; that tail is dropped.
            return data.decode("utf-8", errors="strict" if limit is None else "ignore")
        except (OSError, UnicodeDecodeError):
            return None

    def size(self, path: str, tree: Tree = "base") -> int:
        target = self._resolve(tree, path)
        return target.stat().st_size if target is not None and target.is_file() else 0

    def write(self, tree: Tree, path: str, content: str) -> None:
        target = self._resolve(tree, path)
        if target is None:
            raise WorkspaceError("unsafe_path")
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8", newline="")

    def _resolve(self, tree: Tree, path: str) -> Path | None:
        """The file inside the tree, or None if the path could leave it (links included)."""
        if not is_safe_path(path):
            return None
        root = self.tree(tree).resolve()
        target = (root / path).resolve()
        return target if target.is_relative_to(root) and ".git" not in target.parts else None

    def reset_candidate(self) -> None:
        """Back to the untouched baseline: every iteration starts from the same code."""
        self._git("candidate", "checkout", "--quiet", "--detach", "--force", self.base_sha)
        self._git("candidate", "clean", "-fdxq")

    def commit_candidate(self, branch: str, message: str) -> str:
        """Commit everything in the candidate tree on ``branch``; returns the commit SHA."""
        self._git("candidate", "checkout", "--quiet", "-B", branch)
        self._git("candidate", "add", "--all")
        self._git(
            "candidate",
            "-c",
            f"user.name={self.bot_name}",
            "-c",
            f"user.email={self.bot_email}",
            "commit",
            "--quiet",
            "--no-verify",
            "-m",
            message,
        )
        return self._git("candidate", "rev-parse", "HEAD").strip()

    def diff(self, head: str, paths: Sequence[str]) -> str:
        return self._git("candidate", "diff", "--no-color", self.base_sha, head, "--", *paths)

    def tree_sha(self, commit: str) -> str:
        return self._git("candidate", "rev-parse", f"{commit}^{{tree}}").strip()

    def bundle(self, target: Path, branch: str) -> str:
        """Write the candidate commit as a git bundle; returns the file's SHA-256."""
        target.parent.mkdir(parents=True, exist_ok=True)
        self._git("candidate", "bundle", "create", str(target), f"{self.base_sha}..{branch}")
        return hashlib.sha256(target.read_bytes()).hexdigest()

    def remove(self, tree: Tree, path: str) -> None:
        target = self._resolve(tree, path)
        if target is not None and target.is_file():
            target.unlink()

    def sha256(self, path: str, tree: Tree = "base") -> str | None:
        target = self._resolve(tree, path)
        if target is None or not target.is_file():
            return None
        return hashlib.sha256(target.read_bytes()).hexdigest()

    @property
    def runs(self) -> Path:
        return self.root / "runs"

    def export(
        self, commit: str, name: str, tree: Tree, overlay: Mapping[str, str] | None = None
    ) -> Path:
        """The files of ``commit`` in a fresh directory ``runs/<name>/<tree>``, plus ``overlay``
        (path -> content).

        No Git directory and nothing left from another run. Links that point outside the tree
        and special files are refused by the extraction filter.
        """
        if not SHA.match(commit) or not re.fullmatch(r"[A-Za-z0-9_-]{1,40}", name):
            raise WorkspaceError("unsafe_export")
        self.discard(name)
        target = self.runs / name / tree
        target.mkdir(parents=True)
        archive = self.runs / f"{name}.tar"
        try:
            self._git("candidate", "archive", "--format=tar", "-o", str(archive), commit)
            with tarfile.open(archive) as tar:
                tar.extractall(target, filter="data")
        except (OSError, tarfile.TarError) as error:
            raise WorkspaceError("export_failed") from error
        finally:
            archive.unlink(missing_ok=True)
        root = target.resolve()
        for path, content in (overlay or {}).items():
            destination = (root / path).resolve() if is_safe_path(path) else None
            if (
                destination is None
                or not destination.is_relative_to(root)
                or ".git" in destination.relative_to(root).parts
            ):
                raise WorkspaceError("unsafe_path")
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_text(content, encoding="utf-8", newline="")
        return target

    def discard(self, name: str) -> None:
        """Remove an export. Best effort: a file a run left locked goes with the workspace."""
        if re.fullmatch(r"[A-Za-z0-9_-]{1,40}", name):
            shutil.rmtree(self.runs / name, onerror=_remove_readonly_quietly)

    def cleanup(self) -> None:
        """Best effort: a file that cannot be removed must not turn a finished result into a
        failed run."""
        shutil.rmtree(self.root, onerror=_remove_readonly_quietly)


def run_git(
    cwd: Path,
    arguments: Sequence[str],
    check: bool = True,
    extra_environment: Mapping[str, str] | None = None,
    timeout_seconds: float = GIT_TIMEOUT_SECONDS,
) -> str:
    try:
        completed = subprocess.run(
            [
                "git",
                "-c",
                "core.autocrlf=false",
                "-c",
                "advice.detachedHead=false",
                # Defence in depth: nothing in a tree may make git run a program.
                "-c",
                f"core.hooksPath={os.devnull}",
                "-c",
                "core.fsmonitor=false",
                *arguments,
            ],
            cwd=cwd,
            env={**_git_environment(), **(extra_environment or {})},
            capture_output=True,
            timeout=timeout_seconds,
            check=False,
        )
    except (OSError, subprocess.TimeoutExpired) as error:
        raise WorkspaceError("git_unavailable") from error
    if check and completed.returncode != 0:
        raise WorkspaceError("git_failed")
    return completed.stdout.decode("utf-8", errors="replace")


def prepare(
    source: str,
    base_sha: str,
    root: Path,
    bot_name: str = "VisionIQ Bot",
    bot_email: str = "visioniq-bot@users.noreply.invalid",
    access_token: str | None = None,
    token_hosts: Sequence[str] = ("dev.azure.com",),
    cache_dir: Path | None = None,
) -> Workspace:
    """Clone ``source`` into ``root`` with both trees at exactly ``base_sha``.

    ``access_token`` (a read-only repository token) authenticates the clone of a private HTTPS
    repository on one of ``token_hosts``; see :func:`clone_credentials`. With ``cache_dir`` a
    remote repository is downloaded once into a local mirror and only new commits are fetched
    afterwards; see :func:`update_cache`.

    The baseline must be a full commit SHA: a branch name moves, so a result measured against it
    could not be reproduced. A commit that is not in the repository is a reason code, not a guess.
    """
    if not SHA.match(base_sha):
        raise WorkspaceError(BASELINE_NOT_PINNED)
    if root.exists():
        shutil.rmtree(root, onerror=_remove_readonly)
    root.mkdir(parents=True)
    workspace = Workspace(root, base_sha, bot_name, bot_email)
    try:
        try:
            credentials = clone_credentials(source, access_token, token_hosts)
            origin = source
            if cache_dir is not None and _is_remote(source):
                origin = str(update_cache(source, base_sha, cache_dir, credentials))
                credentials = {}  # the copy is made from the local mirror: no network, no token
            run_git(
                root,
                ["clone", "--quiet", "--no-checkout", "--no-hardlinks", origin, "candidate"],
                extra_environment=credentials,
                timeout_seconds=GIT_NETWORK_TIMEOUT_SECONDS,
            )
        except WorkspaceError as error:
            raise WorkspaceError(REPOSITORY_UNAVAILABLE) from error
        if run_git(workspace.candidate, ["cat-file", "-t", base_sha], check=False).strip() != (
            "commit"
        ):
            raise WorkspaceError(BASELINE_NOT_FOUND)
        workspace._git("candidate", "checkout", "--quiet", "--detach", base_sha)
        workspace._git("candidate", "worktree", "add", "--quiet", "--detach", "../base", base_sha)
        if workspace._git("base", "rev-parse", "HEAD").strip() != base_sha:
            raise WorkspaceError(BASELINE_NOT_FOUND)
    except WorkspaceError as error:
        shutil.rmtree(root, onerror=_remove_readonly)
        if error.code == "git_failed":
            raise WorkspaceError(BASELINE_NOT_FOUND) from error
        raise
    return workspace
