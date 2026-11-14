"""Model selection by complexity for a fix run's code tasks (DEF-01 reproduction, DEF-02 fix).

The tier is decided by code, before any model is called, from what the run already knows: how many
source files are in scope, the risk band of the ticket's brief and whether the scope touches a
restricted area. A model never chooses its own tier. Without a brief or a resolved scope a run is
"standard": it is never called simple without evidence that it is.
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass

from app.llm.routing import TIERS


@dataclass(frozen=True)
class Complexity:
    tier: str
    # Fixed codes, safe to log: why the run got this tier.
    reasons: tuple[str, ...]


def assess(
    scope_files: int,
    risk_band: str | None,
    restricted_areas: Sequence[str],
    simple_max_files: int = 2,
    complex_min_files: int = 6,
) -> Complexity:
    """Rate a run simple, standard or complex."""
    reasons = [f"restricted_area_{area}" for area in sorted(set(restricted_areas))]
    if risk_band == "HIGH":
        reasons.append("risk_band_high")
    if scope_files >= complex_min_files:
        reasons.append(f"scope_files_{scope_files}")
    if reasons:
        return Complexity("complex", tuple(reasons))
    if risk_band == "LOW" and 1 <= scope_files <= simple_max_files:
        return Complexity("simple", ("risk_band_low", f"scope_files_{scope_files}"))
    band = (risk_band or "unknown").lower()
    scope = "scope_unknown" if scope_files == 0 else f"scope_files_{scope_files}"
    return Complexity("standard", (f"risk_band_{band}", scope))


def tier_for_attempt(base: str, attempt: int) -> str:
    """The tier of the ``attempt``-th try (1-based): each failed try moves one tier up."""
    index = min(TIERS.index(base) + max(0, attempt - 1), len(TIERS) - 1)
    return TIERS[index]
