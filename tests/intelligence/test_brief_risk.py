"""BRF-03: scope prediction (S10-02), deterministic factors (S10-03), explainable band (S10-13)."""

import pytest
from brief_support import DUPLICATE, PRINTED, RETRY_FILES, fix, key

from app.brief import risk
from app.brief.history import HistoryFacts, HotspotHit
from app.brief.schema import ScopeItem
from app.knowledge.retrieval import Lesson, extract_signals

SCOPE = [ScopeItem(path="RetryPolicy.cs", basis="named_in_ticket", confidence="exact")]
IDS = {key("101"): "E1", key("102"): "E2"}


def assess(facts: HistoryFacts, scope=SCOPE, lessons=()):
    return risk.assess(scope, facts, lessons, IDS, 90)


def fixes(count: int, reverted: int = 0):
    return tuple(fix(str(100 + n), str(200 + n), reverted=n < reverted) for n in range(count))


def factor(result, code):
    return next(f for f in result.factors if f.code == code)


# ---- S10-02 ----------------------------------------------------------------------------------


def test_scope_lists_named_files_then_files_past_fixes_changed() -> None:
    signals = extract_signals("Crash in RetryPolicy.cs when the kiosk retries.")
    changed = {
        key("101"): RETRY_FILES,
        key("102"): (*RETRY_FILES, "/src/Payments/Ledger.cs"),
    }

    scope = risk.predict_scope(signals, [PRINTED, DUPLICATE], changed, IDS)

    assert [(s.path, s.basis, s.confidence) for s in scope] == [
        ("RetryPolicy.cs", "named_in_ticket", "exact"),
        ("/src/Kiosk/ReceiptPrinter.cs", "changed_by_past_fix", "inferred"),  # in both fixes
        ("/src/Payments/Ledger.cs", "changed_by_past_fix", "inferred"),
    ]
    assert scope[1].evidence_ids == ["E1", "E2"] and scope[2].evidence_ids == ["E2"]
    assert risk.scope_file_names(scope) == ["ledger.cs", "receiptprinter.cs", "retrypolicy.cs"]


def test_scope_is_empty_when_nothing_points_at_code() -> None:
    assert risk.predict_scope(extract_signals("The page looks wrong."), [PRINTED], {}, IDS) == []


def test_scope_is_capped() -> None:
    changed = {key("101"): tuple(f"/src/a/File{n}.cs" for n in range(40))}

    assert len(risk.predict_scope(extract_signals("x"), [PRINTED], changed, IDS)) == 12


# ---- S10-13: the band and its thresholds -----------------------------------------------------


@pytest.mark.parametrize(
    ("count", "reverted", "band"),
    [
        (0, 0, "LOW"),
        (1, 0, "LOW"),
        (2, 0, "MODERATE"),
        (1, 1, "MODERATE"),
        (3, 1, "HIGH"),
        (2, 2, "HIGH"),
        (5, 0, "HIGH"),
    ],
)
def test_band_follows_the_observed_counts(count: int, reverted: int, band: str) -> None:
    result = assess(HistoryFacts(fixes=fixes(count, reverted), activity=20, hotspots=()))

    assert result.band == band
    assert factor(result, "recent_fixes").value == count
    assert factor(result, "reverted_fixes").value == reverted
    assert factor(result, "change_activity").value == 20


def test_missing_history_is_insufficient_data_never_low() -> None:
    no_history = assess(HistoryFacts(fixes=(), activity=None, hotspots=None))
    no_scope = assess(HistoryFacts(fixes=(), activity=50, hotspots=()), scope=[])

    assert no_history.band == "INSUFFICIENT_DATA" and no_scope.band == "INSUFFICIENT_DATA"
    missing = {f.code for f in no_history.factors if f.missing}
    assert {"recent_fixes", "reverted_fixes", "change_activity", "hotspot_areas"} <= missing
    # A missing count is shown as missing, not as zero.
    assert factor(no_history, "recent_fixes").value is None
    assert "cannot be judged" in no_history.explanation


def test_low_needs_recorded_history_and_says_what_it_saw() -> None:
    result = assess(HistoryFacts(fixes=fixes(1), activity=14, hotspots=()))

    assert result.band == "LOW"
    assert "1 defect fix(es) and no reverts among 14 recorded change(s)" in result.explanation
    assert not any(f.missing for f in result.factors)


def test_every_factor_names_the_ids_behind_its_count() -> None:
    result = assess(
        HistoryFacts(
            fixes=(fix("101", "201"), fix("102", "202", reverted=True), fix("300", "400")),
            activity=30,
            hotspots=(),
        )
    )

    recent = factor(result, "recent_fixes")
    assert recent.references == [
        "ticket 101 -> PR 201",
        "ticket 102 -> PR 202 (reverted)",
        "ticket 300 -> PR 400",
    ]
    assert recent.evidence_ids == ["E1", "E2"] and recent.window_days == 90
    reverted = factor(result, "reverted_fixes")
    assert reverted.references == ["ticket 102 -> PR 202 (reverted)"]
    assert reverted.evidence_ids == ["E2"]


def test_a_live_lesson_or_a_flagged_hotspot_raises_low_to_moderate() -> None:
    lesson = Lesson("p1", 2, "current", "Retry handling breaks printing", ["/src/Payments/x.cs"])
    stale = Lesson("p2", 1, "stale", "Old pattern", ["/src/Payments/y.cs"])
    quiet = HistoryFacts(fixes=fixes(1), activity=20, hotspots=())
    hot = HistoryFacts(
        fixes=fixes(1), activity=20, hotspots=(HotspotHit("/src/Payments", "WATCH", 1.3),)
    )
    elsewhere = HistoryFacts(
        fixes=fixes(1), activity=20, hotspots=(HotspotHit("/src/Billing", "HOT", 2.0),)
    )

    assert assess(quiet, lessons=[lesson]).band == "MODERATE"
    assert assess(quiet, lessons=[stale]).band == "LOW"
    assert assess(hot).band == "MODERATE"
    assert factor(assess(hot), "hotspot_areas").references == [
        "/src/Payments (WATCH, 1.3x the repository rate)"
    ]
    assert assess(elsewhere).band == "LOW"  # a hotspot somewhere else says nothing about here


def test_a_hot_area_with_two_fixes_is_high() -> None:
    facts = HistoryFacts(
        fixes=fixes(2), activity=20, hotspots=(HotspotHit("/src/Payments", "HOT", 1.9),)
    )

    assert assess(facts).band == "HIGH"


def test_no_factor_is_a_probability() -> None:
    result = assess(HistoryFacts(fixes=fixes(4, 1), activity=20, hotspots=()))

    text = result.model_dump_json().lower()
    assert "probab" not in text and "%" not in text and "likelihood" not in text
    assert all(isinstance(f.value, int) for f in result.factors if not f.missing)


def test_history_this_run_cannot_see_in_full_is_never_low() -> None:
    hidden = HistoryFacts(fixes=(), activity=12, hotspots=(), unreadable_fixes=6)
    visible = HistoryFacts(fixes=fixes(1), activity=12, hotspots=())

    assert assess(hidden).band == "INSUFFICIENT_DATA"
    assert "could not be read" in assess(hidden).explanation
    assert "ticket" not in " ".join(
        factor(assess(hidden), "recent_fixes").references
    )  # no ids leak
    assert risk.assess(SCOPE, visible, [], IDS, 90, lessons_known=False).band == "INSUFFICIENT_DATA"
    unknown = factor(risk.assess(SCOPE, visible, [], IDS, 90, lessons_known=False), "known_lessons")
    assert unknown.missing and unknown.value is None
    # Hidden history never lowers a band that the visible history already justifies.
    busy = HistoryFacts(fixes=fixes(3, 1), activity=12, hotspots=(), unreadable_fixes=6)
    assert assess(busy).band == "HIGH"
