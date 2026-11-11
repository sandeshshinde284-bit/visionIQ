"""LRN-10: consolidate repeated failure evidence without inventing common root causes.

Automatic lessons need two independent signals from at least two distinct defects:
an explicit, undisputed ticket-to-fix link changing overlapping code files AND
strong, independently observed failure-symptom overlap from current redacted ADO
tickets. A shared file alone, generic titles or missing symptoms are insufficient.
All automatically created lessons remain candidates with UNVERIFIED causes.

Decisions are deterministic and conservative; this is lexical corroboration,
not a semantic/root-cause equivalence proof. Ambiguity and reviewed patterns
require a named engineer. Later reverts dispute lessons without erasing history.
"""

from __future__ import annotations

import hashlib
import re
import uuid
from collections.abc import Iterable, Sequence
from dataclasses import dataclass, field, replace
from datetime import datetime
from fnmatch import fnmatch

MIN_TICKETS = 2
OVERLAP = 0.5
SYMPTOM_OVERLAP = 2 / 3
MIN_SHARED_SYMPTOM_TERMS = 3
# LRN-13 (S12-12): a lesson gets a new version only when its file signature changes materially
# (overlap with the previous signature below this) or it becomes disputed. A ticket joining,
# recurrence counts and "last seen" are not material (master design 6.10).
MATERIAL_SIGNATURE_OVERLAP = 0.8
GENERATED_BY = "process:visioniq-lessons"
# Changes to these never characterise a failure mechanism.
NOISE = (
    "*.lock",
    "*package-lock.json",
    "*yarn.lock",
    "*.csproj",
    "*.sln",
    "*.props",
    "*.targets",
    "*.md",
    "*.txt",
    "*.gitignore",
    "*.editorconfig",
    "*requirements*.txt",
    "*pyproject.toml",
)
_NAMESPACE = uuid.UUID("6f7a3b2e-0d1c-4f5e-9a8b-7c6d5e4f3a21")

# Common report boilerplate is not independent evidence of the same failure.
# Keep domain-specific terms (barcode, retry, refund, timeout, receipt, etc.).
STOPWORDS = frozenset(
    {
        "a", "an", "and", "are", "as", "at",
        "be", "been", "before", "being", "by", "can",
        "cannot", "could", "did", "do", "does", "during",
        "error", "errors", "expected", "actual", "failed", "failing",
        "fails", "failure", "for", "from", "had", "has",
        "have", "in", "into", "is", "issue", "issues",
        "it", "its", "not", "of", "on", "or",
        "our", "should", "so", "system", "the", "their",
        "there", "these", "this", "to", "unable", "unexpected",
        "user", "users", "was", "were", "when", "where",
        "which", "while", "will", "with", "would", "work",
        "working", "works", "problem", "problems", "bug", "bugs",
        "defect", "defects", "kiosk", "application", "app", "report",
        "reported", "reproduce", "step", "steps",
    }
)
WORD_ALIASES = {
    "printing": "print",
    "printed": "print",
    "prints": "print",
    "retries": "retry",
    "retrying": "retry",
    "retried": "retry",
    "duplicated": "duplicate",
    "duplicates": "duplicate",
    "timeouts": "timeout",
    "missing": "missing",
    "receipts": "receipt",
}


def failure_terms(title: str, repro_steps: str = "") -> frozenset[str]:
    """Normalized diagnostic tokens from redacted source, not LLM-inferred claims.

    Prefer the short problem title. Only use repro steps when the title has too
    little diagnostic information; otherwise long generic steps can manufacture
    apparent similarity. Raw source text is not persisted in lesson observations.
    """
    def tokens(text: str) -> frozenset[str]:
        words = re.findall(r"[a-z][a-z0-9]{2,}", text[:800].lower())
        return frozenset(
            WORD_ALIASES.get(word, word)
            for word in words
            if word not in STOPWORDS and not word.startswith("http")
        )

    terms = tokens(title[:320])
    if len(terms) < MIN_SHARED_SYMPTOM_TERMS:
        terms |= tokens(repro_steps[:800])
    return terms


def corroborated_failure(a: Observation, b: Observation) -> bool:
    """Both changed code and distinctive problem descriptions must agree."""
    if a.scope != b.scope or jaccard(a.paths, b.paths) < OVERLAP:
        return False
    shared = a.symptoms & b.symptoms
    return (
        len(shared) >= MIN_SHARED_SYMPTOM_TERMS
        and jaccard(a.symptoms, b.symptoms) >= SYMPTOM_OVERLAP
    )



def code_paths(paths: Iterable[str]) -> frozenset[str]:
    """Changed paths that can describe a failure mechanism (noise removed)."""
    return frozenset(p for p in paths if not any(fnmatch(p.lower(), n) for n in NOISE))


def jaccard(a: frozenset[str], b: frozenset[str]) -> float:
    return len(a & b) / len(a | b) if a and b else 0.0


@dataclass(frozen=True)
class Observation:
    association_key: str
    source_instance: str
    project_id: str
    repository_id: str
    ticket_id: str
    fix_kind: str
    fix_id: str
    paths: frozenset[str]
    reverted_by: tuple[str, ...] = ()
    symptoms: frozenset[str] = frozenset()

    @property
    def key(self) -> str:
        return hashlib.sha256(self.association_key.encode()).hexdigest()

    @property
    def scope(self) -> tuple[str, str, str]:
        return (self.source_instance, self.project_id, self.repository_id)


@dataclass
class Pattern:
    pattern_id: str
    scope: tuple[str, str, str]
    latest_revision: int
    status: str
    signature: frozenset[str]
    members: set[str] = field(default_factory=set)


@dataclass(frozen=True)
class Revision:
    pattern_id: str
    revision: int
    status: str
    title: str
    signature: tuple[str, ...]
    observation_keys: tuple[str, ...]
    decision: str
    rationale: str


@dataclass(frozen=True)
class Decision:
    observation_key: str
    decision: str
    pattern_ids: tuple[str, ...]
    rationale: str


@dataclass
class Plan:
    """Everything one run decides; applied atomically by the store."""

    new_patterns: list[Pattern] = field(default_factory=list)
    revisions: list[Revision] = field(default_factory=list)
    memberships: list[tuple[str, str]] = field(default_factory=list)
    decisions: list[Decision] = field(default_factory=list)


def signature_for(observations: Sequence[Observation]) -> frozenset[str]:
    """Public entry point for lesson_store's human-initiated review/fork actions, which need the
    same signature computation consolidate() uses internally -- so a fork's resulting patterns are
    signatured identically to how an automated run would have signatured the same membership split.
    """
    return _signature(observations)


def _signature(observations: Sequence[Observation]) -> frozenset[str]:
    counts: dict[str, int] = {}
    for observation in observations:
        for path in observation.paths:
            counts[path] = counts.get(path, 0) + 1
    shared = frozenset(p for p, n in counts.items() if n >= 2)
    return shared or frozenset.intersection(*(o.paths for o in observations))


def title_for(signature: frozenset[str]) -> str:
    """Public wrapper, same reason as signature_for: lesson_store's fork action needs the identical
    title format consolidate() would have produced, so a forked pattern reads like any other lesson.
    """
    return _title(signature)


def _title(signature: frozenset[str]) -> str:
    first = sorted(signature)[0]
    more = len(signature) - 1
    return f"Repeated fixes touching {first}" + (
        f" (+{more} file{'s' if more > 1 else ''})" if more else ""
    )


def consolidate(
    observations: Sequence[Observation], patterns: Sequence[Pattern], now: datetime
) -> Plan:
    """Decide what the evidence supports. ``patterns`` is the current state (with members)."""
    del now  # timestamps are applied by the store; decisions depend on evidence only
    plan = Plan()
    # Work on copies: deciding must never change the caller's state.
    state = {p.pattern_id: replace(p, members=set(p.members)) for p in patterns}
    member_of = {key: p.pattern_id for p in patterns for key in p.members}
    # A fixed, data-derived order (never insertion order) so two runs over the same evidence make
    # identical decisions, including which pattern an ambiguous observation happens to join first.
    ordered = sorted(
        (o for o in observations if o.paths), key=lambda o: (o.scope, o.ticket_id, o.key)
    )
    by_key = {o.key: o for o in ordered}

    def matching_members(observation: Observation, pattern: Pattern) -> bool:
        """Require corroboration from two independently fixed defect tickets."""
        support = {
            by_key[key].ticket_id
            for key in pattern.members
            if key in by_key
            and by_key[key].ticket_id != observation.ticket_id
            and corroborated_failure(observation, by_key[key])
        }
        return len(support) >= MIN_TICKETS

    for observation in ordered:
        if observation.key in member_of:
            continue
        matches = sorted(
            p.pattern_id
            for p in state.values()
            if p.status in {"candidate", "reviewed"}
            and p.scope == observation.scope
            and jaccard(observation.paths, p.signature) >= OVERLAP
            and matching_members(observation, p)
        )
        if len(matches) == 1:
            pattern = state[matches[0]]
            if pattern.status == "reviewed":
                plan.decisions.append(
                    Decision(
                        observation.key,
                        "HUMAN_REVIEW",
                        (pattern.pattern_id,),
                        "A new observation must be approved before joining a reviewed lesson.",
                    )
                )
                continue
            pattern.members.add(observation.key)
            member_of[observation.key] = pattern.pattern_id
            plan.memberships.append((observation.key, pattern.pattern_id))
            plan.decisions.append(
                Decision(
                    observation.key,
                    "LINK_EXISTING",
                    (pattern.pattern_id,),
                    "Changed files and independently observed failure symptoms corroborate "
                    "this candidate lesson; the root cause remains unverified.",
                )
            )
            continue
        if len(matches) > 1:
            plan.decisions.append(
                Decision(
                    observation.key,
                    "HUMAN_REVIEW",
                    tuple(matches),
                    "Several lessons have matching code changes and symptoms; "
                    "a human must determine the correct membership.",
                )
            )
            continue
        blocked = any(
            p.status in {"disputed", "retired"}
            and p.scope == observation.scope
            and jaccard(observation.paths, p.signature) >= OVERLAP
            and matching_members(observation, p)
            for p in state.values()
        )
        if blocked:
            plan.decisions.append(
                Decision(
                    observation.key,
                    "WAIT_FOR_EVIDENCE",
                    (),
                    "Matching history belongs to a disputed or retired lesson; "
                    "automatic creation of a replacement is unsafe.",
                )
            )
            continue
        # Build a pairwise-corroborated group: a star-shaped chain of file
        # overlaps must not connect unrelated failures through a common file.
        group = [observation]
        for peer in ordered:
            if peer.key == observation.key or peer.key in member_of:
                continue
            if any(peer.ticket_id == member.ticket_id for member in group):
                continue  # two fixes of one ticket are not independent failures
            if all(corroborated_failure(peer, member) for member in group):
                group.append(peer)
        tickets = {o.ticket_id for o in group}
        if len(tickets) < MIN_TICKETS:
            plan.decisions.append(
                Decision(
                    observation.key,
                    "WAIT_FOR_EVIDENCE",
                    (),
                    "No independent defect has both overlapping changed files and "
                    "corroborating failure symptoms; missing symptoms never imply a match.",
                )
            )
            continue
        keys = tuple(sorted(o.key for o in group))
        pattern_id = str(uuid.uuid5(_NAMESPACE, "|".join(keys)))
        signature = _signature(group)
        pattern = Pattern(pattern_id, observation.scope, 1, "candidate", signature, set(keys))
        state[pattern_id] = pattern
        plan.new_patterns.append(pattern)
        rationale = (
            f"{len(tickets)} distinct defect tickets ({', '.join(sorted(tickets))}) "
            "have explicit, undisputed fixes changing overlapping files and "
            "corroborating source-recorded failure symptoms; their shared root cause "
            "is unverified until an engineer reviews it."
        )
        plan.revisions.append(
            Revision(
                pattern_id,
                1,
                "candidate",
                _title(signature),
                tuple(sorted(signature)),
                keys,
                "CREATE_NEW",
                rationale,
            )
        )
        for key in keys:
            member_of[key] = pattern_id
            plan.memberships.append((key, pattern_id))
            plan.decisions.append(Decision(key, "CREATE_NEW", (pattern_id,), rationale))

    # Material signature change of a lesson whose members changed in this run (S12-12).
    joined = {pattern_id for _, pattern_id in plan.memberships}
    created = {p.pattern_id for p in plan.new_patterns}
    for pattern_id in sorted(joined - created):
        pattern = state[pattern_id]
        members = [by_key[k] for k in pattern.members if k in by_key]
        if len(members) < MIN_TICKETS:
            continue
        signature = _signature(members)
        if jaccard(signature, pattern.signature) >= MATERIAL_SIGNATURE_OVERLAP:
            continue  # same lesson, more support: no new version
        previous = sorted(pattern.signature)
        pattern.signature = signature
        # A material evidence change requires fresh verification; never insert a
        # reviewed revision without an attributable human reviewer.
        pattern.status = "candidate"
        pattern.latest_revision += 1
        plan.revisions.append(
            Revision(
                pattern.pattern_id,
                pattern.latest_revision,
                pattern.status,
                _title(signature),
                tuple(sorted(signature)),
                tuple(sorted(pattern.members)),
                "REVISE_EXISTING",
                f"Supporting fixes now change {', '.join(sorted(signature))} "
                f"(was {', '.join(previous)}); prior verification must be reviewed again."[:2000],
            )
        )

    # A later revert of a supporting fix disputes the lesson (history kept: new revision).
    for pattern in state.values():
        reverted = sorted(
            by_key[k] for k in pattern.members if k in by_key and by_key[k].reverted_by
        )
        if not reverted or pattern.status == "disputed":
            continue
        pattern.latest_revision += 1
        pattern.status = "disputed"
        detail = "; ".join(
            f"ticket {o.ticket_id} fix {o.fix_kind} {o.fix_id} reverted by "
            f"{', '.join(o.reverted_by)}"
            for o in reverted
        )
        plan.revisions.append(
            Revision(
                pattern.pattern_id,
                pattern.latest_revision,
                "disputed",
                _title(pattern.signature),
                tuple(sorted(pattern.signature)),
                tuple(sorted(pattern.members)),
                "REVISE_EXISTING",
                f"Disputed: {detail}."[:2000],
            )
        )
    return plan
