"""The sandbox that runs allowlisted commands on a workspace tree (master design 8.6).

A runner gets an argv from the policy's recipes (never a shell line, never a command chosen by a
model), a tree to run it in and a time limit. It never sees the service's environment: no
database password, no token. The combined output is scrubbed of secret-looking text and stored
as the run's log, with its hash.

* ``DockerRunner`` is the isolated one: image pinned by digest, no network, no capabilities,
  a read-only root file system, memory, CPU and process limits, only the tree mounted. The tree
  it gets is an export of one commit and never a Git checkout (see ``Workspace.export``).
* ``LocalRunner`` runs the command as a child process. It cannot cut the network, so it says
  ``network: unrestricted`` in the manifest and must be allowed explicitly.

A command that runs past its time limit is stopped together with everything it started (the
container, or the process tree). Output is read as it comes and only its head and tail are kept,
so a command that prints without end cannot exhaust the service's memory.
"""

from __future__ import annotations

import contextlib
import hashlib
import os
import platform
import signal
import subprocess
import threading
import time
import uuid
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from datetime import UTC, datetime
from pathlib import Path
from typing import Protocol

from app.defect.policy import RunnerPolicy
from app.defect.schema import RunnerInfo
from app.knowledge.redaction import redact

MAX_LOG_BYTES = 512 * 1024
# How long a stopped command gets to release its output before the log is closed without it.
_DRAIN_SECONDS = 10.0
LAUNCH_FAILED = "runner_launch_failed"
# What a child process needs to start at all; nothing that carries a credential.
_BASE_ENVIRONMENT = (
    "PATH",
    "SYSTEMROOT",
    "SYSTEMDRIVE",
    "WINDIR",
    "TEMP",
    "TMP",
    "PATHEXT",
    "COMSPEC",
    "LANG",
    "TZ",
)


@dataclass(frozen=True)
class RunResult:
    started_at: datetime
    duration_ms: int
    # None when the command did not finish (timeout) or could not be started.
    exit_code: int | None
    timed_out: bool
    log_sha256: str
    # A fixed code when the runner itself failed; the command's own failures are exit codes.
    error: str | None = None


class Runner(Protocol):
    def describe(self) -> RunnerInfo: ...

    def environment_names(self) -> list[str]: ...

    def run(
        self, argv: Sequence[str], tree: Path, timeout_seconds: float, log: Path
    ) -> RunResult: ...


def store_log(log: Path, output: bytes) -> str:
    """Write the scrubbed, bounded log and return its SHA-256."""
    text = output.decode("utf-8", errors="replace")
    if len(text) > MAX_LOG_BYTES:
        half = MAX_LOG_BYTES // 2
        text = f"{text[:half]}\n[... log truncated ...]\n{text[-half:]}"
    data = redact(text).text.encode("utf-8")
    log.parent.mkdir(parents=True, exist_ok=True)
    log.write_bytes(data)
    return hashlib.sha256(data).hexdigest()


class BoundedOutput:
    """The head and the tail of a stream; what lies between is dropped as it arrives."""

    def __init__(self, limit: int = MAX_LOG_BYTES) -> None:
        self._half = limit // 2
        self._head = bytearray()
        self._tail = bytearray()
        self._dropped = False

    def feed(self, chunk: bytes) -> None:
        room = self._half - len(self._head)
        if room > 0:
            self._head += chunk[:room]
            chunk = chunk[room:]
        if chunk:
            self._tail += chunk
            if len(self._tail) > self._half:
                self._dropped = True
                del self._tail[: len(self._tail) - self._half]

    def value(self) -> bytes:
        gap = b"\n[... log truncated ...]\n" if self._dropped else b""
        return bytes(self._head) + gap + bytes(self._tail)


def _stop_tree(process: subprocess.Popen[bytes]) -> None:
    """Stop the command and everything it started."""
    with contextlib.suppress(OSError, subprocess.SubprocessError):
        if os.name == "nt":
            subprocess.run(
                ["taskkill", "/T", "/F", "/PID", str(process.pid)],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                timeout=30,
                check=False,
            )
        else:
            os.killpg(process.pid, signal.SIGKILL)
    with contextlib.suppress(OSError):
        process.kill()


def _execute(
    command: Sequence[str],
    cwd: Path | None,
    environment: dict[str, str],
    timeout_seconds: float,
    log: Path,
    on_timeout: Callable[[], None] | None = None,
) -> RunResult:
    started_at = datetime.now(UTC)
    started = time.perf_counter()
    output = BoundedOutput()

    def result(exit_code: int | None, timed_out=False, error=None) -> RunResult:
        elapsed = int((time.perf_counter() - started) * 1000)
        return RunResult(
            started_at, elapsed, exit_code, timed_out, store_log(log, output.value()), error
        )

    try:
        process = subprocess.Popen(
            list(command),
            cwd=cwd,
            env=environment,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            # Its own process group, so a timeout can stop what the command started as well.
            start_new_session=os.name != "nt",
        )
    except OSError as error:
        # Type only: the message can quote local paths.
        output.feed(type(error).__name__.encode())
        return result(None, error=LAUNCH_FAILED)

    def drain() -> None:
        stream = process.stdout
        assert stream is not None
        with contextlib.suppress(OSError, ValueError):
            for chunk in iter(lambda: stream.read(65536), b""):
                output.feed(chunk)

    reader = threading.Thread(target=drain, daemon=True)
    reader.start()
    timed_out = False
    exit_code: int | None
    try:
        exit_code = process.wait(timeout=timeout_seconds)
    except subprocess.TimeoutExpired:
        exit_code, timed_out = None, True
        if on_timeout is not None:
            on_timeout()
        _stop_tree(process)
        with contextlib.suppress(subprocess.TimeoutExpired):
            process.wait(timeout=_DRAIN_SECONDS)
    # A grandchild that survived may still hold the pipe; the log is closed without it.
    reader.join(_DRAIN_SECONDS)
    return result(exit_code, timed_out=timed_out)


class LocalRunner:
    def __init__(self, policy: RunnerPolicy) -> None:
        names = [*_BASE_ENVIRONMENT, *policy.env_allow]
        self._environment = {name: os.environ[name] for name in names if name in os.environ}
        self._environment.update(policy.env)

    def describe(self) -> RunnerInfo:
        return RunnerInfo(
            kind="local",
            os=f"{platform.system()} {platform.release()}",
            architecture=platform.machine(),
            network="unrestricted",
        )

    def environment_names(self) -> list[str]:
        return sorted(self._environment)

    def run(self, argv: Sequence[str], tree: Path, timeout_seconds: float, log: Path) -> RunResult:
        return _execute(argv, tree, self._environment, timeout_seconds, log)


class DockerRunner:
    MOUNT = "/workspace"

    def __init__(self, policy: RunnerPolicy, docker: str = "docker") -> None:
        assert policy.image is not None  # the policy refuses a docker runner without a digest
        self._policy = policy
        self._docker = docker
        # Docker's own client needs a few variables; the container gets only policy.env.
        self._client_environment = {
            name: os.environ[name]
            for name in (*_BASE_ENVIRONMENT, "DOCKER_HOST", "DOCKER_CONTEXT", "USERPROFILE", "HOME")
            if name in os.environ
        }

    def describe(self) -> RunnerInfo:
        return RunnerInfo(
            kind="docker",
            image=self._policy.image,
            os="linux",
            architecture=platform.machine(),
            network="none",
        )

    def environment_names(self) -> list[str]:
        return sorted(self._policy.env)

    def command(self, argv: Sequence[str], tree: Path, name: str | None = None) -> list[str]:
        """The full ``docker run`` line: no network, no privileges, only the tree mounted.

        ``tree`` must be an export of a commit (``Workspace.export``), never a Git checkout:
        the container can write to it, and a Git directory the host later uses would let code in
        the container run commands on the host.
        """
        if (tree / ".git").exists():
            raise ValueError("A Git checkout may not be mounted into the sandbox.")
        policy = self._policy
        command = [
            self._docker,
            "run",
            "--rm",
            *(["--name", name] if name else []),
            "--network",
            "none",
            "--cap-drop",
            "ALL",
            "--security-opt",
            "no-new-privileges",
            # Nothing outside the mounted tree and a scratch /tmp can be written.
            "--read-only",
            "--tmpfs",
            "/tmp:rw,exec,nosuid,size=512m",
            *self._user(),
            "--pids-limit",
            str(policy.pids_limit),
            "--memory",
            policy.memory,
            "--cpus",
            str(policy.cpus),
            "--volume",
            f"{tree.resolve()}:{self.MOUNT}",
            "--workdir",
            self.MOUNT,
        ]
        for name, value in sorted(policy.env.items()):
            command += ["--env", f"{name}={value}"]
        return [*command, str(policy.image), *argv]

    @staticmethod
    def _user() -> list[str]:
        """Run as the service's own user where the host has one to map (not on Windows), so the
        files a run leaves behind can be removed again and nothing runs as root."""
        getuid, getgid = getattr(os, "getuid", None), getattr(os, "getgid", None)
        if getuid is None or getgid is None:
            return []
        return ["--user", f"{getuid()}:{getgid()}"]

    def run(self, argv: Sequence[str], tree: Path, timeout_seconds: float, log: Path) -> RunResult:
        name = f"visioniq-fix-{uuid.uuid4().hex}"

        def kill_container() -> None:
            # Stopping the docker client does not stop the container; this does.
            with contextlib.suppress(OSError, subprocess.SubprocessError):
                subprocess.run(
                    [self._docker, "kill", name],
                    env=self._client_environment,
                    stdin=subprocess.DEVNULL,
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    timeout=60,
                    check=False,
                )

        try:
            command = self.command(argv, tree, name)
        except ValueError:
            return RunResult(
                datetime.now(UTC),
                0,
                None,
                False,
                store_log(log, b"git_checkout_refused"),
                LAUNCH_FAILED,
            )
        return _execute(
            command, None, self._client_environment, timeout_seconds, log, kill_container
        )


def build_runner(policy: RunnerPolicy, production: bool = False) -> Runner:
    """``production`` is true wherever the local runner was not allowed explicitly."""
    if policy.kind == "local":
        if production:
            raise ValueError("The local runner has no network isolation; use docker.")
        return LocalRunner(policy)
    return DockerRunner(policy)
