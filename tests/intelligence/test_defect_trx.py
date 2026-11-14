"""DEF-01 / DEF-03 on .NET: a TRX report (``dotnet test --logger trx``) is read like a JUnit one."""

from datetime import UTC, datetime
from pathlib import Path

from app.defect.results import classify, find_cases, parse_junit, parse_report
from app.defect.runner import RunResult
from app.defect.schema import ExpectedFailure

NS = 'xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"'
STACK = (
    "   at Shop.Tests.RetryTests.Captured_payment_is_not_retried() in /src/RetryTests.cs:line 21"
)
MESSAGE = "Assert.False() Failure&#10;Expected: False&#10;Actual: True (captured payment)"
# xUnit: the method name in the definition is fully qualified.
XUNIT = f"""<?xml version="1.0" encoding="utf-8"?>
<TestRun id="r1" {NS}>
  <Results>
    <UnitTestResult testId="t1" testName="Shop.Tests.RetryTests.Captured_payment_is_not_retried"
                    outcome="Failed">
      <Output><ErrorInfo>
        <Message>{MESSAGE}</Message>
        <StackTrace>{STACK}</StackTrace>
      </ErrorInfo></Output>
    </UnitTestResult>
    <UnitTestResult testId="t2" testName="Shop.Tests.RetryTests.Other_case" outcome="Passed"/>
    <UnitTestResult testId="t3" testName="Shop.Tests.RetryTests.Amounts(amount: 5)"
                    outcome="Passed"/>
    <UnitTestResult testId="t4" testName="Shop.Tests.RetryTests.Later" outcome="NotExecuted"/>
  </Results>
  <TestDefinitions>
    <UnitTest name="Shop.Tests.RetryTests.Captured_payment_is_not_retried" id="t1">
      <TestMethod className="Shop.Tests.RetryTests"
                  name="Shop.Tests.RetryTests.Captured_payment_is_not_retried"/>
    </UnitTest>
    <UnitTest name="Shop.Tests.RetryTests.Other_case" id="t2">
      <TestMethod className="Shop.Tests.RetryTests" name="Shop.Tests.RetryTests.Other_case"/>
    </UnitTest>
    <UnitTest name="Shop.Tests.RetryTests.Amounts(amount: 5)" id="t3">
      <TestMethod className="Shop.Tests.RetryTests" name="Shop.Tests.RetryTests.Amounts"/>
    </UnitTest>
    <UnitTest name="Shop.Tests.RetryTests.Later" id="t4">
      <TestMethod className="Shop.Tests.RetryTests" name="Shop.Tests.RetryTests.Later"/>
    </UnitTest>
  </TestDefinitions>
</TestRun>"""
# NUnit and MSTest: the method name is short.
SHORT = f"""<TestRun {NS}>
  <Results>
    <UnitTestResult testId="a" testName="Throws_on_retry" outcome="Failed">
      <Output><ErrorInfo>
        <Message>System.InvalidOperationException : payment already captured</Message>
        <StackTrace>   at Shop.Payments.Retry.Run()</StackTrace>
      </ErrorInfo></Output>
    </UnitTestResult>
    <UnitTestResult testId="b" testName="Hangs" outcome="Timeout"/>
    <UnitTestResult testId="c" testName="Odd" outcome="SomethingNew"/>
  </Results>
  <TestDefinitions>
    <UnitTest name="Throws_on_retry" id="a">
      <TestMethod className="Shop.Tests.RetryTests" name="Throws_on_retry"/>
    </UnitTest>
    <UnitTest name="Hangs" id="b">
      <TestMethod className="Shop.Tests.RetryTests" name="Hangs"/>
    </UnitTest>
    <UnitTest name="Odd" id="c">
      <TestMethod className="Shop.Tests.RetryTests" name="Odd"/>
    </UnitTest>
  </TestDefinitions>
</TestRun>"""


def ran(exit_code: int | None = 1) -> RunResult:
    return RunResult(datetime.now(UTC), 10, exit_code, False, "0" * 64, None)


def report(tmp_path: Path, content: str) -> Path:
    path = tmp_path / "X1.xml"
    path.write_text(content, encoding="utf-8")
    return path


def test_an_xunit_trx_report_is_read_per_test_case(tmp_path: Path) -> None:
    cases = parse_report(report(tmp_path, XUNIT))

    assert cases is not None
    assert [(c.name, c.classname, c.status) for c in cases] == [
        ("Captured_payment_is_not_retried", "Shop.Tests.RetryTests", "failed"),
        ("Other_case", "Shop.Tests.RetryTests", "passed"),
        ("Amounts(amount: 5)", "Shop.Tests.RetryTests", "passed"),  # arguments stay on the name
        ("Later", "Shop.Tests.RetryTests", "skipped"),
    ]
    red = cases[0]
    assert "captured payment" in red.message and "RetryTests.cs:line 21" in red.text
    assert "RetryTests.cs" not in red.headline  # the stack trace is never part of the headline
    # Found by method, by Class.Method and with its arguments, exactly as a JUnit case is.
    assert find_cases(cases, "Captured_payment_is_not_retried") == [red]
    assert find_cases(cases, "RetryTests.Captured_payment_is_not_retried") == [red]
    assert [c.name for c in find_cases(cases, "Amounts")] == ["Amounts(amount: 5)"]


def test_the_predicted_failure_is_recognised_from_a_trx_report(tmp_path: Path) -> None:
    cases = parse_report(report(tmp_path, XUNIT))
    predicted = ExpectedFailure(message_contains=["captured payment"])
    other = ExpectedFailure(message_contains=["something else entirely"])
    test_id = "Captured_payment_is_not_retried"

    assert classify(ran(1), cases, test_id, predicted)[0] == "FAIL_TARGET_SIGNATURE"
    assert classify(ran(1), cases, test_id, other)[0] == "FAIL_DIFFERENT_SIGNATURE"
    assert classify(ran(0), cases, "Other_case")[0] == "PASS"
    assert classify(ran(0), cases, "Later")[0] == "NOT_RUN"


def test_nunit_and_mstest_names_and_exception_messages_are_read(tmp_path: Path) -> None:
    cases = parse_report(report(tmp_path, SHORT))
    predicted = ExpectedFailure(
        exception="System.InvalidOperationException", message_contains=["already captured"]
    )

    assert cases is not None
    assert (cases[0].name, cases[0].classname) == ("Throws_on_retry", "Shop.Tests.RetryTests")
    assert classify(ran(1), cases, "Throws_on_retry", predicted)[0] == "FAIL_TARGET_SIGNATURE"


def test_an_unclear_trx_outcome_is_never_a_pass(tmp_path: Path) -> None:
    cases = parse_report(report(tmp_path, SHORT))

    assert cases is not None
    assert {c.name: c.status for c in cases}["Hangs"] == "error"  # Timeout
    assert {c.name: c.status for c in cases}["Odd"] == "error"  # an outcome this code does not know
    assert classify(ran(0), cases, "Odd")[0] != "PASS"


def test_an_empty_trx_report_means_the_test_did_not_run(tmp_path: Path) -> None:
    cases = parse_report(report(tmp_path, f"<TestRun {NS}><Results/></TestRun>"))

    assert cases == []
    assert classify(ran(0), cases, "Anything")[0] == "NOT_RUN"


def test_junit_reports_are_read_as_before(tmp_path: Path) -> None:
    junit = '<testsuite><testcase classname="t.test_pay" name="test_ok"/></testsuite>'

    cases = parse_junit(report(tmp_path, junit))

    assert cases is not None and (cases[0].name, cases[0].status) == ("test_ok", "passed")
    assert parse_report is parse_junit
