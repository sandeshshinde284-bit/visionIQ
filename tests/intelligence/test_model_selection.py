"""Model selection by complexity for a fix run's code tasks: off by default, decided by code."""

from dataclasses import dataclass
from pathlib import Path
from types import SimpleNamespace

import pytest
from brief_support import ScriptedProvider
from defect_support import SAFE_FIX, TEST_ANSWER, FakeFixSources, honest
from pydantic import ValidationError
from test_config import settings as app_settings
from test_defect_pipeline import outcome
from test_llm_gateway import CONTEXT, ledger

from app.defect.complexity import assess, tier_for_attempt
from app.llm import factory
from app.llm.gateway import ModelGateway
from app.llm.routing import RoutingPolicy
from app.llm.types import DataClassification, TaskKind

INTERNAL = DataClassification.INTERNAL
RESTRICTED = DataClassification.RESTRICTED


def model(name: str, model_id: str, *answers) -> ScriptedProvider:
    return ScriptedProvider(name, list(answers), model_id=model_id)


def routing(tiers, access=None, **providers) -> RoutingPolicy:
    limits = access or dict.fromkeys(providers, INTERNAL)
    return RoutingPolicy(providers, limits, tiers=tiers)


# ---- Routing ----------------------------------------------------------------------------------


def test_a_tier_sends_only_the_code_tasks_to_its_model() -> None:
    usual, cheap = model("claude", "opus"), model("claude", "haiku")
    gemini = model("gemini", "flash")
    policy = routing({"simple": "cheap"}, claude=usual, cheap=cheap, gemini=gemini)

    for task in (TaskKind.REPRODUCTION_TEST, TaskKind.CODE_FIX):
        decision = policy.primary(task, INTERNAL, "simple")
        assert (decision.provider, decision.reason) == (cheap, "complexity_simple")
    # Every other task follows the table, whatever tier is named.
    brief = policy.primary(TaskKind.BRIEF_DRAFT, INTERNAL, "simple")
    assert (brief.provider, brief.reason) == (gemini, "routing_table")


def test_without_a_usable_tier_the_routing_table_decides_as_before() -> None:
    usual, cheap = model("claude", "opus"), model("claude", "haiku")
    policy = routing({"simple": "cheap"}, claude=usual, cheap=cheap)
    table = (usual, "routing_table")

    def decided(*arguments):
        decision = policy.primary(TaskKind.CODE_FIX, *arguments)
        return decision.provider, decision.reason

    assert decided(INTERNAL) == table  # no tier asked for
    assert decided(INTERNAL, "complex") == table  # a tier with no entry
    assert decided(INTERNAL, "nonsense") == table
    # No tiers configured at all: identical to a policy built without them.
    plain = RoutingPolicy({"claude": usual}, {"claude": INTERNAL})
    assert plain.primary(TaskKind.CODE_FIX, INTERNAL, "simple").provider is usual


def test_a_tier_never_receives_data_above_its_classification_limit() -> None:
    usual, cheap = model("claude", "opus"), model("gemini", "flash")
    policy = routing(
        {"simple": "cheap"}, {"claude": RESTRICTED, "cheap": INTERNAL}, claude=usual, cheap=cheap
    )

    assert policy.primary(TaskKind.CODE_FIX, INTERNAL, "simple").provider is cheap
    restricted = policy.primary(TaskKind.CODE_FIX, RESTRICTED, "simple")
    assert (restricted.provider, restricted.reason) == (usual, "routing_table")


def test_when_a_tier_model_fails_the_usual_route_is_tried_once() -> None:
    usual = model("claude", "opus", TEST_ANSWER)
    cheap = model("claude", "haiku", RuntimeError("model unavailable"))
    run = ledger()

    result = ModelGateway(routing({"simple": "cheap"}, claude=usual, cheap=cheap)).generate(
        TaskKind.REPRODUCTION_TEST, CONTEXT, run, "simple"
    )

    assert result.result is not None and result.result.model_id == "opus"
    assert [(c.role, c.routing_reason, c.result.model_id) for c in run.calls] == [
        ("primary", "complexity_simple", "haiku"),
        ("fallback", "tier_provider_error", "opus"),
    ]


# ---- Rating -----------------------------------------------------------------------------------


@pytest.mark.parametrize(
    ("files", "band", "areas", "tier"),
    [
        (1, "LOW", [], "simple"),
        (2, "LOW", [], "simple"),
        (3, "LOW", [], "standard"),  # too many files to be simple
        (1, "MODERATE", [], "standard"),
        (1, "INSUFFICIENT_DATA", [], "standard"),
        (1, None, [], "standard"),  # no brief: never called simple without evidence
        (0, "LOW", [], "standard"),  # scope not resolved
        (1, "HIGH", [], "complex"),
        (6, "LOW", [], "complex"),
        (1, "LOW", ["payment"], "complex"),  # a restricted area, however small the change
    ],
)
def test_a_run_is_rated_from_its_scope_brief_and_restricted_areas(files, band, areas, tier) -> None:
    assert assess(files, band, areas).tier == tier


def test_the_rating_says_why_and_its_thresholds_are_configurable() -> None:
    assert assess(1, "HIGH", ["payment", "payment"]).reasons == (
        "restricted_area_payment",
        "risk_band_high",
    )
    assert assess(4, "LOW", [], simple_max_files=4).tier == "simple"
    assert assess(4, "LOW", [], complex_min_files=4).tier == "complex"


def test_each_failed_attempt_moves_one_tier_up() -> None:
    assert [tier_for_attempt("simple", n) for n in (1, 2, 3, 4)] == [
        "simple",
        "standard",
        "complex",
        "complex",
    ]
    assert tier_for_attempt("complex", 1) == "complex"


# ---- Whole fix runs ---------------------------------------------------------------------------


@dataclass
class BriefedSources(FakeFixSources):
    band: str | None = "LOW"

    def brief_risk_band(self, project_id: str, ticket_id: str) -> str | None:
        return self.band


def tiered(usual, **tiers) -> ModelGateway:
    providers = {"claude": usual, **{f"tier-{name}": p for name, p in tiers.items()}}
    return ModelGateway(
        RoutingPolicy(
            providers,
            dict.fromkeys(providers, INTERNAL),
            tiers={name: f"tier-{name}" for name in tiers},
        )
    )


def kinds(provider: ScriptedProvider) -> list[str]:
    return [task.kind.value for task in provider.tasks]


def test_a_simple_fix_runs_on_the_simple_model_and_never_calls_the_usual_one(
    tmp_path: Path,
) -> None:
    usual = model("claude", "opus")
    cheap = model("claude", "haiku", TEST_ANSWER, SAFE_FIX)

    result = outcome(
        tmp_path,
        tiered(usual, simple=cheap),
        honest,
        BriefedSources(),
        model_selection="by_complexity",
    ).result

    assert result.verdict == "VERIFIED"
    assert kinds(cheap) == ["reproduction_test", "code_fix"]
    assert usual.tasks == []


def test_a_failed_attempt_is_retried_one_tier_up(tmp_path: Path) -> None:
    outside = {**TEST_ANSWER, "test_path": "src/kiosk/test_sneaky.py"}  # rejected by the policy
    usual = model("claude", "opus")
    cheap = model("claude", "haiku", outside, SAFE_FIX)
    middle = model("claude", "sonnet", TEST_ANSWER)

    result = outcome(
        tmp_path,
        tiered(usual, simple=cheap, standard=middle),
        honest,
        BriefedSources(),
        model_selection="by_complexity",
    ).result

    assert result.verdict == "VERIFIED"
    assert kinds(cheap) == ["reproduction_test", "code_fix"]  # attempt 1, then the fix
    assert kinds(middle) == ["reproduction_test"]  # attempt 2 of the test, one tier up
    assert usual.tasks == []


def test_a_high_risk_fix_keeps_the_usual_model_when_no_complex_tier_is_set(
    tmp_path: Path,
) -> None:
    usual = model("claude", "opus", TEST_ANSWER, SAFE_FIX)
    cheap = model("claude", "haiku")

    result = outcome(
        tmp_path,
        tiered(usual, simple=cheap),
        honest,
        BriefedSources(band="HIGH"),
        model_selection="by_complexity",
    ).result

    assert result.verdict == "VERIFIED"
    assert cheap.tasks == [] and kinds(usual) == ["reproduction_test", "code_fix"]


def test_switched_off_tiers_are_never_used_even_when_configured(tmp_path: Path) -> None:
    usual = model("claude", "opus", TEST_ANSWER, SAFE_FIX)
    cheap = model("claude", "haiku")

    # FakeFixSources has no brief_risk_band: with the default setting it is never asked for.
    result = outcome(tmp_path, tiered(usual, simple=cheap), honest, FakeFixSources()).result

    assert result.verdict == "VERIFIED"
    assert cheap.tasks == [] and len(usual.tasks) == 2


# ---- Settings and providers -------------------------------------------------------------------


def options(**values) -> SimpleNamespace:
    defaults = {
        "defect_model_selection": "by_complexity",
        "defect_model_simple": "",
        "defect_model_standard": "",
        "defect_model_complex": "",
    }
    return SimpleNamespace(**{**defaults, **values})


def test_each_tier_gets_its_model_with_its_providers_own_data_limit(monkeypatch) -> None:
    built: list[str] = []

    def another(family):
        def make(_settings, model_id=None):
            built.append(f"{family}:{model_id}")
            return model(family, model_id)

        return make

    monkeypatch.setattr(factory, "_claude", another("claude"))
    monkeypatch.setattr(factory, "_gemini", another("gemini"))
    providers = {"gemini": model("gemini", "flash"), "claude": model("claude", "opus")}
    access = {"gemini": RESTRICTED, "claude": INTERNAL}

    tiers = factory.tier_providers(
        options(
            defect_model_simple="gemini",
            defect_model_standard="claude:claude-haiku-5-5",
            defect_model_complex="claude:opus",  # the provider's own model: nothing new to build
        ),
        providers,
        access,
    )

    assert tiers == {"simple": "gemini", "standard": "claude:claude-haiku-5-5", "complex": "claude"}
    assert built == ["claude:claude-haiku-5-5"]
    assert providers["claude:claude-haiku-5-5"].model_id == "claude-haiku-5-5"
    assert access["claude:claude-haiku-5-5"] == INTERNAL  # never wider than Claude's own limit


def test_a_tier_is_ignored_when_off_or_when_its_provider_is_not_available() -> None:
    providers = {"gemini": model("gemini", "flash")}
    access = {"gemini": RESTRICTED}

    off = options(defect_model_selection="fixed", defect_model_simple="gemini")
    assert factory.tier_providers(off, providers, access) == {}
    # Claude is not configured (or not allowed): its tier is left out, not an error.
    missing = options(defect_model_simple="claude:claude-haiku-5-5", defect_model_complex="gemini")
    assert factory.tier_providers(missing, providers, access) == {"complex": "gemini"}
    assert set(providers) == {"gemini"}


def test_tier_settings_are_validated_and_default_to_today_s_behaviour() -> None:
    assert app_settings().defect_model_selection == "fixed"
    chosen = app_settings(
        defect_model_selection="BY_COMPLEXITY", defect_model_simple=" claude:claude-haiku-5-5 "
    )
    assert (chosen.defect_model_selection, chosen.defect_model_simple) == (
        "by_complexity",
        "claude:claude-haiku-5-5",
    )
    for wrong in ("openai:gpt", "claude:", "claude haiku", "claude:model with spaces"):
        with pytest.raises(ValidationError):
            app_settings(defect_model_simple=wrong)
