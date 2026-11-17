"""What DEFECT shows the model: the code a defect lives in, the code it imports, example tests
of the same kind and the project's own test data."""

from __future__ import annotations

import subprocess
from dataclasses import replace
from pathlib import Path

import structlog
from defect_support import SAFE_FIX, TEST_ANSWER, make_demo
from test_defect_pipeline import logged

from app.defect.pipeline import imported_names, local_imports, order_tests

SCREEN = "ui/src/components/SelectRailcard/SelectRailcard.vue"
SCREEN_SOURCE = """<template><button :disabled="!ready">Continue</button></template>
<script setup lang="ts">
import { useItsoStore } from '@/store/itso'
import RailcardList from '../RailcardList/RailcardList.vue'
import { getSelectRailcardView, type ViewModel } from '@/services/itso'
import axios from 'axios'
import type { Railcard } from './types'
const lazy = await import('./lazy')
</script>
"""
FILES = {
    "ui/package.json",
    "ui/src/store/itso.ts",
    "ui/src/components/RailcardList/RailcardList.vue",
    "ui/src/services/itso/index.ts",
    "ui/src/components/SelectRailcard/types.ts",
    "ui/src/components/SelectRailcard/lazy.js",
    SCREEN,
}


def test_local_imports_follow_relative_paths_and_the_source_alias_never_packages() -> None:
    assert local_imports(SCREEN, SCREEN_SOURCE, FILES) == [
        "ui/src/store/itso.ts",  # "@/" is the src folder of the nearest package.json
        "ui/src/components/RailcardList/RailcardList.vue",
        "ui/src/services/itso/index.ts",  # a folder import resolves to its index file
        "ui/src/components/SelectRailcard/types.ts",
        "ui/src/components/SelectRailcard/lazy.js",  # a dynamic import too; 'axios' is a package
    ]


def test_only_script_files_are_followed_and_missing_targets_are_skipped() -> None:
    assert local_imports("src/Basket.cs", "import x from './y'", {"src/y.ts"}) == []
    assert local_imports(SCREEN, "import a from './missing'", FILES) == []


def test_imported_names_are_the_names_taken_from_local_modules() -> None:
    assert imported_names(SCREEN_SOURCE) == [
        "useItsoStore",
        "RailcardList",
        "getSelectRailcardView",
        "ViewModel",
        "Railcard",
    ]  # nothing from 'axios'


def test_example_tests_of_the_same_kind_and_nearest_come_first() -> None:
    tests = [
        "Api/Core.Tests/BasketTests.cs",
        "ui/src/components/Other/Other.spec.ts",
        "ui/src/components/SelectRailcard/Header.spec.ts",
        "ui/tests/unit/RailcardList.spec.ts",
    ]
    assert order_tests([SCREEN, "ui/src/components/RailcardList/RailcardList.vue"], tests) == [
        "ui/tests/unit/RailcardList.spec.ts",  # same subject
        "ui/src/components/SelectRailcard/Header.spec.ts",  # same kind, nearest
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
