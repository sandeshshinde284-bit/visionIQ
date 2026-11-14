"""ENH-01: enhancement guidance in the eight sections of master design 8.5.

The card's acceptance criteria, each with a test here:
* the guidance covers the eight sections;
* a section with no evidence is marked missing, never filled with guesses;
* each item cites its source and has an advice id, like the defect brief.
"""

import dataclasses
import json
from dataclasses import dataclass, field
from typing import get_args

from brief_support import (
    FakeSources,
    ScriptedProvider,
    advice,
    gateway_for,
    key,
    offline_gateway,
    request,
)
from jsonschema import Draft202012Validator

from app.brief import guidance, schema
from app.brief.guidance import SECTION_CODES, Dependents
from app.brief.history import file_dependents, repository_dependents
from app.brief.pipeline import BriefSettings, build_brief
from app.llm import prompts
from app.llm.types import TaskKind

PROJECT = "src/Payments/Payments.csproj"
REFERENCES = [
    Dependents(
        target=PROJECT,
        dependents=("src/Kiosk/Kiosk.csproj", "tests/Payments.Tests/Payments.Tests.csproj"),
        item_key=key("scan"),
        url="https://dev.azure.com/acme/Payments/_git/kiosk",
        revision="0123abc",
    )
]
OTHER_REPOSITORY = Dependents(
    target="kiosk-repo",
    dependents=("recon-service: deps/kiosk.gitmodules",),
    item_key=key("scan2"),
    cross_repository=True,
)


def item(section: str, statement: str, evidence_id: str = "E1", quote: str = "", kind="DO") -> dict:
    quote = quote or "no barcode on the receipt"
    return {**advice(statement, evidence_id, quote, kind), "section": section}


PAST_CHANGE = item(
    "related_past_changes",
    "Bug 101 made the retry policy idempotent after the barcode was not printed.",
    quote="Fixed by making the retry policy idempotent",
    kind="APPROACH",
)
DONT = item(
    "do_and_dont",
    "Do not reuse the retry counter fix: it was later reverted.",
    "E2",
    "The retry counter fix was merged",
    "DONT",
)
TEST = item(
    "tests_to_add",
    "Add a test that a retry of a captured payment still prints the barcode on the receipt.",
)
INVENTED = item(
    "deployment_and_migration_risk",
    "Roll the change out behind a feature flag on the staging cluster first.",
    quote="staged rollout behind a feature flag",
    kind="RISK",
)
DRAFT = {"advice": [TEST, DONT, PAST_CHANGE], "limitations": []}


@dataclass
class GuidanceSources(FakeSources):
    references: list[Dependents] = field(default_factory=lambda: list(REFERENCES))
    asked: list[tuple[str, tuple[str, ...]]] = field(default_factory=list)

    def dependents(self, repository_id: str, paths) -> list[Dependents]:
        self.asked.append((repository_id, tuple(paths)))
        return self.references


def enhancement():
    return dataclasses.replace(request(), ticket_kind="enhancement", repository_id="kiosk-repo")


def build(draft=DRAFT, sources=None, gateway=None, the_request=None):
    sources = sources or GuidanceSources()
    model = ScriptedProvider("gemini", [draft])
    outcome = build_brief(
        the_request or enhancement(), sources, gateway or gateway_for(model), BriefSettings()
    )
    return outcome.brief, model, sources


def by_section(brief) -> dict[str, list]:
    return {
        code: [a for a in brief.advice if a.section == code]
        for code in SECTION_CODES
        if any(a.section == code for a in brief.advice)
    }


# ---- The acceptance criteria -------------------------------------------------------------------


def test_an_enhancement_gets_guidance_in_the_eight_sections_in_order() -> None:
    brief, model, _ = build()

    assert (brief.kind, brief.state) == ("enhancement", "PUBLISHED")
    assert [section.section for section in brief.guidance] == list(SECTION_CODES)
    assert len(brief.guidance) == 8
    covered = [s.section for s in brief.guidance if s.status == "covered"]
    assert covered == ["contracts_and_apis", "related_past_changes", "do_and_dont", "tests_to_add"]
    # The model was asked for guidance, with the guidance prompt: never the defect brief's.
    assert [task.kind for task in model.tasks] == [TaskKind.ENHANCEMENT_APPROACH]
    assert brief.generation.prompt_template_version == "brief.enhancement@v1"


def test_each_item_cites_its_source_and_has_an_advice_id_like_the_defect_brief() -> None:
    brief, _, _ = build()

    # Ids run in the order the sections are shown, whatever order the model answered in.
    assert brief.advice_ids() == ["ADV-001", "ADV-002", "ADV-003", "ADV-004"]
    assert [a.section for a in brief.advice] == [
        "contracts_and_apis",
        "related_past_changes",
        "do_and_dont",
        "tests_to_add",
    ]
    known = set(brief.evidence_ids())
    for entry in brief.advice:
        assert entry.evidence_ids and set(entry.evidence_ids) <= known
        assert all(support.quote for support in entry.support)
    for section in brief.guidance:
        assert section.advice_ids == [
            a.advice_id for a in brief.advice if a.section == section.section
        ]


def test_a_section_with_no_evidence_is_marked_missing_never_filled_with_a_guess() -> None:
    brief, _, _ = build({"advice": [INVENTED, PAST_CHANGE], "limitations": []})

    risk = next(s for s in brief.guidance if s.section == "deployment_and_migration_risk")
    assert (risk.status, risk.advice_ids) == ("missing", [])
    assert all("feature flag" not in entry.statement for entry in brief.advice)
    assert any("removed because their sources did not support them" in n for n in brief.limitations)
    missing = [s.section for s in brief.guidance if s.status == "missing"]
    assert missing == [
        "acceptance_criteria_gaps",
        "deployment_and_migration_risk",
        "do_and_dont",
        "recommended_sequence",
        "tests_to_add",
        "reviewer_decisions",
    ]


def test_the_stored_guidance_matches_the_shared_brief_contract() -> None:
    brief, _, _ = build()
    document = json.loads(brief.model_dump_json())

    Draft202012Validator(json.loads(schema.find_schema_file().read_text("utf-8"))).validate(
        document
    )
    assert document["kind"] == "enhancement" and len(document["guidance"]) == 8


# ---- What a model may not do -------------------------------------------------------------------


def test_an_answer_with_a_section_that_does_not_exist_is_rejected_as_a_whole() -> None:
    made_up = {**PAST_CHANGE, "section": "performance"}

    brief, _, _ = build({"advice": [made_up, DONT], "limitations": []})

    # The gateway checks the answer against the output rules before any item is looked at: an
    # answer that breaks them is not used at all. What code can state itself is still given.
    assert [section.section for section in brief.guidance] == list(SECTION_CODES)
    assert set(by_section(brief)) == {"contracts_and_apis"}
    assert brief.state == "PARTIAL" and brief.reason_codes


def test_the_evidence_check_also_drops_an_item_without_a_known_section() -> None:
    from app.brief.validate import MALFORMED, EvidenceText, validate

    source = {
        "E1": EvidenceText(
            "Bug 101",
            "no barcode on the receipt. Fixed by making the retry policy idempotent",
            None,
        )
    }
    unplaced = {k: v for k, v in TEST.items() if k != "section"}
    made_up = {**TEST, "section": "performance"}

    checked = validate({"advice": [made_up, unplaced, TEST]}, source, SECTION_CODES)

    assert [entry.section for entry in checked.advice] == ["tests_to_add"]
    assert checked.removed[MALFORMED] == 2
    # A defect brief has no sections, and its items never get one.
    plain = validate({"advice": [unplaced]}, source)
    assert [entry.section for entry in plain.advice] == [None]


def test_at_most_three_items_are_kept_for_a_section() -> None:
    many = [
        item(
            "tests_to_add",
            f"Add test {number} that a captured payment retry prints the barcode on the receipt.",
        )
        for number in range(1, 6)
    ]

    brief, _, _ = build({"advice": many, "limitations": []})

    assert len(by_section(brief)["tests_to_add"]) == guidance.MAX_PER_SECTION == 3


# ---- Contracts and APIs touched: from the dependency graph (PROP-02) ---------------------------


def test_contracts_are_written_by_code_from_recorded_references() -> None:
    seen = []

    def answer(task, context):
        seen.append(context)
        return {"advice": [], "limitations": []}  # the model says nothing about contracts

    sources = GuidanceSources()
    brief = build_brief(
        enhancement(), sources, gateway_for(ScriptedProvider("gemini", answer)), BriefSettings()
    ).brief

    # The files of the predicted scope were looked up for the ticket's repository.
    assert sources.asked == [
        ("kiosk-repo", ("/src/Payments/RetryPolicy.cs", "/src/Kiosk/ReceiptPrinter.cs"))
    ]
    reference = brief.evidence[-1]
    assert (reference.id, reference.kind, reference.relevance) == (
        "E3",
        "code_dependency",
        "strong",
    )
    assert reference.title == f"{PROJECT} is referenced by 2 other files"
    assert reference.url == "https://dev.azure.com/acme/Payments/_git/kiosk"
    (contract,) = by_section(brief)["contracts_and_apis"]
    assert contract.evidence_ids == ["E3"] and contract.kind == "RISK"
    assert "src/Kiosk/Kiosk.csproj" in contract.statement
    # The model saw the same references as numbered evidence.
    assert (
        seen[0].evidence[-1].evidence_id == "E3" and seen[0].evidence[-1].kind == "code_dependency"
    )


def test_without_a_recorded_reference_no_dependent_is_named_and_the_guidance_says_so() -> None:
    brief, _, _ = build(sources=GuidanceSources(references=[]))

    contracts = next(s for s in brief.guidance if s.section == "contracts_and_apis")
    assert contracts.status == "missing"
    assert all(e.kind != "code_dependency" for e in brief.evidence)
    assert any("not proof that nothing depends on them" in note for note in brief.limitations)


def test_a_model_may_not_name_a_dependent_the_evidence_does_not() -> None:
    claim = item(
        "contracts_and_apis",
        "The mobile wallet team's client depends on this response and must be told.",
        quote="the mobile wallet client depends on it",
        kind="RISK",
    )

    brief, _, _ = build({"advice": [claim], "limitations": []})

    assert all("mobile wallet" not in entry.statement for entry in brief.advice)
    assert [a.evidence_ids for a in by_section(brief)["contracts_and_apis"]] == [["E3"]]


def test_dependents_in_other_repositories_get_a_second_opinion() -> None:
    def review(task, context):
        assert task.kind is TaskKind.EVIDENCE_CRITIQUE
        return {
            "verdicts": [
                {"index": index, "verdict": "supported", "reason": "The source says so."}
                for index in range(len(context.draft))
            ]
        }

    drafter, reviewer = ScriptedProvider("gemini", [DRAFT]), ScriptedProvider("claude", review)
    sources = GuidanceSources(references=[*REFERENCES, OTHER_REPOSITORY])

    brief = build_brief(
        enhancement(), sources, gateway_for(drafter, reviewer), BriefSettings()
    ).brief

    assert brief.generation.reviewed_by == "claude" and len(reviewer.tasks) == 1
    cross = next(e for e in brief.evidence if "another repository" in e.title)
    assert (
        cross.title == "1 file in another repository declare a dependency on repository kiosk-repo"
    )

    # Without one, the routing table asks for no second opinion.
    quiet = ScriptedProvider("claude", review)
    build_brief(
        enhancement(),
        GuidanceSources(),
        gateway_for(ScriptedProvider("gemini", [DRAFT]), quiet),
        BriefSettings(),
    )
    assert quiet.tasks == []


# ---- Falling short -----------------------------------------------------------------------------


def test_without_a_model_the_recorded_references_are_still_given_as_partial() -> None:
    brief = build_brief(
        enhancement(),
        GuidanceSources(),
        gateway_for(ScriptedProvider("gemini", ["ERROR"])),
        BriefSettings(),
    ).brief

    assert brief.state == "PARTIAL" and brief.reason_codes
    assert set(by_section(brief)) == {"contracts_and_apis"}
    assert [s.status for s in brief.guidance].count("missing") == 7


def test_offline_mode_gives_guidance_from_the_history_alone() -> None:
    brief = build_brief(enhancement(), GuidanceSources(), offline_gateway(), BriefSettings()).brief

    assert brief.kind == "enhancement"
    assert set(by_section(brief)) == {"contracts_and_apis", "related_past_changes", "do_and_dont"}
    assert by_section(brief)["do_and_dont"][0].kind == "DONT"  # the reverted change


def test_an_unreadable_enhancement_is_blocked_with_all_eight_sections_missing() -> None:
    brief, model, sources = build(sources=GuidanceSources(ticket=None))

    assert (brief.kind, brief.state) == ("enhancement", "BLOCKED")
    assert [s.status for s in brief.guidance] == ["missing"] * 8
    assert model.tasks == [] and sources.asked == []


def test_a_defect_still_gets_a_brief_and_the_graph_is_not_read() -> None:
    # FakeSources has no dependents(): for a defect it is never called.
    model = ScriptedProvider(
        "gemini", [{"advice": [advice("Make the payment retry idempotent.")], "limitations": []}]
    )

    brief = build_brief(request(), FakeSources(), gateway_for(model), BriefSettings()).brief

    assert (brief.kind, brief.guidance) == ("defect", [])
    assert [task.kind for task in model.tasks] == [TaskKind.BRIEF_DRAFT]
    assert all(entry.section is None for entry in brief.advice)


# ---- The sections are one list, in four places ---------------------------------------------------


def test_the_eight_sections_are_the_same_in_the_model_the_prompt_and_the_contract() -> None:
    template = prompts.TEMPLATES[TaskKind.ENHANCEMENT_APPROACH]
    allowed = template.output_schema["properties"]["advice"]["items"]["properties"]["section"]

    assert len(SECTION_CODES) == 8
    assert SECTION_CODES == prompts.GUIDANCE_SECTIONS == get_args(schema.GuidanceSectionCode)
    assert tuple(allowed["enum"]) == SECTION_CODES
    assert all(f"- {code}:" in template.system for code in SECTION_CODES)
    assert "section" in template.output_schema["properties"]["advice"]["items"]["required"]


# ---- Which files are looked up -------------------------------------------------------------------


def test_a_file_the_ticket_only_names_is_looked_up_when_history_knows_exactly_one() -> None:
    known = [
        "/src/Payments/RetryPolicy.cs",
        "/src/Kiosk/ReceiptPrinter.cs",
        "/a/Util.cs",
        "/b/Util.cs",
    ]

    paths = guidance.lookup_paths(
        ["RetryPolicy.cs", "/src/Kiosk/ReceiptPrinter.cs", "Util.cs", "Unknown.cs"], known
    )

    # RetryPolicy.cs is resolved; Util.cs is ambiguous and Unknown.cs is not recorded: both are
    # left out, because the dependents of the wrong project would be a guess.
    assert paths == ["/src/Payments/RetryPolicy.cs", "/src/Kiosk/ReceiptPrinter.cs"]
    assert guidance.lookup_paths(["RetryPolicy.cs"], []) == []


# ---- Recorded references, from rows --------------------------------------------------------------


def row(path, target, dependent, repository="kiosk-repo"):
    return (path, target, dependent, repository, key("scan"), "https://example.invalid/r", "abc")


def test_a_source_file_is_matched_through_the_innermost_project_that_contains_it() -> None:
    rows = [
        row("src/Payments/Api/Response.cs", "src/All.csproj", "tools/Build.csproj"),
        row("src/Payments/Api/Response.cs", PROJECT, "src/Kiosk/Kiosk.csproj"),
        row("src/Payments/Api/Response.cs", PROJECT, "src/Recon/Recon.csproj"),
    ]

    (found,) = file_dependents(rows, "kiosk-repo")

    assert found.target == PROJECT  # not the outer project
    assert found.dependents == ("src/Kiosk/Kiosk.csproj", "src/Recon/Recon.csproj")


def test_a_direct_reference_wins_and_own_repository_rows_are_preferred() -> None:
    rows = [
        row("app/retry.py", "app/retry.py", "app/print.py"),
        row("app/retry.py", "app/app.pyproj", "tests/tests.pyproj"),
        row("app/retry.py", "app/retry.py", "elsewhere/copy.py", repository="another-repo"),
    ]

    (found,) = file_dependents(rows, "kiosk-repo")

    assert (found.target, found.dependents) == ("app/retry.py", ("app/print.py",))
    # With no row of the ticket's repository, what there is is used.
    (fallback,) = file_dependents(rows[2:], "kiosk-repo")
    assert fallback.dependents == ("elsewhere/copy.py",)
    assert file_dependents([], "kiosk-repo") == []


def test_other_repositories_are_one_record_naming_where_the_dependency_is_declared() -> None:
    rows = [
        ("recon-service", "deps/kiosk.gitmodules", key("s1"), None, "r1"),
        ("print-service", "requirements.txt", key("s2"), None, "r2"),
    ]

    (found,) = repository_dependents(rows, "kiosk-repo")

    assert found.cross_repository and found.target == "kiosk-repo"
    assert found.dependents == (
        "recon-service: deps/kiosk.gitmodules",
        "print-service: requirements.txt",
    )
    assert repository_dependents([], "kiosk-repo") == []


def test_only_the_most_referenced_targets_become_evidence_and_long_lists_are_cut() -> None:
    many = Dependents("a.csproj", tuple(f"p{n}.csproj" for n in range(12)), key("s"))
    few = [Dependents(f"t{n}.csproj", ("x.csproj",), key("s")) for n in range(5)]

    evidence = guidance.dependency_evidence(
        [*few, many, Dependents("empty.csproj", (), key("s"))], 4
    )

    assert [e.id for e in evidence] == ["E4", "E5", "E6"]  # numbered after the history's evidence
    assert evidence[0].title == "a.csproj is referenced by 12 other files"
    assert evidence[0].snippet.endswith("p7.csproj and 4 more.")
    assert all("empty.csproj" not in e.title for e in evidence)
