"""S11-11: performance of the touched path, baseline against candidate (master design 8.7).

The same workload runs on both trees in alternating pairs, after warm-up pairs that are thrown
away. The medians are compared and the spread of the samples is the noise. A noisy measurement
is INCONCLUSIVE; a slowdown counts only when it is larger than both the owner's threshold and
the noise. The best result this can give is "no regression demonstrated under these conditions",
never "cannot regress".
"""

from __future__ import annotations

import statistics
from collections.abc import Callable, Sequence

from app.defect.schema import Performance

MIN_PAIRS = 3


def _spread_percent(samples: Sequence[float]) -> float:
    """Interquartile range as a percentage of the median."""
    median = statistics.median(samples)
    if median <= 0:
        return 0.0
    quartiles = statistics.quantiles(samples, n=4, method="inclusive")
    return 100.0 * (quartiles[2] - quartiles[0]) / median


def compare(
    baseline: Sequence[float],
    candidate: Sequence[float],
    threshold_percent: float,
    noise_limit_percent: float,
) -> Performance:
    """Judge paired samples of a lower-is-better metric."""
    pairs = min(len(baseline), len(candidate))
    measured = {
        "pairs": pairs,
        "baseline_samples": list(baseline),
        "candidate_samples": list(candidate),
        "threshold_percent": threshold_percent,
        "noise_limit_percent": noise_limit_percent,
    }
    if pairs < MIN_PAIRS or len(baseline) != len(candidate):
        return Performance(
            status="INCONCLUSIVE",
            detail=f"Only {pairs} complete pair(s) were measured; at least {MIN_PAIRS} are needed.",
            **measured,
        )
    base_median = statistics.median(baseline)
    candidate_median = statistics.median(candidate)
    if base_median <= 0:
        return Performance(
            status="INCONCLUSIVE",
            detail="The baseline measured zero, so no change can be computed.",
            **measured,
        )
    change = 100.0 * (candidate_median - base_median) / base_median
    noise = max(_spread_percent(baseline), _spread_percent(candidate))
    measured.update(
        baseline_median=round(base_median, 6),
        candidate_median=round(candidate_median, 6),
        change_percent=round(change, 2),
        noise_percent=round(noise, 2),
    )
    if noise > noise_limit_percent:
        return Performance(
            status="INCONCLUSIVE",
            detail=f"The samples vary by {noise:.1f}%, more than the {noise_limit_percent:g}% "
            "limit, so the comparison says nothing.",
            **measured,
        )
    if change > threshold_percent:
        if change > noise:
            return Performance(
                status="REGRESSION",
                detail=f"The candidate is {change:.1f}% slower (threshold {threshold_percent:g}%, "
                f"noise {noise:.1f}%).",
                **measured,
            )
        return Performance(
            status="INCONCLUSIVE",
            detail=f"The candidate measured {change:.1f}% slower, which is within the noise of "
            f"{noise:.1f}%.",
            **measured,
        )
    return Performance(
        status="NO_REGRESSION_DEMONSTRATED",
        detail=f"Median change {change:+.1f}% over {pairs} pairs (threshold "
        f"{threshold_percent:g}%, noise {noise:.1f}%). Measured under these conditions only.",
        **measured,
    )


def measure_pairs(
    measure: Callable[[str], float | None], pairs: int, warmup_pairs: int
) -> tuple[list[float], list[float]]:
    """Alternate baseline and candidate runs so drift hits both; drop the warm-up pairs.

    ``measure("baseline" | "candidate")`` returns one sample, or None when the run failed. A
    pair with a failed run is dropped whole, so the two lists always stay paired.
    """
    baseline: list[float] = []
    candidate: list[float] = []
    for index in range(warmup_pairs + pairs):
        # Swap the order every pair: whichever runs second must not always be the same tree.
        order = ("baseline", "candidate") if index % 2 == 0 else ("candidate", "baseline")
        samples = {tree: measure(tree) for tree in order}
        if index < warmup_pairs or None in samples.values():
            continue
        baseline.append(samples["baseline"])  # type: ignore[arg-type]
        candidate.append(samples["candidate"])  # type: ignore[arg-type]
    return baseline, candidate
