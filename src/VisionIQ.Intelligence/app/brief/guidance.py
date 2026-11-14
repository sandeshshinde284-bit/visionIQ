"""ENH-01: enhancement guidance (master design 8.5), built with the brief's own machinery.

An enhancement gets the same pipeline as a defect: retrieval, numbered evidence, the check that
removes any statement its source does not support, advice ids. What differs is the shape of the
answer: eight fixed sections instead of a flat list.

* The sections and their order are fixed here. A model can neither add nor drop one.
* An item is an ordinary advice item (advice id, statement, cited evidence with a quote) that
  names its section. It is kept only if the evidence check keeps it.
* A section with no surviving item is ``missing``. It is never filled with a guess.
* "Contracts and APIs touched" is written by code from the dependency graph (LRN-07), which
  records explicit references only. Dependents are therefore named only where a recorded
  reference exists (8.5: "only when evidence supports them").
"""

from __future__ import annotations

from collections.abc import Sequence
from dataclasses import dataclass
from typing import Any

from app.brief.schema import Advice, Evidence, GuidanceSection

# (code, title shown to the engineer, what the section is for: given to the model as its rule)
SECTIONS: tuple[tuple[str, str, str], ...] = (
    (
        "acceptance_criteria_gaps",
        "Acceptance-criteria gaps",
        "something the ticket does not specify that the evidence shows a similar change had to "
        "decide",
    ),
    (
        "contracts_and_apis",
        "Contracts and APIs touched",
        "an interface, file or project that other code depends on and this change touches",
    ),
    (
        "deployment_and_migration_risk",
        "Deployment and migration risk",
        "a rollout, data-migration or compatibility problem the evidence shows for this area",
    ),
    (
        "related_past_changes",
        "Related past changes",
        "an earlier change to the same code or behaviour, and what happened to it",
    ),
    (
        "do_and_dont",
        "Do / don't",
        "something a past change shows works (DO) or fails, for example a reverted change (DONT)",
    ),
    (
        "recommended_sequence",
        "Recommended sequence",
        "an order of work the evidence supports, for example what a past change did first",
    ),
    (
        "tests_to_add",
        "Tests to add",
        "a test the evidence shows was needed or was missing for a change like this",
    ),
    (
        "reviewer_decisions",
        "Reviewer decisions",
        "a choice the evidence leaves open and a reviewer has to make",
    ),
)
SECTION_CODES: tuple[str, ...] = tuple(code for code, _, _ in SECTIONS)
TITLES: dict[str, str] = {code: title for code, title, _ in SECTIONS}
CONTRACTS = "contracts_and_apis"

MAX_ITEMS = 16
MAX_PER_SECTION = 3
MAX_DEPENDENCY_EVIDENCE = 3
MAX_NAMED_DEPENDENTS = 8
# The routing table's reason for a second opinion on enhancement guidance (v1.11 matrix).
ESCALATE_CROSS_REPOSITORY = "cross_repo_contract"


@dataclass(frozen=True)
class Dependents:
    """Explicit references the code scan recorded: which files refer to ``target``.

    ``target`` is a file of the predicted scope, or the project file that contains it (a .NET
    project reference is recorded between project files), or, with ``cross_repository``, this
    repository as a whole. ``dependents`` are the referring files, exactly as recorded.
    """

    target: str
    dependents: tuple[str, ...]
    item_key: str
    url: str | None = None
    revision: str | None = None
    cross_repository: bool = False


def _named(names: Sequence[str]) -> str:
    shown = ", ".join(names[:MAX_NAMED_DEPENDENTS])
    more = len(names) - MAX_NAMED_DEPENDENTS
    return f"{shown} and {more} more" if more > 0 else shown


def _headline(found: Dependents) -> str:
    count = len(found.dependents)
    if found.cross_repository:
        noun = "file in another repository" if count == 1 else "files in other repositories"
        return f"{count} {noun} declare a dependency on repository {found.target}"
    noun = "other file" if count == 1 else "other files"
    return f"{found.target} is referenced by {count} {noun}"


def dependency_evidence(found: Sequence[Dependents], first_number: int) -> list[Evidence]:
    """Citable evidence (E<n>...) for recorded references, most referenced first."""
    usable = [item for item in found if item.dependents]
    usable.sort(key=lambda item: (not item.cross_repository, -len(item.dependents), item.target))
    evidence = []
    for offset, item in enumerate(usable[:MAX_DEPENDENCY_EVIDENCE]):
        evidence.append(
            Evidence(
                id=f"E{first_number + offset}",
                item_key=item.item_key,
                kind="code_dependency",
                title=_headline(item),
                url=item.url,
                revision=item.revision,
                snippet=f"Referenced by: {_named(item.dependents)}.",
                relevance="strong",
                notes=["Explicit references recorded by the code scan; runtime use is not known."],
            )
        )
    return evidence


def contract_items(evidence: Sequence[Evidence]) -> list[dict[str, Any]]:
    """The "contracts and APIs touched" items, written by code from dependency evidence.

    They are draft items like a model's and go through the same evidence check: nothing here is
    exempt from it.
    """
    items = []
    for source in evidence:
        if source.kind != "code_dependency":
            continue
        referrers = source.snippet.removeprefix("Referenced by: ").removesuffix(".")
        items.append(
            {
                "section": CONTRACTS,
                "kind": "RISK",
                "statement": (
                    f"{source.title} ({referrers}): changing what it exposes affects them."
                ),
                "support": [{"evidence_id": source.id, "quote": source.title}],
            }
        )
    return items


def in_section_order(items: Sequence[Any]) -> list[Any]:
    """Draft items in the order of the eight sections (items of one section keep their order)."""

    def position(item: Any) -> int:
        section = item.get("section") if isinstance(item, dict) else None
        return SECTION_CODES.index(section) if section in SECTION_CODES else len(SECTION_CODES)

    return sorted(items, key=position)


def sections(advice: Sequence[Advice]) -> list[GuidanceSection]:
    """All eight sections, each ``covered`` by its advice ids or ``missing``."""
    return [
        GuidanceSection(
            section=code,  # type: ignore[arg-type]
            title=TITLES[code],
            status="covered" if ids else "missing",
            advice_ids=ids,
        )
        for code in SECTION_CODES
        for ids in [[item.advice_id for item in advice if item.section == code]]
    ]
