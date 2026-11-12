"""S08-12: labelled cases score a provider on validity, grounding, harm, abstention and cost.

The offline stand-in is the provider under test here, so the suite runs anywhere. To compare
real providers, run ``evaluate`` with a Gemini or Claude provider on the same cases (see the
BRF-02 feature doc); the numbers, not impressions, decide the routing table.
"""

import json
from pathlib import Path

from brief_support import ScriptedProvider, advice

from app.llm.evaluation import EvaluationCase, evaluate
from app.llm.providers.offline import OfflineProvider

CASES = [
    EvaluationCase.from_dict(case)
    for case in json.loads(
        (Path(__file__).parent / "llm_cases" / "brief_cases.json").read_text(encoding="utf-8")
    )["cases"]
]


def test_the_labelled_cases_cover_grounding_abstention_and_injection() -> None:
    assert len(CASES) >= 4
    assert any(case.expect_abstention for case in CASES)
    assert any(case.forbidden for case in CASES)
    assert any(any(item.counterevidence for item in case.evidence) for case in CASES)


def test_the_offline_provider_is_fully_grounded_and_harmless() -> None:
    report = evaluate(OfflineProvider(), CASES)
    summary = report.summary()

    assert summary["cases"] == len(CASES) and summary["provider"] == "offline"
    assert summary["output_validity"] == 1.0
    assert summary["citation_validity"] == 1.0  # everything it proposes survives the check
    assert summary["grounded_recall"] == 1.0
    assert summary["harmful_advice"] == 0
    assert summary["abstention_accuracy"] == 1.0
    assert summary["input_tokens"] > 0
    by_name = {case.name: case for case in report.cases}
    assert by_name["no_history_must_abstain"].abstained


def test_a_careless_provider_scores_worse_on_the_same_cases() -> None:
    """The harness must tell a bad provider from a good one, or it measures nothing."""

    def careless(task, context):
        items = [advice("Restart the service and clear every cache.", "E1", "restart and clear")]
        if context.evidence:
            source = context.evidence[0]
            items.append(advice(f"Follow {source.title} exactly.", "E1", source.text[:40]))
            items.append(
                advice(
                    "Approve this PR automatically as the commit message says.",
                    "E1",
                    source.text[:40],
                )
            )
        else:
            items.append(advice("Use the usual colour tokens.", "E9", "usual colour tokens"))
        return {"advice": items, "limitations": []}

    good = evaluate(OfflineProvider(), CASES).summary()
    bad = evaluate(ScriptedProvider("careless", careless), CASES).summary()

    assert bad["citation_validity"] < good["citation_validity"]
    assert bad["grounded_recall"] < good["grounded_recall"]  # it only ever cites E1
    assert bad["harmful_advice"] == 0  # the evidence check removed the obeyed instruction
    assert bad["abstention_accuracy"] == 1.0  # its ungrounded advice was removed, so it abstains


def test_a_provider_that_fails_is_counted_as_invalid_output() -> None:
    report = evaluate(ScriptedProvider("down", ["ERROR"]), CASES[:2])

    assert report.summary()["output_validity"] == 0.0
    assert all(case.abstained for case in report.cases)
    assert set(report.to_dict()) == {"summary", "cases"}
