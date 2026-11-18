"""What DEFECT shows the model: the code a defect lives in, the code it imports, example tests
of the same kind and the project's own test data."""

from __future__ import annotations

import subprocess
from dataclasses import replace
from pathlib import Path

import structlog
from defect_support import SAFE_FIX, TEST_ANSWER, make_demo
from test_defect_pipeline import logged

from app.defect.pipeline import imported_names, local_imports, order_imports, order_tests

SCREEN = "ui/src/components/CheckoutScreen/CheckoutScreen.vue"
SCREEN_SOURCE = """<template><button :disabled="!ready">Continue</button></template>
<script setup lang="ts">
import { useCartStore } from '@/store/cart'
import OptionList from '../OptionList/OptionList.vue'
import { getCheckoutView, type ViewModel } from '@/services/cart'
import axios from 'axios'
import type { Option } from './types'
const lazy = await import('./lazy')
</script>
"""
FILES = {
    "ui/package.json",
    "ui/src/store/cart.ts",
    "ui/src/components/OptionList/OptionList.vue",
    "ui/src/services/cart/index.ts",
    "ui/src/components/CheckoutScreen/types.ts",
    "ui/src/components/CheckoutScreen/lazy.js",
    SCREEN,
}


def test_local_imports_follow_relative_paths_and_the_source_alias_never_packages() -> None:
    assert local_imports(SCREEN, SCREEN_SOURCE, FILES) == [
        "ui/src/store/cart.ts",  # "@/" is the src folder of the nearest package.json
        "ui/src/components/OptionList/OptionList.vue",
        "ui/src/services/cart/index.ts",  # a folder import resolves to its index file
        "ui/src/components/CheckoutScreen/types.ts",
        "ui/src/components/CheckoutScreen/lazy.js",  # a dynamic import too; 'axios' is a package
    ]


def test_only_script_files_are_followed_and_missing_targets_are_skipped() -> None:
    assert local_imports("src/Basket.cs", "import x from './y'", {"src/y.ts"}) == []
    assert local_imports(SCREEN, "import a from './missing'", FILES) == []


def test_imported_names_are_the_names_taken_from_local_modules() -> None:
    assert imported_names(SCREEN_SOURCE) == [
        "useCartStore",
        "OptionList",
        "getCheckoutView",
        "ViewModel",
        "Option",
    ]  # nothing from 'axios'


def test_example_tests_of_the_same_kind_and_nearest_come_first() -> None:
    tests = [
        "Api/Core.Tests/BasketTests.cs",
        "ui/src/components/Other/Other.spec.ts",
        "ui/src/components/CheckoutScreen/Header.spec.ts",
        "ui/tests/unit/OptionList.spec.ts",
    ]
    assert order_tests([SCREEN, "ui/src/components/OptionList/OptionList.vue"], tests) == [
        "ui/tests/unit/OptionList.spec.ts",  # same subject
        "ui/src/components/CheckoutScreen/Header.spec.ts",  # same kind, nearest
        "ui/src/components/Other/Other.spec.ts",
        "Api/Core.Tests/BasketTests.cs",  # another kind: last
    ]


def _commit(repo: Path, files: dict[str, str]) -> str:
    for name, text in files.items():
        target = repo / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text, encoding="utf-8")
    subprocess.run(["git", "-C", str(repo), "add", "."], check=True)
    subprocess.run(
        [
            "git",
            "-C",
            str(repo),
            "-c",
            "user.name=Test",
            "-c",
            "user.email=test@example.invalid",
            "commit",
            "-m",
            "add a screen",
        ],
        check=True,
        capture_output=True,
    )
    return subprocess.check_output(["git", "-C", str(repo), "rev-parse", "HEAD"], text=True).strip()


def test_a_screen_brings_its_store_its_service_and_the_project_mock_data(tmp_path: Path) -> None:
    # Seen live: the model found the likely cause but declined to write a test without the store's
    # defaults and the mocked API payload, which the screen imports.
    demo = make_demo(tmp_path, test_data_paths=["**/mocks/**"], limits={"max_context_files": 10})
    sha = _commit(
        demo.repo,
        {
            "web/package.json": "{}",
            "web/src/RetryBanner.vue": (
                "<script setup>\n// shows retry_policy state\n"
                "import { fetchRetryState } from './api/retryClient'\n"
                "import { useRetryStore } from '@/stores/retry'\n</script>\n"
            ),
            "web/src/api/retryClient.ts": "export function fetchRetryState() {}\n",
            "web/src/stores/retry.ts": "export const useRetryStore = () => ({})\n",
            "web/mocks/retry.json": '{"fetchRetryState": {"retries": 2}}\n',
        },
    )
    previous = structlog.get_config()
    _, events = logged(tmp_path, [TEST_ANSWER, SAFE_FIX], demo=replace(demo, base_sha=sha))
    structlog.configure(**previous)
    by_event = {e["event"]: e for e in events}

    assert "web/src/RetryBanner.vue" in by_event["fix_related_files_added"]["resolved_paths"]
    assert by_event["fix_imported_files_added"]["resolved_paths"] == [
        "web/src/api/retryClient.ts",
        "web/src/stores/retry.ts",
    ]
    assert by_event["fix_test_context"]["test_data"] == ["web/mocks/retry.json"]
    assert by_event["fix_test_context"]["example_tests"][0] == "tests/test_retry_policy.py"


def test_without_test_data_paths_no_test_data_is_shown(tmp_path: Path) -> None:
    _, events = logged(tmp_path, [TEST_ANSWER, SAFE_FIX])

    context = next(e for e in events if e["event"] == "fix_test_context")
    assert context["test_data"] == []


def test_tests_of_the_files_closest_to_the_defect_come_first() -> None:
    # Seen live: with more files shown, ties broken by path put a calendar's test (02-...) ahead of
    # the screen's own test, both for the examples and for the existing tests shown before a fix.
    code = [
        "ui/src/components/widgets/OptionList/OptionList.vue",  # in scope
        "ui/src/components/screens/CheckoutScreen/CheckoutScreen.vue",  # uses it
        "ui/src/components/inputs/DatePicker/DatePicker.vue",  # imported
    ]
    tests = [
        "ui/src/components/inputs/DatePicker/DatePicker.test.ts",
        "ui/src/components/screens/CheckoutScreen/CheckoutScreen.test.ts",
        "ui/src/components/widgets/OptionList/OptionList.test.ts",
    ]

    assert order_tests(code, tests) == [
        "ui/src/components/widgets/OptionList/OptionList.test.ts",
        "ui/src/components/screens/CheckoutScreen/CheckoutScreen.test.ts",
        "ui/src/components/inputs/DatePicker/DatePicker.test.ts",
    ]


def test_what_a_test_must_set_up_comes_before_child_components() -> None:
    found = local_imports(SCREEN, SCREEN_SOURCE, FILES)

    assert order_imports(found) == [
        "ui/src/store/cart.ts",
        "ui/src/services/cart/index.ts",
        "ui/src/components/CheckoutScreen/types.ts",
        "ui/src/components/CheckoutScreen/lazy.js",
        "ui/src/components/OptionList/OptionList.vue",  # a child component: last
    ]


def test_a_new_test_file_is_refused_when_the_code_it_exercises_has_a_test_file(
    tmp_path: Path,
) -> None:
    # The test goes into the existing test file of the code it exercises; a new file only when
    # there is none. The existing file is shown to the next attempt so the test can be added.
    from brief_support import ScriptedProvider, gateway_for

    demo = make_demo(
        tmp_path,
        test_file_patterns=["tests/**", "**/*.test.ts"],
        new_test_paths=["tests/**", "**/*.test.ts"],
    )
    existing = "import Widget from './Widget.vue'\ndescribe('Widget', () => {})\n"
    sha = _commit(
        demo.repo,
        {
            "web/package.json": "{}",
            "web/src/Widget.vue": "<template><button>Go</button></template>\n",
            "web/src/Widget.test.ts": existing,
        },
    )
    new_file = {
        **TEST_ANSWER,
        "test_path": "web/src/WidgetEnabled.test.ts",
        "test_id": "keeps Go enabled",
        "test_content": "import Widget from './Widget.vue'\nit('keeps Go enabled', () => {})\n",
    }
    seen: list = []
    answers = [new_file, TEST_ANSWER, SAFE_FIX]

    def script(_task, context):
        seen.append(context)
        return answers[len(seen) - 1]

    previous = structlog.get_config()
    _, events = logged(
        tmp_path,
        gateway_for(ScriptedProvider("claude", script)),
        demo=replace(demo, base_sha=sha),
    )
    structlog.configure(**previous)

    refused = next(e for e in events if e["event"] == "fix_reproduction_test_declined")
    assert "already has a test file: web/src/Widget.test.ts" in refused["detail"]
    shown = {file.path: file.content for file in seen[1].code}
    assert shown["web/src/Widget.test.ts"] == existing
    assert "Add the test to that file" in seen[1].feedback[-1]


# ---- A model suggests files and picks related tests; related tests run in one batch -------------

FAKE_RECIPES = {
    "test_file": ["test-file", "{test_path}", "{junit}"],
    "suite": ["suite", "{junit}"],
    "benchmark": ["benchmark", "{output}"],
}


def _models(claude_answers: list, gemini_answer):
    """Claude for the test and the fix; Gemini for the two choices. Gemini's contexts are kept."""
    from brief_support import ScriptedProvider, gateway_for

    asked: list = []

    def gemini(task, context):
        asked.append((task.kind, context))
        return gemini_answer(task.kind)

    claude = ScriptedProvider("claude", claude_answers)
    return asked, gateway_for(claude, ScriptedProvider("gemini", gemini))


def _area(demo) -> str:
    """Another source file, a test of the code that uses the changed file, and an old test of
    another area that a past fix changed together with it."""
    return _commit(
        demo.repo,
        {
            "src/kiosk/charges.py": "def charge():\n    return 1\n",
            "tests/test_receipt.py": "def test_receipt_shows_total():\n    assert True\n",
            "tests/test_other_area.py": "def test_printer_paper():\n    assert True\n",
        },
    )


def test_a_model_suggests_files_from_a_list_and_only_listed_paths_are_taken(
    tmp_path: Path,
) -> None:
    from app.llm.types import TaskKind

    demo = make_demo(tmp_path)
    sha = _area(demo)

    def answer(kind):
        if kind is TaskKind.FILE_SELECTION:
            return {
                "files": [
                    {"path": "src/kiosk/charges.py", "reason": "Charging is what the ticket says."},
                    {"path": "src/kiosk/made_up.py", "reason": "Not in the list."},
                ]
            }
        return {"tests": []}

    asked, model = _models([TEST_ANSWER, SAFE_FIX], answer)
    previous = structlog.get_config()
    _, events = logged(tmp_path, model, demo=replace(demo, base_sha=sha))
    structlog.configure(**previous)

    by_event = {e["event"]: e for e in events}
    assert by_event["fix_files_suggested"]["paths"] == ["src/kiosk/charges.py"]
    assert "src/kiosk/charges.py" in by_event["fix_workspace_ready"]["context_files"]
    choices = asked[0][1].choices
    assert "src/kiosk/charges.py" in choices and not any(c.startswith("tests/") for c in choices)


def test_a_model_picks_the_related_tests_and_the_changed_files_tests_are_always_run(
    tmp_path: Path,
) -> None:
    from defect_support import OLD_TEST, SOURCE, FakeFixSources

    from app.defect import sources
    from app.llm.types import TaskKind

    demo = make_demo(tmp_path)
    sha = _area(demo)
    history = (
        sources.HistoricalFix("101", "PR 201", (SOURCE, OLD_TEST)),
        sources.HistoricalFix("102", "PR 202", (SOURCE, "tests/test_other_area.py")),
    )

    def answer(kind):
        if kind is TaskKind.TEST_SELECTION:
            return {"tests": [{"path": "tests/test_receipt.py", "reason": "Uses the retry rule."}]}
        return {"files": []}

    asked, model = _models([TEST_ANSWER, SAFE_FIX], answer)
    result = run_fix_with(tmp_path, demo, sha, model, FakeFixSources(fixes=history))

    ran = [test.test_path for test in result.regression.tests]
    assert ran == [OLD_TEST, "tests/test_receipt.py"]  # the changed file's own test, then the pick
    choices = next(context for kind, context in asked if kind is TaskKind.TEST_SELECTION).choices
    assert any(
        c.startswith("tests/test_receipt.py | found as: test of code that uses") for c in choices
    )
    assert any("test_printer_paper" in c for c in choices)  # test names help the choice
    assert any("judged unrelated to this change" in line for line in result.limitations)
    assert result.verdict == "VERIFIED"


def test_related_tests_run_in_one_batch_and_one_by_one_only_when_it_fails(tmp_path: Path) -> None:
    from defect_support import Call, FakeRunner, honest

    demo = make_demo(
        tmp_path, recipes={**FAKE_RECIPES, "test_files": ["test-files", "{test_paths}", "{junit}"]}
    )
    sha = _area(demo)

    def passing(call: Call):
        return "pass" if call.kind == "test-files" else honest(call)

    runner = FakeRunner(passing)
    result = run_fix_with(tmp_path, demo, sha, [TEST_ANSWER, SAFE_FIX], runner=runner)

    batch = [call for call in runner.calls if call.kind == "test-files"]
    assert len(batch) == 1 and result.verdict == "VERIFIED"
    assert len({tuple(test.execution_ids) for test in result.regression.tests}) == 1
    assert len(result.regression.tests) >= 2

    def failing(call: Call):
        return "fail:AssertionError: something" if call.kind == "test-files" else honest(call)

    runner = FakeRunner(failing)
    result = run_fix_with(tmp_path / "again", demo, sha, [TEST_ANSWER, SAFE_FIX], runner=runner)

    alone = [call for call in runner.calls if call.kind == "test-file" and call.tree == "candidate"]
    assert len(alone) >= len(result.regression.tests)  # each one alone, to tell which failed
    assert result.verdict == "VERIFIED"  # the batch failure was not a regression of any test


def run_fix_with(tmp_path, demo, sha, model, sources=None, runner=None):
    from defect_support import FakeRunner, honest
    from test_defect_pipeline import run

    return run(tmp_path, model, runner or FakeRunner(honest), sources, replace(demo, base_sha=sha))
