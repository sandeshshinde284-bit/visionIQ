"""LRN-10 lesson engine: created only on qualifying evidence, no duplicates, reverts dispute."""

from datetime import UTC, datetime

from app.knowledge.lessons import Observation, Pattern, Plan, consolidate, failure_terms

NOW = datetime(2026, 3, 1, tzinfo=UTC)
ORG = "https://dev.azure.com/contoso"


def obs(ticket, *paths, repo="repo-1", fix=None, reverted=(), symptom="Barcode print fails after payment retry"):
    return Observation(
        f"assoc-{ticket}-{fix or ticket}",
        ORG,
        "Payments",
        repo,
        ticket,
        "pull_request",
        fix or f"pr-{ticket}",
        frozenset(paths),
        tuple(reverted),
        failure_terms(symptom),
    )


RETRY = ("/src/Retry.cs", "/src/Payment.cs")


def state_after(plan: Plan, before=()) -> list[Pattern]:
    """The state a store would hold after applying ``plan``."""
    patterns = {p.pattern_id: p for p in before}
    for p in plan.new_patterns:
        patterns[p.pattern_id] = p
    return list(patterns.values())


def test_one_ticket_alone_never_becomes_a_lesson():
    plan = consolidate([obs("1", *RETRY)], [], NOW)

    assert not plan.new_patterns and not plan.revisions
    assert [d.decision for d in plan.decisions] == ["WAIT_FOR_EVIDENCE"]


def test_three_similar_tickets_become_one_candidate_lesson():
    plan = consolidate(
        [
            obs("1", *RETRY),
            obs("2", *RETRY),
            obs("3", "/src/Retry.cs", "/src/Payment.cs", "/src/X.cs"),
        ],
        [],
        NOW,
    )

    assert len(plan.new_patterns) == 1
    revision = plan.revisions[0]
    assert (revision.revision, revision.status, revision.decision) == (1, "candidate", "CREATE_NEW")
    assert len(revision.observation_keys) == 3
    assert revision.signature == ("/src/Payment.cs", "/src/Retry.cs")
    assert "unverified" in revision.rationale
    assert revision.title == "Repeated fixes touching /src/Payment.cs (+1 file)"


def test_a_new_observation_joins_the_existing_lesson_instead_of_a_duplicate():
    first = consolidate([obs("1", *RETRY), obs("2", *RETRY)], [], NOW)
    later = consolidate(
        [obs("1", *RETRY), obs("2", *RETRY), obs("3", *RETRY)], state_after(first), NOW
    )

    assert not later.new_patterns and not later.revisions  # joining is not a new lesson version
    assert [(d.decision, d.pattern_ids) for d in later.decisions] == [
        ("LINK_EXISTING", (first.new_patterns[0].pattern_id,))
    ]


def test_two_fixes_of_the_same_ticket_are_one_ticket():
    plan = consolidate([obs("1", *RETRY, fix="a"), obs("1", *RETRY, fix="b")], [], NOW)

    assert not plan.new_patterns


def test_different_repositories_never_merge():
    plan = consolidate([obs("1", *RETRY, repo="repo-1"), obs("2", *RETRY, repo="repo-2")], [], NOW)

    assert not plan.new_patterns


def test_noise_only_overlap_never_creates_a_lesson():
    from app.knowledge.lessons import code_paths

    a = obs("1", *code_paths(["/package-lock.json", "/src/A.cs"]))
    b = obs("2", *code_paths(["/package-lock.json", "/src/B.cs"]))
    assert consolidate([a, b], [], NOW).new_patterns == []


def test_an_observation_matching_two_lessons_goes_to_human_review():
    first = consolidate(
        [obs("1", "/a.cs"), obs("2", "/a.cs"), obs("3", "/b.cs"), obs("4", "/b.cs")], [], NOW
    )
    assert len(first.new_patterns) == 2

    later = consolidate([obs("5", "/a.cs", "/b.cs")], state_after(first), NOW)

    assert [d.decision for d in later.decisions] == ["HUMAN_REVIEW"]
    assert not later.memberships


def test_a_later_revert_disputes_the_lesson_and_keeps_history():
    first = consolidate([obs("1", *RETRY), obs("2", *RETRY), obs("3", *RETRY)], [], NOW)
    reverted = [obs("1", *RETRY), obs("2", *RETRY, reverted=["deadbeef"]), obs("3", *RETRY)]

    plan = consolidate(reverted, state_after(first), NOW)

    (revision,) = plan.revisions
    assert (revision.revision, revision.status, revision.decision) == (
        2,
        "disputed",
        "REVISE_EXISTING",
    )
    assert "ticket 2" in revision.rationale and "deadbeef" in revision.rationale
    assert consolidate(reverted, state_after(first), NOW).revisions[0].revision == 2  # not 3
    already_disputed = state_after(first)
    already_disputed[0].status = "disputed"
    assert consolidate(reverted, already_disputed, NOW).revisions == []  # idempotent


def test_deciding_never_changes_the_callers_state():
    first = consolidate([obs("1", *RETRY), obs("2", *RETRY)], [], NOW)
    current = state_after(first)
    snapshot = [(p.status, p.latest_revision, set(p.members)) for p in current]

    consolidate(
        [obs("1", *RETRY, reverted=["x"]), obs("2", *RETRY), obs("3", *RETRY)], current, NOW
    )

    assert [(p.status, p.latest_revision, set(p.members)) for p in current] == snapshot


def test_reruns_and_reordered_input_are_stable():
    items = [obs("1", *RETRY), obs("2", *RETRY), obs("3", *RETRY)]
    first = consolidate(items, [], NOW)

    assert (
        consolidate(list(reversed(items)), [], NOW).new_patterns[0].pattern_id
        == first.new_patterns[0].pattern_id
    )
    rerun = consolidate(items, state_after(first), NOW)
    assert not rerun.new_patterns and not rerun.revisions and not rerun.decisions


def test_more_support_for_the_same_files_is_not_a_new_lesson_version():
    first = consolidate([obs("1", "/a.cs", "/b.cs"), obs("2", "/a.cs", "/b.cs")], [], NOW)
    # A third ticket touching the same files plus one of its own: same lesson, more support.
    later = consolidate(
        [
            obs("1", "/a.cs", "/b.cs"),
            obs("2", "/a.cs", "/b.cs"),
            obs("3", "/a.cs", "/b.cs", "/c.cs"),
        ],
        state_after(first),
        NOW,
    )

    assert [d.decision for d in later.decisions] == ["LINK_EXISTING"]
    assert later.revisions == []


def test_a_materially_changed_signature_is_a_new_lesson_version_with_history():
    first = consolidate([obs("1", "/a.cs", "/b.cs"), obs("2", "/a.cs", "/b.cs")], [], NOW)
    wider = ("/a.cs", "/b.cs", "/c.cs", "/d.cs")
    current = state_after(first)
    step = consolidate(
        [obs("1", "/a.cs", "/b.cs"), obs("2", "/a.cs", "/b.cs"), obs("3", *wider)], current, NOW
    )
    current[0].members |= {k for k, _ in step.memberships}
    later = consolidate(
        [
            obs("1", "/a.cs", "/b.cs"),
            obs("2", "/a.cs", "/b.cs"),
            obs("3", *wider),
            obs("4", *wider),
        ],
        current,
        NOW,
    )

    (revision,) = later.revisions
    assert (revision.revision, revision.decision, revision.status) == (
        2,
        "REVISE_EXISTING",
        "candidate",
    )
    assert revision.signature == wider and "was /a.cs, /b.cs" in revision.rationale


def test_shared_changed_file_does_not_merge_unrelated_defects():
    same_file = "/src/PaymentService.cs"
    plan = consolidate(
        [
            obs("1", same_file, symptom="Barcode print fails after payment retry"),
            obs("2", same_file, symptom="Card reader timeout causes contactless authorization error"),
            obs("3", same_file, symptom="Refund balance calculation displays incorrect total"),
        ],
        [],
        NOW,
    )
    assert plan.new_patterns == []
    assert all(d.decision == "WAIT_FOR_EVIDENCE" for d in plan.decisions)


def test_generic_or_missing_symptoms_never_qualify_a_lesson():
    files = ("/src/PaymentService.cs",)
    plan = consolidate(
        [
            obs("1", *files, symptom="A problem happened in the kiosk"),
            obs("2", *files, symptom="Unexpected issue in the application"),
            obs("3", *files, symptom=""),
        ],
        [],
        NOW,
    )
    assert plan.new_patterns == []


def test_similar_symptom_text_with_different_wording_can_qualify():
    plan = consolidate(
        [
            obs("1", "/src/Retry.cs", symptom="Barcode printing fails after payment retry"),
            obs("2", "/src/Retry.cs", symptom="Barcode print fails after payment retry"),
            obs("3", "/src/Retry.cs", symptom="Payment retry causes barcode print failure"),
        ],
        [],
        NOW,
    )
    assert len(plan.new_patterns) == 1
    assert len(plan.revisions[0].observation_keys) == 3
    assert "unverified" in plan.revisions[0].rationale


def test_new_unrelated_observation_is_not_joined_just_for_shared_file():
    first = consolidate([obs("1", "/same.cs"), obs("2", "/same.cs")], [], NOW)
    unrelated = obs(
        "3", "/same.cs",
        symptom="Card reader timeout during contactless authorization",
    )
    later = consolidate(
        [obs("1", "/same.cs"), obs("2", "/same.cs"), unrelated],
        state_after(first),
        NOW,
    )
    assert not later.memberships
    assert [d.decision for d in later.decisions] == ["WAIT_FOR_EVIDENCE"]


def test_new_member_to_reviewed_lesson_requires_human_review():
    initial = consolidate([obs("1", "/a.cs"), obs("2", "/a.cs")], [], NOW)
    current = state_after(initial)
    current[0].status = "reviewed"
    result = consolidate(
        [obs("1", "/a.cs"), obs("2", "/a.cs"), obs("3", "/a.cs")],
        current,
        NOW,
    )
    assert not result.memberships
    assert [(d.decision, d.pattern_ids) for d in result.decisions] == [
        ("HUMAN_REVIEW", (current[0].pattern_id,))
    ]


def test_matching_disputed_pattern_never_creates_replacement_or_new_membership():
    initial = consolidate([obs("1", "/a.cs"), obs("2", "/a.cs")], [], NOW)
    current = state_after(initial)
    current[0].status = "disputed"
    result = consolidate(
        [obs("1", "/a.cs"), obs("2", "/a.cs"), obs("3", "/a.cs")],
        current,
        NOW,
    )
    assert not result.new_patterns and not result.memberships
    assert [d.decision for d in result.decisions] == ["WAIT_FOR_EVIDENCE"]


def test_two_fixes_from_one_ticket_do_not_supply_independent_support():
    plan = consolidate(
        [
            obs("1", "/a.cs", fix="pr-a"),
            obs("1", "/a.cs", fix="pr-b"),
            obs("2", "/a.cs", fix="pr-c"),
        ],
        [],
        NOW,
    )
    assert len(plan.new_patterns) == 1
    assert len(plan.revisions[0].observation_keys) == 2
