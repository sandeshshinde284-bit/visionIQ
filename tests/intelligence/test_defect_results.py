"""S11-03 / S11-12 / S11-11: test outcomes, the rerun policy and the performance comparison."""

from datetime import UTC, datetime
from pathlib import Path

import pytest

from app.defect.performance import compare, measure_pairs
from app.defect.results import (
    Case,
    Observation,
    classify,
    find_cases,
    is_specific,
    parse_junit,
    run_with_policy,
    signature,
)
from app.defect.runner import RunResult
from app.defect.schema import ExpectedFailure

EXPECTED = ExpectedFailure(exception="AssertionError", message_contains=["captured payment"])
JUNIT = """<?xml version="1.0"?>
<testsuites><testsuite name="pytest" tests="4">
  <testcase classname="tests.test_pay" name="test_ok"/>
  <testcase classname="tests.test_pay" name="test_red">
    <failure message="AssertionError: captured payment was retried&#10;assert True is False">
      tests/test_pay.py:7: AssertionError</failure>
  </testcase>
  <testcase classname="tests.test_pay" name="test_many[a]"/>
  <testcase classname="tests.test_pay" name="test_later"><skipped message="later"/></testcase>
</testsuite></testsuites>"""


def ran(exit_code: int | None = 1, timed_out: bool = False, error: str | None = None) -> RunResult:
    return RunResult(datetime.now(UTC), 10, exit_code, timed_out, "0" * 64, error)


def failed(message: str, kind: str = "", status: str = "failed") -> list[Case]:
    return [Case("test_red", "tests.test_pay", status, kind, message)]  # type: ignore[arg-type]


def test_a_junit_report_is_read_per_test_case(tmp_path: Path) -> None:
    report = tmp_path / "report.xml"
    report.write_text(JUNIT, encoding="utf-8")

    cases = parse_junit(report)

    assert [(case.name, case.status) for case in cases] == [
        ("test_ok", "passed"),
        ("test_red", "failed"),
        ("test_many[a]", "passed"),
        ("test_later", "skipped"),
    ]
    assert [case.name for case in find_cases(cases, "test_many")] == ["test_many[a]"]
    assert signature(cases[1]) == "AssertionError: captured payment was retried"


def test_a_missing_or_broken_report_is_no_report(tmp_path: Path) -> None:
    broken = tmp_path / "broken.xml"
    broken.write_text("<testsuite><testcase", encoding="utf-8")

    assert parse_junit(tmp_path / "absent.xml") is None
    assert parse_junit(broken) is None


@pytest.mark.parametrize(
    ("run", "cases", "outcome"),
    [
        # Red only when the failure is the predicted one.
        (ran(), failed("AssertionError: captured payment was retried"), "FAIL_TARGET_SIGNATURE"),
        (ran(), failed("AssertionError: receipt was empty"), "FAIL_DIFFERENT_SIGNATURE"),
        (
            ran(),
            failed("captured payment was retried", kind="ValueError"),
            "FAIL_DIFFERENT_SIGNATURE",
        ),
        # A compile error, a missing module or a crashed runner is not red.
        (
            ran(),
            failed("ModuleNotFoundError: No module named 'kiosk'", status="error"),
            "ENVIRONMENT_ERROR",
        ),
        (
            ran(),
            failed("error CS0246: The type or namespace name could not be found"),
            "ENVIRONMENT_ERROR",
        ),
        (ran(2), None, "ENVIRONMENT_ERROR"),
        (ran(None, error="runner_launch_failed"), None, "ENVIRONMENT_ERROR"),
        (ran(None, timed_out=True), None, "TIMEOUT"),
        (ran(0), [Case("test_other", "tests.test_pay", "passed")], "NOT_RUN"),
        (ran(0), [Case("test_red", "tests.test_pay", "skipped")], "NOT_RUN"),
        (ran(0), [Case("test_red", "tests.test_pay", "passed")], "PASS"),
    ],
)
def test_one_execution_is_classified_by_what_it_proved(run, cases, outcome) -> None:
    assert classify(run, cases, "test_red", EXPECTED)[0] == outcome


def test_an_exception_reported_as_an_error_can_still_be_the_defect() -> None:
    # JUnit-style frameworks report an unexpected exception as <error>, not <failure>.
    expected = ExpectedFailure(exception="NullReferenceException", message_contains=["Printer"])
    cases = failed("Object reference not set: Printer", "System.NullReferenceException", "error")

    assert classify(ran(), cases, "test_red", expected)[0] == "FAIL_TARGET_SIGNATURE"


def test_a_whole_file_passes_only_if_every_case_passes() -> None:
    passing = [Case("a", "t", "passed"), Case("b", "t", "skipped")]
    failing = [*passing, Case("c", "t", "failed", message="AssertionError: old bug is back")]

    assert classify(ran(0), passing, None) == ("PASS", None)
    assert classify(ran(1), failing, None) == (
        "FAIL_DIFFERENT_SIGNATURE",
        "AssertionError: old bug is back",
    )
    assert classify(ran(0), [], None)[0] == "NOT_RUN"


def test_the_signature_drops_what_changes_from_run_to_run() -> None:
    case = Case(
        "t",
        "c",
        "failed",
        message="KeyError at 0x7ffd12ab in C:\\work\\run-1\\src\\pay.py:42 after 0.31 seconds",
    )

    assert signature(case) == "KeyError at 0x? in pay.py:? after ? s"


@pytest.mark.parametrize(
    ("fragments", "specific"),
    [
        (["captured payment was retried"], True),
        (["assert 2 == 1"], True),
        (["AssertionError"], False),
        (["Error"], False),
        (["captured payment", "failed"], False),  # every fragment must say something
        (["ab"], False),
    ],
)
def test_a_prediction_must_be_able_to_be_wrong(fragments: list[str], specific: bool) -> None:
    assert is_specific(ExpectedFailure(message_contains=fragments)) is specific


# ---- 8.6.3 rerun policy -----------------------------------------------------------------------


def policy(outcomes: list[str], expectation: str, **limits: int):
    queue = iter(outcomes)
    count = iter(range(1, 100))
    verdict = run_with_policy(
        lambda: Observation(f"X{next(count)}", next(queue)),  # type: ignore[arg-type]
        expectation,  # type: ignore[arg-type]
        **limits,
    )
    return verdict.outcome, verdict.first_outcome, len(verdict.observations)


RED, OTHER = "FAIL_TARGET_SIGNATURE", "FAIL_DIFFERENT_SIGNATURE"


@pytest.mark.parametrize(
    ("outcomes", "expectation", "result"),
    [
        # A pass where a pass is expected is run once.
        (["PASS"], "green", ("PASS", "PASS", 1)),
        # A failure is rerun up to twice; one pass among the reruns makes it FLAKY, not PASS.
        ([RED, "PASS"], "green", ("FLAKY", RED, 2)),
        ([RED, RED, "PASS"], "green", ("FLAKY", RED, 3)),
        ([RED, RED, RED], "green", (RED, RED, 3)),
        # A red is confirmed once; runs that disagree prove nothing.
        ([RED, RED], "red", (RED, RED, 2)),
        ([RED, "PASS"], "red", ("FLAKY", RED, 2)),
        ([RED, OTHER], "red", ("FLAKY", RED, 2)),
        (["PASS", "PASS"], "red", ("PASS", "PASS", 2)),
        (["PASS", RED], "red", ("FLAKY", "PASS", 2)),
        # A run the environment broke is retried once, then it is the result.
        (["ENVIRONMENT_ERROR", "PASS"], "green", ("PASS", "ENVIRONMENT_ERROR", 2)),
        (
            ["ENVIRONMENT_ERROR", "ENVIRONMENT_ERROR"],
            "green",
            ("ENVIRONMENT_ERROR", "ENVIRONMENT_ERROR", 2),
        ),
        (["TIMEOUT", "TIMEOUT"], "red", ("TIMEOUT", "TIMEOUT", 2)),
        (["NOT_RUN"], "red", ("NOT_RUN", "NOT_RUN", 1)),
    ],
)
def test_reruns_add_information_and_never_erase_the_first_result(
    outcomes, expectation, result
) -> None:
    assert policy(outcomes, expectation) == result


def test_the_rerun_budget_is_the_repositorys() -> None:
    assert policy([RED], "green", flaky_reruns=0) == (RED, RED, 1)
    assert policy([RED], "red", red_confirmations=0) == (RED, RED, 1)
    assert policy(["TIMEOUT"], "green", environment_retries=0) == ("TIMEOUT", "TIMEOUT", 1)


# ---- 8.7 performance --------------------------------------------------------------------------


def test_equal_medians_show_no_regression_under_these_conditions_only() -> None:
    result = compare([10.0, 10.2, 9.9, 10.1, 10.0], [10.1, 10.0, 10.2, 9.9, 10.1], 10.0, 15.0)

    assert result.status == "NO_REGRESSION_DEMONSTRATED" and result.pairs == 5
    assert (result.baseline_median, result.candidate_median) == (10.0, 10.1)
    assert result.change_percent == 1.0 and "these conditions only" in result.detail


def test_a_slowdown_beyond_threshold_and_noise_is_a_regression() -> None:
    result = compare([10.0] * 5, [13.0, 13.1, 12.9, 13.0, 13.0], 10.0, 15.0)

    assert (result.status, result.change_percent) == ("REGRESSION", 30.0)


@pytest.mark.parametrize(
    ("baseline", "candidate"),
    [
        ([10.0, 4.0, 19.0, 8.0, 15.0], [10.0] * 5),  # too noisy to say anything
        ([10.0, 10.0], [10.0, 10.0]),  # too few pairs
        ([10.0] * 5, [10.0] * 4),  # not paired
        ([0.0] * 5, [1.0] * 5),  # nothing to compare against
    ],
)
def test_a_measurement_that_cannot_carry_a_conclusion_is_inconclusive(baseline, candidate) -> None:
    assert compare(baseline, candidate, 10.0, 15.0).status == "INCONCLUSIVE"


def test_a_slowdown_inside_the_noise_is_inconclusive_not_a_regression() -> None:
    baseline = [10.0, 8.6, 11.4, 9.3, 10.7]  # spread of 14% around the median
    candidate = [round(value * 1.12, 3) for value in baseline]  # 12% slower, same spread

    result = compare(baseline, candidate, 10.0, 15.0)

    assert (result.status, result.change_percent, result.noise_percent) == (
        "INCONCLUSIVE",
        12.0,
        14.0,
    )
    assert "within the noise" in result.detail


def test_pairs_alternate_drop_warm_up_and_stay_paired() -> None:
    order: list[str] = []
    values = iter([1.0, 1.0, 2.0, 2.0, None, 3.0, 4.0, 4.0])

    def measure(tree: str) -> float | None:
        order.append(tree)
        return next(values)

    baseline, candidate = measure_pairs(measure, pairs=3, warmup_pairs=1)

    assert order == [
        "baseline", "candidate",  # warm-up, discarded
        "candidate", "baseline",
        "baseline", "candidate",  # the baseline run failed: the whole pair is dropped
        "candidate", "baseline",
    ]  # fmt: skip
    assert (baseline, candidate) == ([2.0, 4.0], [2.0, 4.0])
