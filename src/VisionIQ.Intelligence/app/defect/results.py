"""S11-03 / S11-12: what a test run proved (master design 8.6.2, 8.6.3).

A red result only counts when the test failed with the failure that was predicted for the
defect. A compile error, a missing module or a crashed runner is not red. A test that fails once
and passes on a rerun is FLAKY, never PASS: reruns add information, they do not erase the first
result. All of this is decided by code from the runner's JUnit report, not by a model.
"""

from __future__ import annotations

import re
import xml.etree.ElementTree as ElementTree
from collections.abc import Callable, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Literal

from app.defect.runner import RunResult
from app.defect.schema import ExpectedFailure, Outcome

MAX_REPORT_BYTES = 5 * 1024 * 1024
MAX_SIGNATURE_CHARS = 300
MIN_FRAGMENT_CHARS = 4

CaseStatus = Literal["passed", "failed", "error", "skipped"]
Expectation = Literal["red", "green"]

# A failure caused by the test's own setup or by the environment, in any framework: it says
# nothing about the defect.
_ENVIRONMENT = re.compile(
    r"(?i)\b(ModuleNotFoundError|ImportError|SyntaxError|IndentationError|NameError: name|"
    r"fixture '[^']+' not found|collection error|error collecting|cannot find module|"
    r"could not load file or assembly|ClassNotFoundException|NoClassDefFoundError|"
    r"error CS\d{4}|error TS\d{4}|compilation (failed|error)|command not found|"
    r"permission denied|no space left on device|connection refused|"
    r"(could not|unable to|failed to) (connect|resolve|restore))"
)
# The part of the above that can only come from the toolchain, never from the code under test:
# it means "not built" even when a framework reports it as an ordinary failure.
_TOOLING = re.compile(
    r"(?i)\b(error collecting|collection error|could not load file or assembly|"
    r"error CS\d{4}|error TS\d{4}|compilation (failed|error)|command not found|"
    r"no space left on device)"
)
# Fragments that match almost any failure and so predict nothing.
_GENERIC = {"assert", "assertion", "assertionerror", "error", "exception", "fail", "failed"}
_VOLATILE = (
    (re.compile(r"0x[0-9a-fA-F]+"), "0x?"),
    (re.compile(r"(?i)\b(line|ln) \d+"), r"\1 ?"),
    (re.compile(r":\d+(:\d+)?(?=\b|\)|$)"), ":?"),
    (re.compile(r"\b\d+(\.\d+)? ?(ms|s|sec|seconds)\b"), "? s"),
    (re.compile(r"(?i)([a-z]:)?[\\/][^\s:'\"]*[\\/]"), ""),  # directories of absolute paths
)


@dataclass(frozen=True)
class Case:
    name: str
    classname: str
    status: CaseStatus
    kind: str = ""
    message: str = ""
    text: str = ""

    @property
    def failure(self) -> str:
        """Everything the report says about the failure, traceback included."""
        return " ".join(f"{self.kind} {self.message} {self.text}".split())

    @property
    def headline(self) -> str:
        """The exception and its message, without the traceback. A traceback quotes the test's
        own source, so anything the test's author wrote there could be "found" in it."""
        text = self.message
        if not text:
            # No message attribute: the last line of the text names the exception.
            lines = [line for line in self.text.splitlines() if line.strip()]
            text = lines[-1] if lines else ""
        return " ".join(f"{self.kind} {text}".split())


def parse_junit(path: Path) -> list[Case] | None:
    """The test cases of a JUnit XML report, or None when there is no readable report."""
    try:
        if not path.is_file() or path.stat().st_size > MAX_REPORT_BYTES:
            return None
        root = ElementTree.parse(path).getroot()
    except (OSError, ElementTree.ParseError):
        return None
    cases: list[Case] = []
    for element in root.iter("testcase"):
        status: CaseStatus = "passed"
        kind = message = text = ""
        for child in element:
            if child.tag in ("failure", "error", "skipped"):
                status = "failed" if child.tag == "failure" else child.tag  # type: ignore[assignment]
                kind = child.get("type", "")
                message = child.get("message", "")
                text = child.text or ""
                break
        cases.append(
            Case(element.get("name", ""), element.get("classname", ""), status, kind, message, text)
        )
    return cases


def find_cases(cases: Sequence[Case], test_id: str) -> list[Case]:
    """The cases of one test: its name, or the name with a parameter suffix."""
    return [
        case
        for case in cases
        if case.name == test_id
        or case.name.startswith(f"{test_id}[")
        or case.name.startswith(f"{test_id}(")
        or f"{case.classname}.{case.name}".endswith(f".{test_id}")
    ]


def signature(case: Case) -> str:
    """The failure in a stable form: no addresses, line numbers, timings or directories.

    The headline only (exception and its message); the full text stays in the run's log.
    """
    headline = (case.message or case.text).strip().splitlines()
    text = " ".join(f"{case.kind} {headline[0] if headline else ''}".split())
    for pattern, replacement in _VOLATILE:
        text = pattern.sub(replacement, text)
    return text[:MAX_SIGNATURE_CHARS].strip()


def is_specific(expected: ExpectedFailure) -> bool:
    """A prediction must be able to fail: at least one fragment that is not generic."""
    fragments = [" ".join(fragment.lower().split()) for fragment in expected.message_contains]
    return bool(fragments) and all(
        len(fragment) >= MIN_FRAGMENT_CHARS and fragment not in _GENERIC for fragment in fragments
    )


def matches_expected(case: Case, expected: ExpectedFailure) -> bool:
    """Whether the failure is the predicted one, judged on its headline only."""
    failure = case.headline.lower()
    if expected.exception and expected.exception.lower().rsplit(".", 1)[-1] not in failure:
        return False
    return all(" ".join(f.lower().split()) in failure for f in expected.message_contains)


def _failing(cases: Sequence[Case]) -> Case | None:
    return next((case for case in cases if case.status in ("failed", "error")), None)


def classify(
    run: RunResult,
    cases: Sequence[Case] | None,
    test_id: str | None,
    expected: ExpectedFailure | None = None,
) -> tuple[Outcome, str | None]:
    """The outcome of one execution and, for a failure, its signature.

    ``test_id`` None means the whole report: every case must pass. ``expected`` is the predicted
    baseline failure; without it any real failure is FAIL_DIFFERENT_SIGNATURE.
    """
    if run.error is not None:
        return "ENVIRONMENT_ERROR", run.error
    if run.timed_out:
        return "TIMEOUT", None
    if cases is None:
        # No report: the command broke before the test framework could say anything.
        return "ENVIRONMENT_ERROR", "no_test_report"
    selected = list(cases) if test_id is None else find_cases(cases, test_id)
    if not selected:
        broken = _failing(cases)
        if broken is not None and _ENVIRONMENT.search(broken.failure):
            return "ENVIRONMENT_ERROR", signature(broken)
        return "NOT_RUN", None
    failing = _failing(selected)
    if failing is None:
        if all(case.status == "skipped" for case in selected):
            return "NOT_RUN", None
        # The report alone is not proof: it is a file the tested code could have written. A
        # command that did not end with 0 passed only if another case in the report explains it.
        if run.exit_code != 0 and _failing(cases) is None:
            return "ENVIRONMENT_ERROR", "exit_code_contradicts_report"
        return "PASS", None
    # A broken setup shows as an *error* (collection, import, fixture) or as a toolchain message.
    # Any other failure inside the test is a result about the code, whatever its message says:
    # a patch with a NameError in it is a wrong patch, not a broken environment.
    pattern = _ENVIRONMENT if failing.status == "error" else _TOOLING
    if pattern.search(failing.failure):
        return "ENVIRONMENT_ERROR", signature(failing)
    if expected is not None and matches_expected(failing, expected):
        return "FAIL_TARGET_SIGNATURE", signature(failing)
    return "FAIL_DIFFERENT_SIGNATURE", signature(failing)


@dataclass(frozen=True)
class Observation:
    """One execution as the rerun policy sees it."""

    execution_id: str
    outcome: Outcome
    signature: str | None = None


@dataclass(frozen=True)
class CheckVerdict:
    outcome: Outcome
    first_outcome: Outcome
    observations: tuple[Observation, ...]

    @property
    def execution_ids(self) -> list[str]:
        return [observation.execution_id for observation in self.observations]

    @property
    def signature(self) -> str | None:
        return next((o.signature for o in self.observations if o.signature), None)


_BROKEN: tuple[Outcome, ...] = ("ENVIRONMENT_ERROR", "TIMEOUT")
_FAILED: tuple[Outcome, ...] = ("FAIL_TARGET_SIGNATURE", "FAIL_DIFFERENT_SIGNATURE")


def run_with_policy(
    execute: Callable[[], Observation],
    expectation: Expectation,
    flaky_reruns: int = 2,
    red_confirmations: int = 1,
    environment_retries: int = 1,
) -> CheckVerdict:
    """Run a test under the 8.6.3 rerun policy and say what the runs add up to.

    * A run the environment broke is retried ``environment_retries`` times; if it stays broken
      that is the result.
    * Where a pass is expected, a failure is rerun up to ``flaky_reruns`` times. One pass among
      them makes the test FLAKY; the failure is never averaged away.
    * Where the defect's failure is expected, the result is confirmed ``red_confirmations``
      times. Runs that disagree make the test FLAKY, so an unstable red proves nothing.
    """
    observations = [execute()]
    for _ in range(environment_retries):
        if observations[-1].outcome not in _BROKEN:
            break
        observations.append(execute())
    first = observations[0].outcome
    settled = observations[-1].outcome

    def verdict(outcome: Outcome) -> CheckVerdict:
        return CheckVerdict(outcome, first, tuple(observations))

    if settled in _BROKEN or settled == "NOT_RUN":
        return verdict(settled)
    if expectation == "green":
        if settled == "PASS":
            # A pass that needed an environment retry is still a pass of the first real run.
            return verdict("PASS")
        for _ in range(flaky_reruns):
            observations.append(execute())
            if observations[-1].outcome == "PASS":
                return verdict("FLAKY")
        return verdict(settled)
    for _ in range(red_confirmations):
        observations.append(execute())
        if observations[-1].outcome != settled:
            return verdict("FLAKY")
    return verdict(settled)
