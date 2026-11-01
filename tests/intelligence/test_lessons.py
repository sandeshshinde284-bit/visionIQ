"""LRN-10 lesson engine: created only on qualifying evidence, no duplicates, reverts dispute."""

from datetime import UTC, datetime

from app.knowledge.lessons import Observation, Pattern, Plan, consolidate

NOW = datetime(2026, 3, 1, tzinfo=UTC)
ORG = "https://dev.azure.com/contoso"


def obs(ticket, *paths, repo="repo-1", fix=None, reverted=()):
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
