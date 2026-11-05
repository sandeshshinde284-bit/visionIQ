"""LRN-09 unit tests: signals, fusion, snippets and the abstention policy."""

import pytest

from app.knowledge.retrieval import Result, extract_signals, fuse, judge, snippet


def test_exact_signals_come_from_paths_stack_frames_and_constants():
    text = (
        "PAYMENT_CAPTURED then barcode print fails.\n"
        "   at Shop.Payments.RetryPolicy.Execute(Int32 attempt) in "
        "/src/Payments/RetryPolicy.cs:line 42\n"
        "   at Shop.Kiosk.Print() in C:\\agent\\src\\Kiosk\\ReceiptPrinter.cs:line 7\n"
        "See Printer.cs and the KioskController."
    )

    signals = extract_signals(text)

    assert signals.files == ("Printer.cs", "ReceiptPrinter.cs", "RetryPolicy.cs")
    assert {"Execute", "PAYMENT_CAPTURED", "KioskController", "RetryPolicy"} <= set(signals.symbols)
    assert "barcode" in signals.terms and "the" not in signals.terms


def test_fusion_rewards_agreement_between_lanes_and_honours_weights():
    lanes = {"keyword": ["a", "b"], "vector": ["b", "c"], "code_path": ["c"]}
    fused = fuse(lanes, {"code_path": 1.5})

    assert fused["b"] > fused["a"]  # found by two lanes beats rank 1 in one lane
    assert fused["c"] > fused["b"]  # an exact code-path match weighs more


def test_snippets_are_verbatim_windows_around_the_match():
    body = "x " * 200 + "the barcode receipt never prints" + " y" * 200
    content = "Bug 7: Barcode print\n[Repro steps]\n" + body

    text = snippet(content, ["receipt"])

    assert "barcode receipt never prints" in text
    assert text.startswith("…") and text.endswith("…")
    assert text.strip("…").strip() in content  # never paraphrased


def _result(**lanes):
    return Result("k", "work_item_current", "t", 0.1, lanes, "s", {})


def test_a_vague_query_is_answered_with_what_to_add_instead_of_a_guess():
    status, reasons, suggestions = judge(extract_signals("it does not work"), [_result(keyword=1)])

    assert status == "insufficient_query" and reasons and suggestions


def test_results_from_a_single_fuzzy_lane_are_weak_evidence():
    signals = extract_signals("receipt printer stops after network blip")

    assert judge(signals, [_result(vector=1)])[0] == "weak_evidence"
    assert judge(signals, [_result(vector=1, keyword=3)])[0] == "answered"
    assert judge(signals, [_result(code_path=1)])[0] == "answered"


def test_without_a_published_snapshot_nothing_is_searched():
    from app.knowledge.embeddings import HashingEmbedder
    from app.knowledge.retrieval import retrieve

    class NoSnapshot:
        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

        def execute(self, *args, **kwargs):
            return self

        def fetchone(self):
            return None

    bundle = retrieve(
        NoSnapshot, HashingEmbedder(), "barcode print fails", ["scope"], live_check=lambda key: True
    )

    assert bundle.status == "weak_evidence" and bundle.results == []
    assert "No knowledge snapshot" in bundle.reasons[0]


def test_live_check_has_no_default_and_must_be_passed_explicitly():
    """Review R2-01: a caller cannot accidentally omit the live access re-check and have it
    silently skipped. Confluence restrictions and ADO project membership can both change after a
    page was indexed; without this, a forgotten live_check would serve content whose stored ACL is
    now stale."""
    import inspect

    from app.knowledge.retrieval import retrieve

    parameter = inspect.signature(retrieve).parameters["live_check"]
    assert parameter.default is inspect.Parameter.empty

    with pytest.raises(TypeError, match="live_check"):
        retrieve(lambda: None, object(), "query", ["scope"])  # type: ignore[call-arg]
