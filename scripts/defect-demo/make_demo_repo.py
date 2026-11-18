"""Create a small Git repository with a real bug, so the fix pipeline (DEF-01 to DEF-06) can be
tried end to end without Azure DevOps, a database or a model provider.

    python scripts/defect-demo/make_demo_repo.py            # prints the command to run next
    python scripts/defect-demo/make_demo_repo.py --out D:\\tmp\\kiosk-demo

It writes, under the output folder:

* ``repo/``                a Python project (``kiosk``) whose retry policy retries a payment
                           that was already captured. It has older tests, one of them the
                           regression test of a past bug, and a small benchmark.
* ``defect-policy.json``   the fix policy for that repository (local runner).
* ``ticket.json``          the defect ticket #4711, the files its brief predicted and the past
                           fix in the same area.
* ``offline-script.json``  prepared model answers: the reproduction test, then two fixes. The
                           first fix passes the new test but brings back the old bug, so the
                           regression replay must catch it; the second one is correct.

For ENH-02 (the optional starter pull request of an enhancement) it also writes:

* ``enhancement.json``     enhancement ticket #4712 with its guidance, as ENH-01 would store it.
* ``starter-policy.json``  the same policy with starter pull requests switched on.
* ``starter-script.json``  a prepared scaffold: one new source file and one new test file.

Everything is synthetic. The prepared answers stand in for a model only: the tests and the
benchmark are really run, on the baseline and on each candidate. A starter is never run.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from pathlib import Path

REPOSITORY_ID = "kiosk-demo"
TICKET_ID = "4711"

RETRY_POLICY = '''"""When the kiosk retries a payment."""


class RetryPolicy:
    def __init__(self, max_attempts: int = 3) -> None:
        self.max_attempts = max_attempts

    def should_retry(self, attempt: int, captured: bool) -> bool:
        """Retry while attempts remain. A captured payment must never be retried."""
        return attempt < self.max_attempts

    def delays(self) -> list[float]:
        """Seconds to wait before each retry: 0.5, 1, 2, ..."""
        return [0.5 * 2**number for number in range(self.max_attempts - 1)]
'''

RECEIPT = '''"""What the kiosk prints after a payment."""

from kiosk.retry_policy import RetryPolicy


def barcode_lines(order_id: str, attempts: int, captured: bool) -> list[str]:
    """One barcode per order, however often the payment was attempted."""
    policy = RetryPolicy()
    retried = sum(1 for attempt in range(1, attempts) if policy.should_retry(attempt, captured))
    return [f"*{order_id}*"] * (1 + retried)
'''

# The regression test of past bug #101 ("the kiosk retries forever").
OLD_TESTS = """from kiosk.retry_policy import RetryPolicy


def test_retry_stops_after_max_attempts():
    policy = RetryPolicy(max_attempts=3)
    assert policy.should_retry(attempt=1, captured=False) is True
    assert policy.should_retry(attempt=3, captured=False) is False, "retry did not stop"


def test_delays_double():
    assert RetryPolicy(max_attempts=4).delays() == [0.5, 1.0, 2.0]
"""

BENCHMARK = '''"""Time the retry decision. Writes {"metric", "unit", "value"} to the given file."""

import json
import sys
import timeit

sys.path.insert(0, "src")

from kiosk.retry_policy import RetryPolicy  # noqa: E402

policy = RetryPolicy(max_attempts=5)


def workload() -> None:
    for attempt in range(1, 6):
        policy.should_retry(attempt, captured=False)
        policy.should_retry(attempt, captured=True)


# The best of several repeats is the least disturbed by whatever else the machine is doing.
best = min(timeit.repeat(workload, number=20000, repeat=7))
with open(sys.argv[1], "w", encoding="utf-8") as out:
    json.dump({"metric": "retry_decisions_20000x", "unit": "ms", "value": best * 1000}, out)
'''

PYPROJECT = """[project]
name = "kiosk"
version = "1.4.0"

[tool.pytest.ini_options]
pythonpath = ["src", "."]
"""

TICKET_TEXT = (
    "Customer was charged twice at the kiosk and two barcodes were printed.\n"
    "Steps: pay by card, the payment is captured, the receipt printer is slow, the kiosk "
    "retries.\n"
    "Expected: a captured payment is never retried. Actual: RetryPolicy.should_retry returns "
    "True for a captured payment while attempts remain (src/kiosk/retry_policy.py)."
)

REPRODUCTION_TEST = """from kiosk.retry_policy import RetryPolicy


def test_captured_payment_is_not_retried():
    policy = RetryPolicy(max_attempts=3)
    assert policy.should_retry(attempt=1, captured=True) is False, "captured payment was retried"
"""

# Passes the new test, but a payment that was not captured is now retried forever (bug #101).
UNSAFE_FIX = RETRY_POLICY.replace(
    "        return attempt < self.max_attempts\n", "        return not captured\n"
)
SAFE_FIX = RETRY_POLICY.replace(
    "        return attempt < self.max_attempts\n",
    "        return not captured and attempt < self.max_attempts\n",
)

OFFLINE_SCRIPT = {
    "reproduction_test": {
        "reproducible": True,
        "test_path": "tests/test_captured_payment.py",
        "test_id": "test_captured_payment_is_not_retried",
        "test_content": REPRODUCTION_TEST,
        "expected_failure": {
            "exception": "AssertionError",
            "message_contains": ["captured payment was retried"],
        },
        "rationale": "The ticket says a captured payment is retried; the test asks the policy.",
        "checks": "reported_behaviour",
        "limitations": [],
    },
    "code_fix": [
        {
            "changes": [{"path": "src/kiosk/retry_policy.py", "content": UNSAFE_FIX}],
            "cause": "should_retry ignores whether the payment was captured.",
            "summary": "Never retry a captured payment",
            "limitations": [],
        },
        {
            "changes": [{"path": "src/kiosk/retry_policy.py", "content": SAFE_FIX}],
            "cause": "should_retry ignores whether the payment was captured.",
            "summary": "Do not retry a captured payment; keep the attempt limit",
            "limitations": [],
        },
    ],
}


# ---- ENH-02: an enhancement in the same repository, with guidance to start from -----------------

ENHANCEMENT_ID = "4712"
ENHANCEMENT_TEXT = (
    "Give the retry policy a per-terminal override.\n"
    "Today RetryPolicy has one max_attempts for every kiosk. Slow terminals need fewer retries.\n"
    "Add a way to look up the limit per terminal (src/kiosk/retry_policy.py); keep the rule that "
    "a captured payment is never retried."
)
_SECTIONS = (
    "acceptance_criteria_gaps",
    "contracts_and_apis",
    "deployment_and_migration_risk",
    "related_past_changes",
    "do_and_dont",
    "recommended_sequence",
    "tests_to_add",
    "reviewer_decisions",
)
_ADVICE = (
    ("ADV-001", "contracts_and_apis", "APPROACH", "src/kiosk/receipt.py calls RetryPolicy."),
    ("ADV-002", "related_past_changes", "RISK", "Fix #101 stopped retries after max attempts."),
    ("ADV-003", "do_and_dont", "DONT", "Do not change should_retry for captured payments."),
    ("ADV-004", "tests_to_add", "DO", "Add a test for a terminal with its own limit."),
)
GUIDANCE = {
    "brief_id": "5b0c7a52-3a51-4a0e-8f7e-2f4d3a1b9c10",
    "kind": "enhancement",
    "state": "PUBLISHED",
    "predicted_scope": [{"path": "src/kiosk/retry_policy.py", "basis": "named_in_ticket"}],
    "advice": [
        {"advice_id": advice_id, "section": section, "kind": kind, "statement": statement}
        for advice_id, section, kind, statement in _ADVICE
    ],
    "guidance": [
        {
            "section": code,
            "title": code,
            "status": "covered"
            if any(code == section for _, section, _, _ in _ADVICE)
            else "missing",
            "advice_ids": [advice_id for advice_id, section, _, _ in _ADVICE if section == code],
        }
        for code in _SECTIONS
    ],
}
TERMINAL_LIMITS = """\"\"\"Per-terminal retry limits (scaffold: see the TODO comments).\"\"\"

from kiosk.retry_policy import RetryPolicy


class TerminalLimits:
    def __init__(self, default: RetryPolicy) -> None:
        self.default = default
        self._limits: dict[str, int] = {}

    def policy_for(self, terminal_id: str) -> RetryPolicy:
        # TODO(ADV-001): receipt.py builds its own RetryPolicy; decide how it gets this one.
        # TODO(ADV-003): the captured-payment rule stays in RetryPolicy.should_retry.
        raise NotImplementedError
"""
TERMINAL_LIMITS_TEST = """import pytest


@pytest.mark.skip(reason="starter scaffold: TODO(ADV-004) a terminal with its own limit")
def test_a_terminal_with_its_own_limit_uses_it():
    raise NotImplementedError
"""
STARTER_SCRIPT = {
    "starter_scaffold": {
        "suitable": True,
        "unsuitable_reason": "",
        "changes": [
            {"path": "src/kiosk/terminal_limits.py", "content": TERMINAL_LIMITS},
            {"path": "tests/test_terminal_limits.py", "content": TERMINAL_LIMITS_TEST},
        ],
        "summary": "Adds a TerminalLimits lookup next to RetryPolicy, and a test stub for it",
        "checklist": [
            "Decide how receipt.py gets the per-terminal policy (ADV-001)",
            "Implement TerminalLimits.policy_for; keep the captured-payment rule (ADV-003)",
            "Write the test for a terminal with its own limit (ADV-004)",
        ],
        "limitations": [],
    }
}


def starter_policy(repo: Path, python: str = sys.executable) -> dict:
    """The demo policy with starter pull requests switched on for the repository."""
    raw = policy(repo, python)
    raw["repositories"][0]["starter_pr"] = {"enabled": True}
    return raw


def _git(repo: Path, *arguments: str) -> str:
    completed = subprocess.run(
        ["git", "-c", "core.autocrlf=false", *arguments],
        cwd=repo,
        check=True,
        capture_output=True,
        text=True,
    )
    return completed.stdout.strip()


def _commit(repo: Path, message: str) -> str:
    _git(repo, "add", "--all")
    _git(
        repo,
        "-c",
        "user.name=Demo Engineer",
        "-c",
        "user.email=demo@example.invalid",
        "commit",
        "--quiet",
        "-m",
        message,
    )
    return _git(repo, "rev-parse", "HEAD")


def policy(repo: Path, python: str = sys.executable) -> dict:
    """The fix policy for the demo repository, run with the given Python on the local runner."""
    pytest = [python, "-m", "pytest", "-q", "-p", "no:cacheprovider"]
    return {
        "revision": "defect-policy-demo.1",
        "repositories": [
            {
                "repository_id": REPOSITORY_ID,
                "source": str(repo),
                "target_branch": "main",
                "runner": {
                    "kind": "local",
                    "env": {"PYTHONDONTWRITEBYTECODE": "1", "PYTHONHASHSEED": "0"},
                    "test_timeout_seconds": 120,
                    "suite_timeout_seconds": 300,
                    "benchmark_timeout_seconds": 120,
                },
                "recipes": {
                    "test_file": [*pytest, "{test_path}", "--junitxml={junit}"],
                    "suite": [*pytest, "tests", "--junitxml={junit}"],
                    "benchmark": [python, "bench/bench_retry.py", "{output}"],
                },
                "toolchain": {"python": [python, "--version"]},
                "new_test_paths": ["tests/**"],
                "test_file_patterns": ["tests/**"],
                "writable_paths": ["src/**"],
                "restricted_areas": [
                    {"area": "payment", "patterns": ["src/kiosk/payments/**"]},
                    {"area": "schema", "patterns": ["migrations/**"]},
                ],
                "lock_files": ["pyproject.toml"],
                "benchmark": {
                    "applies_to": ["src/kiosk/retry_policy.py"],
                    "pairs": 5,
                    "warmup_pairs": 1,
                    "max_regression_percent": 25,
                    "max_noise_percent": 30,
                },
            }
        ],
    }


def create(out: Path, python: str = sys.executable) -> dict[str, str]:
    """Write the demo into ``out`` and return the paths and the baseline commit SHA."""
    repo = out / "repo"
    files = {
        "pyproject.toml": PYPROJECT,
        "src/kiosk/__init__.py": "",
        "src/kiosk/retry_policy.py": RETRY_POLICY.replace(
            "        return attempt < self.max_attempts\n", "        return True\n"
        ),
        "src/kiosk/receipt.py": RECEIPT,
        "bench/bench_retry.py": BENCHMARK,
    }
    for name, content in files.items():
        (repo / name).parent.mkdir(parents=True, exist_ok=True)
        (repo / name).write_text(content, encoding="utf-8", newline="\n")
    _git(repo, "init", "--quiet", "--initial-branch=main")
    _commit(repo, "Kiosk retry policy and receipt printing")
    # The fix of past bug #101, with its regression test: this is the area's history.
    (repo / "src/kiosk/retry_policy.py").write_text(RETRY_POLICY, encoding="utf-8", newline="\n")
    (repo / "tests").mkdir()
    (repo / "tests/test_retry_policy.py").write_text(OLD_TESTS, encoding="utf-8", newline="\n")
    base_sha = _commit(repo, "Fix #101: stop retrying after max attempts")

    ticket = {
        "project": "Kiosk",
        "id": TICKET_ID,
        "revision": 3,
        "title": "Bug 4711: customer charged twice when the receipt printer is slow",
        "text": TICKET_TEXT,
        "scope": ["src/kiosk/retry_policy.py", "src/kiosk/receipt.py"],
        "historical_fixes": [
            {
                "ticket_id": "101",
                "fix_reference": "PR 201",
                "paths": ["src/kiosk/retry_policy.py", "tests/test_retry_policy.py"],
            }
        ],
    }
    enhancement = {
        "project": "Kiosk",
        "id": ENHANCEMENT_ID,
        "revision": 1,
        "title": "Story 4712: per-terminal retry limit",
        "text": ENHANCEMENT_TEXT,
        "guidance": GUIDANCE,
    }
    written = {
        "defect-policy.json": policy(repo, python),
        "ticket.json": ticket,
        "offline-script.json": OFFLINE_SCRIPT,
        "starter-policy.json": starter_policy(repo, python),
        "enhancement.json": enhancement,
        "starter-script.json": STARTER_SCRIPT,
    }
    for name, content in written.items():
        (out / name).write_text(json.dumps(content, indent=2) + "\n", encoding="utf-8")
    return {
        "repo": str(repo),
        "base_sha": base_sha,
        "policy": str(out / "defect-policy.json"),
        "ticket": str(out / "ticket.json"),
        "offline_script": str(out / "offline-script.json"),
        "starter_policy": str(out / "starter-policy.json"),
        "enhancement": str(out / "enhancement.json"),
        "starter_script": str(out / "starter-script.json"),
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Create the fix pipeline's demo repository.")
    parser.add_argument("--out", type=Path, help="output folder (default: a new temp folder)")
    arguments = parser.parse_args(argv)
    out = arguments.out or Path(tempfile.mkdtemp(prefix="visioniq-defect-demo-"))
    if (out / "repo").exists():
        parser.error(f"{out / 'repo'} already exists; choose another --out")
    out.mkdir(parents=True, exist_ok=True)
    demo = create(out)
    sys.stdout.write(
        f"Demo repository: {demo['repo']}\nBaseline commit: {demo['base_sha']}\n\n"
        "Run the fix pipeline on it (from src/VisionIQ.Intelligence):\n\n"
        f'  python -m app.defect.cli --policy "{demo["policy"]}" --repository {REPOSITORY_ID} '
        f'--base-sha {demo["base_sha"]} --ticket "{demo["ticket"]}" '
        f'--offline-script "{demo["offline_script"]}" --out "{out / "evidence"}"\n\n'
        "Prepare a starter scaffold for the enhancement (ENH-02; nothing is run):\n\n"
        f'  python -m app.starter.cli --policy "{demo["starter_policy"]}" '
        f"--repository {REPOSITORY_ID} "
        f'--base-sha {demo["base_sha"]} --ticket "{demo["enhancement"]}" '
        f'--offline-script "{demo["starter_script"]}" --out "{out / "starter"}"\n'
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
