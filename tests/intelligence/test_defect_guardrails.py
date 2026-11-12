"""S11-06 / S11-08: patch guardrails, the fix policy file and regression-test discovery."""

import json
from pathlib import Path

import pytest
from pydantic import ValidationError

from app.defect import regression
from app.defect.guardrails import ProposedChange, check
from app.defect.policy import DefectPolicy, RepositoryPolicy, load_policy, matches, render
from app.defect.schema import RegressionTest
from app.defect.sources import HistoricalFix

ROOT = Path(__file__).resolve().parents[2]
BASELINE = {
    "src/Payments/RetryPolicy.cs": "class RetryPolicy { int Max = 3; }\n",
    "src/Kiosk/ReceiptPrinter.cs": "class ReceiptPrinter { }\n",
    "src/Kiosk/Big.cs": "class Big { }\n",
    "tests/RetryPolicyTests.cs": "class RetryPolicyTests { }\n",
    "src/Demo.csproj": "<Project />\n",
}
REPRODUCTION = "tests/CapturedPaymentTests.cs"


def repository(**overrides) -> RepositoryPolicy:
    values = {
        "repository_id": "demo-repo",
        "source": "https://example.invalid/demo.git",
        "runner": {"kind": "local"},
        "recipes": {"test_file": ["run", "{test_path}", "{junit}"]},
        "new_test_paths": ["tests/**"],
        "test_file_patterns": ["tests/**", "**/*Tests.cs"],
        "writable_paths": ["src/**"],
        "restricted_areas": [
            {"area": "payment", "patterns": ["src/Payments/**"]},
            {"area": "schema", "patterns": ["**/Migrations/**"]},
        ],
    }
    values.update(overrides)
    return RepositoryPolicy.model_validate(values)


def judge(*changes: tuple[str, str], policy: RepositoryPolicy | None = None, shown=None):
    return check(
        [ProposedChange(path, content) for path, content in changes],
        policy or repository(),
        BASELINE.get,
        REPRODUCTION,
        set(BASELINE) - {"src/Kiosk/Big.cs"} if shown is None else shown,
    )


def codes(result) -> list[tuple[str, str]]:
    return [(finding.code, finding.severity) for finding in result.findings]


def test_a_small_change_in_a_writable_file_passes() -> None:
    result = judge(("src/Kiosk/ReceiptPrinter.cs", "class ReceiptPrinter { int Copies = 1; }\n"))

    assert result.status == "PASS" and result.findings == ()
    (file,) = result.files
    assert (file.change, file.added_lines, file.removed_lines) == ("modified", 1, 1)
    assert file.content.endswith("Copies = 1; }\n")


@pytest.mark.parametrize(
    ("path", "code"),
    [
        ("src/Payments/RetryPolicy.cs", "restricted_area_payment"),
        ("src/Data/Migrations/0007_add_column.cs", "restricted_area_schema"),
        ("src/Demo.csproj", "dependency_change"),
        ("src/Kiosk/appsettings.secret.json", "secret_change"),
    ],
)
def test_sensitive_areas_secrets_and_dependencies_stop_for_an_engineer(
    path: str, code: str
) -> None:
    result = judge((path, "changed\n"))

    assert result.status == "REVIEW_REQUIRED" and (code, "review") in codes(result)


def test_a_one_line_change_in_a_restricted_area_still_stops() -> None:
    # 8.6.4: review is about where the change is, not how small it is.
    result = judge(("src/Payments/RetryPolicy.cs", "class RetryPolicy { int Max = 4; }\n"))

    assert result.status == "REVIEW_REQUIRED" and result.files[0].added_lines == 1


def test_text_that_looks_like_a_credential_stops_for_an_engineer() -> None:
    content = 'class ReceiptPrinter { string ApiKey = "password=hunter2-super-secret"; }\n'

    result = judge(("src/Kiosk/ReceiptPrinter.cs", content))

    assert ("secret_content", "review") in codes(result)


@pytest.mark.parametrize(
    ("path", "content", "code"),
    [
        ("../outside.cs", "x\n", "unsafe_path"),
        ("/etc/passwd", "x\n", "unsafe_path"),
        ("src\\Kiosk\\ReceiptPrinter.cs", "x\n", "unsafe_path"),
        ("docs/readme.md", "x\n", "path_not_writable"),
        (REPRODUCTION, "class CapturedPaymentTests { }\n", "test_changed"),
        ("tests/RetryPolicyTests.cs", "// deleted the assertion\n", "test_changed"),
        ("src/Kiosk/bin/Release/Kiosk.dll", "binary\n", "generated_artifact"),
        ("src/Kiosk/ReceiptPrinter.cs", BASELINE["src/Kiosk/ReceiptPrinter.cs"], "unchanged_file"),
        # The model saw this file cut short (or not at all): replacing it would lose the rest.
        ("src/Kiosk/Big.cs", "class Big { int X; }\n", "file_not_shown"),
    ],
)
def test_what_a_fix_may_never_do_is_rejected(path: str, content: str, code: str) -> None:
    result = judge((path, content))

    assert result.status == "BLOCKED" and (code, "block") in codes(result)
    assert result.files == ()


def test_an_empty_proposal_and_a_file_changed_twice_are_rejected() -> None:
    twice = judge(("src/Kiosk/ReceiptPrinter.cs", "a\n"), ("src/Kiosk/ReceiptPrinter.cs", "b\n"))

    assert codes(judge()) == [("no_change", "block")]
    assert ("duplicate_path", "block") in codes(twice)


def test_a_new_source_file_is_allowed() -> None:
    result = judge(("src/Kiosk/CaptureGuard.cs", "class CaptureGuard { }\n"))

    assert result.status == "PASS" and result.files[0].change == "added"


def test_a_change_broader_than_the_repositorys_budget_stops_for_review() -> None:
    small = repository(limits={"max_changed_files": 1, "max_changed_lines": 400})
    tight = repository(limits={"max_changed_lines": 2})
    two = (
        ("src/Kiosk/ReceiptPrinter.cs", "class ReceiptPrinter { int A; }\n"),
        ("src/Kiosk/CaptureGuard.cs", "class CaptureGuard { }\n"),
    )

    assert ("patch_too_broad", "review") in codes(judge(*two, policy=small))
    assert ("patch_too_broad", "review") in codes(judge(*two, policy=tight))
    assert judge(*two).status == "PASS"  # inside the default budget


def test_the_model_is_told_rule_codes_and_paths_only() -> None:
    result = judge(("docs/readme.md", "password=hunter2\n"))

    assert result.feedback() == "path_not_writable (docs/readme.md)"


# ---- The policy file ---------------------------------------------------------------------------


def test_the_example_policy_is_valid_and_uses_the_isolated_runner() -> None:
    policy = load_policy(ROOT / "config" / "defect-policy.example.json")  # has // comments

    (demo,) = policy.repositories
    assert policy.repository("demo-repo") is demo and policy.repository("other") is None
    assert demo.runner.kind == "docker" and "@sha256:" in demo.runner.image
    assert {area.area for area in demo.restricted_areas} == {
        "payment",
        "security",
        "identity",
        "schema",
        "hardware",
    }
    assert not demo.draft_pr_on_incomplete


@pytest.mark.parametrize(
    "change",
    [
        {"runner": {"kind": "docker", "image": "mcr.microsoft.com/dotnet/sdk:8.0"}},  # a tag
        {"runner": {"kind": "docker"}},
        {"runner": {"kind": "local", "env_allow": ["ADO_READ_PAT"]}},
        {"runner": {"kind": "local", "env": {"DB_PASSWORD": "x"}}},
        {"recipes": {"suite": ["run", "{junit}"]}},  # no test_file
        {"recipes": {"test_file": ["run", "{test_path}"]}},  # no report
        {"recipes": {"test_file": ["run", "{test_path}", "{junit}", "{shell}"]}},
        {"recipes": {"test_file": ["run", "{test_path}", "{junit}"], "deploy": ["x"]}},
        {"benchmark": {"applies_to": ["src/**"]}},  # no benchmark recipe
        {"writable_paths": []},
        {"repository_id": "has spaces"},
    ],
)
def test_an_unsafe_or_incomplete_policy_is_refused(change: dict) -> None:
    with pytest.raises(ValidationError):
        repository(**change)


def test_a_policy_file_cannot_carry_unknown_settings(tmp_path: Path) -> None:
    path = tmp_path / "policy.json"
    path.write_text(json.dumps({"revision": "r1", "repositories": [], "auto_merge": True}), "utf-8")

    with pytest.raises(ValidationError):
        load_policy(path)
    assert DefectPolicy(revision="r1").repository("anything") is None


@pytest.mark.parametrize(
    ("path", "pattern", "expected"),
    [
        ("src/Payments/RetryPolicy.cs", "src/Payments/**", True),
        ("src/PaymentsExtra/X.cs", "src/Payments/**", False),
        ("tests/unit/RetryPolicyTests.cs", "**/*Tests.cs", True),
        ("RetryPolicyTests.cs", "**/*Tests.cs", True),
        ("src/a/b/Migrations/001.sql", "**/Migrations/**", True),
        ("src/Kiosk/x.cs", "src/*", False),  # * stays inside one directory
        ("SRC/Kiosk/X.cs", "src/**", True),  # Windows checkouts differ only in case
        (".env.local", ".env.*", True),
    ],
)
def test_globs(path: str, pattern: str, expected: bool) -> None:
    assert matches(path, [pattern]) is expected


def test_recipe_values_are_arguments_not_shell_text() -> None:
    argv = render(["run", "--filter={test_path}", "{junit}", "{unknown}"], {
        "test_path": "tests/a.py; rm -rf /",
        "junit": "out.xml",
    })  # fmt: skip

    assert argv == ["run", "--filter=tests/a.py; rm -rf /", "out.xml", "{unknown}"]


# ---- S11-08: which old tests to replay  ----------------------------------------------------------


def discover(fixes, changed=("src/Payments/RetryPolicy.cs",), **overrides):
    return regression.discover(
        fixes, set(BASELINE), changed, repository(**overrides), {REPRODUCTION}
    )


def test_tests_changed_by_a_linked_fix_are_the_areas_regression_tests() -> None:
    fixes = [
        HistoricalFix(
            "101", "PR 201", ("/src/Payments/RetryPolicy.cs", "/tests/RetryPolicyTests.cs")
        ),
        HistoricalFix("102", "PR 202", ("src/Payments/RetryPolicy.cs", "tests/DeletedTests.cs")),
    ]

    found = discover(fixes)

    assert [(c.test_path, c.tier, c.ticket_id, c.fix_reference, c.available) for c in found] == [
        ("tests/RetryPolicyTests.cs", "historical", "101", "PR 201", True),
        ("tests/DeletedTests.cs", "historical", "102", "PR 202", False),  # gone at the baseline
    ]
    assert found[0].discovery_method == "test_changed_by_linked_fix"


def test_a_test_found_by_name_only_is_labelled_inferred() -> None:
    (found,) = discover([])

    assert (found.test_path, found.tier, found.ticket_id) == (
        "tests/RetryPolicyTests.cs",
        "inferred",
        None,
    )
    assert found.discovery_method == "test_name_matches_changed_file"
    assert discover([], changed=("src/Kiosk/ReceiptPrinter.cs",)) == []


def test_an_inferred_ticket_link_does_not_make_historical_proof() -> None:
    inferred = HistoricalFix("103", "commit abc", ("tests/RetryPolicyTests.cs",), link="inferred")
    explicit = HistoricalFix("101", "PR 201", ("tests/RetryPolicyTests.cs",))

    assert discover([inferred])[0].tier == "inferred"
    assert discover([inferred, explicit])[0].tier == "historical"  # the better evidence wins


def test_the_reproduction_test_and_the_limit_are_respected() -> None:
    fixes = [HistoricalFix("101", "PR 201", (REPRODUCTION, "tests/RetryPolicyTests.cs"))]

    assert [c.test_path for c in discover(fixes)] == ["tests/RetryPolicyTests.cs"]
    assert discover(fixes, limits={"max_regression_tests": 0}) == []


def old(outcome: str, baseline: str | None = None, tier: str = "historical", available=True):
    return RegressionTest(
        test_path=f"tests/{outcome}{baseline}.cs",
        tier=tier,  # type: ignore[arg-type]
        discovery_method="test_changed_by_linked_fix",
        availability="AVAILABLE" if available else "UNAVAILABLE",
        outcome=outcome,  # type: ignore[arg-type]
        baseline_outcome=baseline,  # type: ignore[arg-type]
    )


@pytest.mark.parametrize(
    ("tests", "status", "historical"),
    [
        ([old("PASS")], "PASS", True),
        ([old("PASS", tier="inferred")], "PASS", False),
        # Passes on the baseline, fails on the candidate: the change broke it.
        ([old("PASS"), old("FAIL_DIFFERENT_SIGNATURE", "PASS")], "FAIL", True),
        # Fails on both: it says nothing about the change.
        ([old("FAIL_DIFFERENT_SIGNATURE", "FAIL_DIFFERENT_SIGNATURE")], "INCONCLUSIVE", True),
        ([old("FLAKY")], "INCONCLUSIVE", True),
        ([old("ENVIRONMENT_ERROR")], "INCONCLUSIVE", True),
        ([old("UNAVAILABLE", available=False)], "NOT_FOUND", False),
        ([], "NOT_FOUND", False),
    ],
)
def test_what_a_replay_adds_up_to(tests, status: str, historical: bool) -> None:
    result = regression.summarize(tests)

    assert (result.status, result.historical_found) == (status, historical)
    if not historical:
        assert regression.NOT_FOUND in result.detail
