"""Stand-ins for the fix pipeline tests: a real (tiny) Git repository, a scripted model and a
runner that reports what a test decides instead of starting a process."""

import importlib.util
import sys
import uuid
from collections.abc import Callable, Sequence
from dataclasses import dataclass, field
from datetime import UTC, datetime
from pathlib import Path
from types import ModuleType
from typing import Any
from xml.sax.saxutils import quoteattr

from brief_support import ScriptedProvider, gateway_for

from app.brief.history import TicketText
from app.defect.pipeline import FixSettings
from app.defect.policy import DefectPolicy
from app.defect.runner import RunResult, store_log
from app.defect.schema import FixRequest, RunnerInfo, TicketRef
from app.defect.sources import HistoricalFix
from app.llm.gateway import ModelGateway

ROOT = Path(__file__).resolve().parents[2]
SOURCE = "src/kiosk/retry_policy.py"
OLD_TEST = "tests/test_retry_policy.py"


def load_demo() -> ModuleType:
    """The demo script (scripts/defect-demo), loaded as a module: its repository is the fixture."""
    spec = importlib.util.spec_from_file_location(
        "make_demo_repo", ROOT / "scripts" / "defect-demo" / "make_demo_repo.py"
    )
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


DEMO = load_demo()
TEST_ANSWER: dict[str, Any] = DEMO.OFFLINE_SCRIPT["reproduction_test"]
UNSAFE_FIX, SAFE_FIX = DEMO.OFFLINE_SCRIPT["code_fix"]
TEST_PATH = TEST_ANSWER["test_path"]
TEST_ID = TEST_ANSWER["test_id"]
RED = "AssertionError: captured payment was retried"


@dataclass(frozen=True)
class Demo:
    repo: Path
    base_sha: str
    policy: DefectPolicy


def make_demo(tmp_path: Path, fake_runner: bool = True, **repository: Any) -> Demo:
    """The demo repository under ``tmp_path`` and its policy, with ``repository`` overrides."""
    created = DEMO.create(tmp_path / "demo", sys.executable)
    raw = DEMO.policy(Path(created["repo"]), sys.executable)
    entry = raw["repositories"][0]
    if fake_runner:
        entry["recipes"] = {
            "test_file": ["test-file", "{test_path}", "{junit}"],
            "suite": ["suite", "{junit}"],
            "benchmark": ["benchmark", "{output}"],
        }
        entry["toolchain"] = {}
    entry.update(repository)
    return Demo(Path(created["repo"]), created["base_sha"], DefectPolicy.model_validate(raw))


def settings(tmp_path: Path, **overrides: Any) -> FixSettings:
    values: dict[str, Any] = {"work_dir": tmp_path / "work", "artifact_dir": tmp_path / "evidence"}
    values.update(overrides)
    return FixSettings(**values)


def request(demo: Demo, base_sha: str | None = None, repository_id: str = "kiosk-demo"):
    return FixRequest(
        run_id=uuid.uuid4(),
        ticket=TicketRef(project="Kiosk", id="4711", revision=3),
        repository_id=repository_id,
        base_commit_sha=base_sha or demo.base_sha,
        security_scope_id="project:Kiosk",
    )


@dataclass
class FakeFixSources:
    ticket: TicketText | None = field(
        default_factory=lambda: TicketText(
            "0" * 64, "file", "Bug 4711: customer charged twice", DEMO.TICKET_TEXT
        )
    )
    scope: tuple[str, ...] = (SOURCE,)
    fixes: tuple[HistoricalFix, ...] = (HistoricalFix("101", "PR 201", (SOURCE, OLD_TEST)),)

    def load_ticket(self, project_id: str, ticket_id: str) -> TicketText | None:
        return self.ticket

    def brief_scope(self, project_id: str, ticket_id: str) -> tuple[str, ...]:
        return self.scope

    def historical_fixes(self, ticket: TicketText, file_names: Sequence[str]) -> list:
        return list(self.fixes)


def claude(*answers: Any) -> ScriptedProvider:
    """A scripted model on the code route: answers in order (dict, a status, or an exception)."""
    return ScriptedProvider("claude", list(answers))


def gateway(*answers: Any) -> ModelGateway:
    return gateway_for(claude(*answers))


def fix(content: str, path: str = SOURCE, summary: str = "Change the retry rule") -> dict:
    return {
        "changes": [{"path": path, "content": content}],
        "cause": "should_retry ignores whether the payment was captured.",
        "summary": summary,
        "limitations": [],
    }


@dataclass(frozen=True)
class Call:
    """One command the pipeline asked the runner for."""

    kind: str  # "test-file", "suite" or "benchmark"
    tree: str  # "base" or "candidate"
    test_path: str | None
    root: Path
    number: int

    def source(self, path: str = SOURCE) -> str:
        return (self.root / path).read_text(encoding="utf-8")


# What a fake run reports: "pass", "fail:<message>", "error:<message>", "skip", "missing" (the
# test is not in the report), "noreport", "timeout", "launch" or, for a benchmark, a number.
Behaviour = Callable[[Call], Any]


class FakeRunner:
    """Writes the JUnit report (or benchmark value) a ``Behaviour`` decides; starts nothing."""

    def __init__(self, behaviour: Behaviour, network: str = "none") -> None:
        self._behaviour = behaviour
        self._network = network
        self.calls: list[Call] = []

    def describe(self) -> RunnerInfo:
        return RunnerInfo(
            kind="docker",
            image="fake@sha256:" + "0" * 64,
            os="linux",
            architecture="x86_64",
            network=self._network,
        )  # type: ignore[arg-type]

    def environment_names(self) -> list[str]:
        return ["PYTHONHASHSEED"]

    def run(self, argv: Sequence[str], tree: Path, timeout_seconds: float, log: Path) -> RunResult:
        kind = argv[0]
        test_path = argv[1] if kind == "test-file" else None
        call = Call(kind, tree.name, test_path, tree, len(self.calls) + 1)
        self.calls.append(call)
        step = self._behaviour(call)
        output = tree / argv[-1]

        def result(exit_code: int | None, timed_out=False, error=None) -> RunResult:
            return RunResult(
                datetime.now(UTC),
                5,
                exit_code,
                timed_out,
                store_log(log, str(step).encode()),
                error,
            )

        if step == "timeout":
            return result(None, timed_out=True)
        if step == "launch":
            return result(None, error="runner_launch_failed")
        if step == "noreport":
            return result(2)
        if kind == "benchmark":
            output.write_text(f'{{"metric": "latency", "unit": "ms", "value": {step}}}', "utf-8")
            return result(0)
        name = "test_other" if step == "missing" else TEST_ID if kind == "test-file" else "test_all"
        status, _, message = str(step).partition(":")
        body = {
            "pass": "",
            "missing": "",
            "skip": "<skipped/>",
            "fail": f"<failure message={quoteattr(message)}>trace</failure>",
            "error": f"<error message={quoteattr(message)}>trace</error>",
        }[status]
        output.write_text(
            f'<testsuite><testcase classname="tests.demo" name="{name}">{body}</testcase>'
            "</testsuite>",
            "utf-8",
        )
        return result(0 if status in ("pass", "missing", "skip") else 1)


def honest(call: Call) -> Any:
    """What the demo repository would really do, decided by reading the tree's source file."""
    source = call.source()
    if call.kind == "benchmark":
        return 10.0
    guards_capture = "not captured" in source
    keeps_limit = "attempt < self.max_attempts" in source
    if call.kind == "test-file" and call.test_path == TEST_PATH:
        return "pass" if guards_capture else f"fail:{RED}"
    return "pass" if keeps_limit else "fail:AssertionError: retry did not stop"
