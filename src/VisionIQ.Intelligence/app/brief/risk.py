"""S10-02 scope prediction, S10-03 deterministic risk factors, S10-13 explainable risk band.

No model is involved here. The scope is read from the ticket and from what past fixes of similar
tickets changed; the factors are plain counts with the ids behind them; the band is an ordinal
label from fixed thresholds (master design 7.5). Missing data gives INSUFFICIENT_DATA, never LOW,
and nothing here is a probability.
"""

from __future__ import annotations

from collections import Counter
from collections.abc import Mapping, Sequence

from app.brief.history import FixRecord, HistoryFacts
from app.brief.schema import Risk, RiskBand, RiskFactor, ScopeItem
from app.knowledge.retrieval import Lesson, Result, Signals

MAX_SCOPE_ITEMS = 12
MAX_REFERENCES = 10
MAX_AGREEING_ANCHORS = 3

# Band thresholds. They are an initial, reviewable rule (7.5 asks for an ADR and a labelled
# hold-out set before they are called predictive); change them here, in one place.
HIGH_REVERTED = 2
HIGH_FIXES = 5
HIGH_FIXES_WITH_REVERT = 3
MODERATE_FIXES = 2


def predict_scope(
    signals: Signals,
    results: Sequence[Result],
    fix_paths: Mapping[str, Sequence[str]],
    evidence_ids: Mapping[str, str],
) -> list[ScopeItem]:
    """Files this ticket probably touches: named in it (exact), or changed by past fixes of the
    most similar tickets (inferred, most often changed first).

    Only close matches may widen the scope: tickets whose fix touched the code the ticket names
    (a code-path match) or, when there is none, the few results two independent searches agree
    on. A loosely related result would otherwise drag unrelated files, and their fixes, into the
    risk count.
    """
    scope = [
        ScopeItem(path=name, basis="named_in_ticket", confidence="exact") for name in signals.files
    ]
    named = {name.lower() for name in signals.files}
    changed: Counter[str] = Counter()
    cited: dict[str, list[str]] = {}
    anchors = [r for r in results if r.lanes.get("code_path")] or [
        r for r in results if "keyword" in r.lanes and "vector" in r.lanes
    ][:MAX_AGREEING_ANCHORS]
    for result in anchors:
        for path in fix_paths.get(result.item_key, ()):
            if path.rsplit("/", 1)[-1].lower() in named:
                continue  # already listed as named in the ticket
            changed[path] += 1
            if result.item_key in evidence_ids:
                cited.setdefault(path, []).append(evidence_ids[result.item_key])
    for path, _ in sorted(changed.items(), key=lambda entry: (-entry[1], entry[0])):
        if len(scope) >= MAX_SCOPE_ITEMS:
            break
        scope.append(
            ScopeItem(
                path=path,
                basis="changed_by_past_fix",
                confidence="inferred",
                evidence_ids=sorted(set(cited.get(path, []))),
            )
        )
    return scope[:MAX_SCOPE_ITEMS]


def scope_file_names(scope: Sequence[ScopeItem]) -> list[str]:
    """Base file names of the scope, as the history queries match them."""
    return sorted({item.path.replace("\\", "/").rsplit("/", 1)[-1].lower() for item in scope})


def _references(fixes: Sequence[FixRecord]) -> list[str]:
    return [fix.reference for fix in fixes[:MAX_REFERENCES]]


def _evidence(fixes: Sequence[FixRecord], evidence_ids: Mapping[str, str]) -> list[str]:
    return sorted(
        {evidence_ids[f.ticket_item_key] for f in fixes if f.ticket_item_key in evidence_ids}
    )


def assess(
    scope: Sequence[ScopeItem],
    facts: HistoryFacts,
    lessons: Sequence[Lesson],
    evidence_ids: Mapping[str, str],
    window_days: int,
    lessons_known: bool = True,
) -> Risk:
    """The factors and the band for one ticket. ``lessons_known`` is False when the lesson
    search failed: an empty list then means "not known", not "none"."""
    fixes = list(facts.fixes)
    reverted = [fix for fix in fixes if fix.reverted]
    live_lessons = [lesson for lesson in lessons if lesson.status in ("current", "candidate")]
    # Hotspot areas are stored without a leading slash ("src/Payments").
    scope_paths = {
        path.replace("\\", "/").lstrip("/")
        for path in [*(p for fix in fixes for p in fix.paths), *(item.path for item in scope)]
    }
    hot = (
        None
        if facts.hotspots is None
        else [
            hit
            for hit in facts.hotspots
            if any(
                path == hit.area.lstrip("/") or path.startswith(hit.area.lstrip("/") + "/")
                for path in scope_paths
            )
        ]
    )
    history_known = bool(scope) and facts.activity is not None

    factors = [
        RiskFactor(
            code="recent_fixes",
            label=f"Defect fixes that changed these files in the last {window_days} days",
            value=len(fixes) if history_known else None,
            window_days=window_days,
            missing=not history_known,
            references=_references(fixes),
            evidence_ids=_evidence(fixes, evidence_ids),
        ),
        RiskFactor(
            code="reverted_fixes",
            label="Of those fixes, how many were later reverted",
            value=len(reverted) if history_known else None,
            window_days=window_days,
            missing=not history_known,
            references=_references(reverted),
            evidence_ids=_evidence(reverted, evidence_ids),
        ),
        RiskFactor(
            code="change_activity",
            label=f"All recorded changes to these files in the last {window_days} days",
            value=facts.activity,
            window_days=window_days,
            missing=facts.activity is None,
        ),
        RiskFactor(
            code="known_lessons",
            label="Recurring failure patterns recorded for these files",
            value=len(live_lessons) if lessons_known else None,
            missing=not lessons_known,
            references=[lesson.title for lesson in live_lessons[:MAX_REFERENCES]],
        ),
        RiskFactor(
            code="hotspot_areas",
            label="Areas flagged by the latest hotspot sweep",
            value=None if hot is None else len(hot),
            missing=hot is None,
            references=[
                f"{hit.area} ({hit.band}, {hit.lift:.1f}x the repository rate)" for hit in hot or []
            ],
        ),
    ]

    band: RiskBand
    if not scope:
        band, why = (
            "INSUFFICIENT_DATA",
            "No affected file could be identified from the ticket or its history.",
        )
    elif facts.activity is None:
        band, why = (
            "INSUFFICIENT_DATA",
            "No change history is recorded for the files in scope, so risk cannot be judged.",
        )
    elif (
        len(reverted) >= HIGH_REVERTED
        or len(fixes) >= HIGH_FIXES
        or (reverted and len(fixes) >= HIGH_FIXES_WITH_REVERT)
        or (hot and any(hit.band == "HOT" for hit in hot) and len(fixes) >= MODERATE_FIXES)
    ):
        band, why = "HIGH", _summary(fixes, reverted, window_days)
    elif len(fixes) >= MODERATE_FIXES or reverted or live_lessons or hot:
        band, why = "MODERATE", _summary(fixes, reverted, window_days)
    elif facts.unreadable_fixes or not lessons_known:
        # LOW would be a claim about history this run could not see in full.
        band, why = (
            "INSUFFICIENT_DATA",
            "Part of the history of these files could not be read for this project "
            "(fixes outside its access scope, or a search that failed), so low risk "
            "cannot be claimed.",
        )
    else:
        band, why = (
            "LOW",
            f"{len(fixes)} defect fix(es) and no reverts among {facts.activity} recorded "
            f"change(s) to these files in the last {window_days} days.",
        )
    return Risk(band=band, explanation=why, factors=factors)


def _summary(fixes: Sequence[FixRecord], reverted: Sequence[FixRecord], window_days: int) -> str:
    return (
        f"{len(fixes)} defect fix(es) changed these files in the last {window_days} days, "
        f"{len(reverted)} of them later reverted."
    )
