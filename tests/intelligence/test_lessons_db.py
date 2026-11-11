"""LRN-10 end to end on PostgreSQL: the card's completion evidence through the real queries.

Three similar defect tickets become one lesson; a revert marks it disputed while keeping
history; features, disputed links and deleted tickets never support a lesson.
"""

import hashlib
import json
import os
import uuid

import pytest

psycopg = pytest.importorskip("psycopg")
from psycopg.conninfo import make_conninfo  # noqa: E402

from app.knowledge import lesson_store  # noqa: E402
from app.knowledge.lessons_cli import run  # noqa: E402
from datetime import UTC, datetime  # noqa: E402

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)
FILES = ["/src/Retry.cs", "/src/Payment.cs"]


def _conninfo(prefix: str) -> str:
    return make_conninfo(
        "",
        host=os.environ.get("DB_HOST", "127.0.0.1"),
        port=int(os.environ.get("DB_PORT", "5432")),
        dbname=os.environ.get("DB_NAME", "visioniq"),
        user=os.environ[f"DB_{prefix}_USER"],
        password=os.environ[f"DB_{prefix}_PASSWORD"],
    )


def _sha(text: str) -> str:
    return hashlib.sha256(text.encode()).hexdigest()


class Evidence:
    """Writes source evidence as the ingestion (API) role would."""

    def __init__(self) -> None:
        self.org = f"https://dev.azure.com/t{uuid.uuid4().hex[:10]}"

    def defect(self, ticket, kind="Bug", files=FILES, review=None, deleted=False, reverts=(), title="Barcode print fails after payment retry"):
        item = _sha(self.org + ticket)
        with psycopg.connect(_conninfo("API")) as conn:
            conn.execute(
                "INSERT INTO evidence.source_items (item_key, provider, source_instance, "
                "project_id, kind, external_id, latest_revision_id, latest_source_updated_at, "
                "first_observed_at, last_observed_at, deleted_at, deletion_reason) VALUES "
                "(%s, 'azure_devops', %s, 'Payments', 'work_item_current', %s, '1', now(), "
                "now(), now(), %s, %s)",
                (
                    item,
                    self.org,
                    ticket,
                    "2026-01-01T00:00:00Z" if deleted else None,
                    "deleted_in_source" if deleted else None,
                ),
            )
            conn.execute(
                "INSERT INTO evidence.source_revisions (item_key, provider_revision_id, "
                "source_updated_at, observed_at, content_sha256, source_url, "
                "retrieved_under_principal, redacted_payload, redaction_version, "
                "normalizer_version, raw_snapshot_uri, archive_status, acl_verified, "
                "acl_principals) VALUES (%s, '1', now(), now(), repeat('0', 64), 'https://x', "
                "'t', %s, 'v', 'v', 'gs://t/x', 'ARCHIVED', true, '[\"p\"]')",
                (item, json.dumps({"kind": "work_item_current", "workItemType": kind, "title": title})),
            )
            conn.execute(
                "INSERT INTO evidence.ticket_fix_links (association_key, source_instance, "
                "project_id, ticket_id, repository_id, pull_request_id, source_classification, "
                "review_status, revert_commit_shas) VALUES (%s, %s, 'Payments', %s, 'repo-1', "
                "%s, 'explicit', %s, %s)",
                (
                    _sha(self.org + ticket + "fix"),
                    self.org,
                    ticket,
                    f"pr{ticket}",
                    review,
                    json.dumps(list(reverts)),
                ),
            )
            conn.execute(
                "INSERT INTO evidence.fix_change_sets (source_instance, project_id, "
                "repository_id, fix_kind, fix_id, paths, provenance_item_key, observed_at) "
                "VALUES (%s, 'Payments', 'repo-1', 'pull_request', %s, %s, %s, now())",
                (self.org, f"pr{ticket}", files, "a" * 64),
            )

    def revert(self, ticket, sha):
        with psycopg.connect(_conninfo("API")) as conn:
            conn.execute(
                "UPDATE evidence.ticket_fix_links SET revert_commit_shas = %s "
                "WHERE association_key = %s",
                (json.dumps([sha]), _sha(self.org + ticket + "fix")),
            )

    def lessons(self):
        with psycopg.connect(_conninfo("API")) as conn:  # the API role can read lessons
            return conn.execute(
                "SELECT r.revision, r.status, r.cause_status, r.decision, "
                "(SELECT count(*) FROM knowledge.lesson_memberships m "
                " WHERE m.pattern_id = p.pattern_id) "
                "FROM knowledge.lesson_patterns p JOIN knowledge.lesson_revisions r "
                "USING (pattern_id) WHERE p.source_instance = %s ORDER BY r.revision",
                (self.org,),
            ).fetchall()

    def pattern_id(self) -> str:
        with psycopg.connect(_conninfo("API")) as conn:
            row = conn.execute(
                "SELECT pattern_id::text FROM knowledge.lesson_patterns WHERE source_instance = %s",
                (self.org,),
            ).fetchone()
            assert row is not None, "no pattern exists yet for this test's evidence"
            return row[0]

    def observation_keys(self, pattern_id: str) -> list[str]:
        with psycopg.connect(_conninfo("API")) as conn:
            return [
                row[0]
                for row in conn.execute(
                    "SELECT observation_key::text FROM knowledge.lesson_memberships "
                    "WHERE pattern_id = %s",
                    (pattern_id,),
                ).fetchall()
            ]


def test_three_similar_defects_make_one_lesson_and_a_revert_disputes_it() -> None:
    evidence = Evidence()
    for ticket in ("101", "102", "103"):
        evidence.defect(ticket)

    run(_conninfo("INTELLIGENCE"))
    assert evidence.lessons() == [(1, "candidate", "unverified", "CREATE_NEW", 3)]

    evidence.revert("102", "deadbeef")
    run(_conninfo("INTELLIGENCE"))
    assert evidence.lessons() == [
        (1, "candidate", "unverified", "CREATE_NEW", 3),  # history kept
        (2, "disputed", "unverified", "REVISE_EXISTING", 3),
    ]

    second = run(_conninfo("INTELLIGENCE"))  # nothing new: no duplicate lesson or revision
    assert evidence.lessons()[-1][0] == 2
    assert second["new_lessons"] == 0


def test_a_later_similar_defect_joins_instead_of_duplicating() -> None:
    evidence = Evidence()
    evidence.defect("201")
    evidence.defect("202")
    run(_conninfo("INTELLIGENCE"))
    evidence.defect("203")
    run(_conninfo("INTELLIGENCE"))

    assert evidence.lessons() == [(1, "candidate", "unverified", "CREATE_NEW", 3)]


@pytest.mark.parametrize(
    "spoiler",
    [{"kind": "Feature"}, {"review": "disputed"}, {"deleted": True}],
    ids=["feature-not-defect", "disputed-link", "deleted-ticket"],
)
def test_non_qualifying_evidence_never_supports_a_lesson(spoiler) -> None:
    evidence = Evidence()
    evidence.defect("301")
    evidence.defect("302", **spoiler)

    run(_conninfo("INTELLIGENCE"))

    assert evidence.lessons() == []  # one qualifying ticket is not a lesson


def test_review_requires_a_named_human_and_records_their_verdict() -> None:
    evidence = Evidence()
    evidence.defect("401")
    evidence.defect("402")
    run(_conninfo("INTELLIGENCE"))
    pattern_id = evidence.pattern_id()
    now = datetime.now(UTC)

    with psycopg.connect(_conninfo("INTELLIGENCE")) as conn:
        with pytest.raises(ValueError, match="verified_by"):
            lesson_store.review(conn, pattern_id, "reviewed", "", now)

    with psycopg.connect(_conninfo("INTELLIGENCE")) as conn, conn.transaction():
        revision = lesson_store.review(
            conn, pattern_id, "reviewed", "jane.doe", now, cause_status="verified",
            rationale="Confirmed: both tickets hit the same retry bug.",
        )
    assert revision == 2  # 1 was the automated CREATE_NEW; this is the human verdict
    assert evidence.lessons()[-1] == (2, "reviewed", "verified", "REVISE_EXISTING", 2)
    with psycopg.connect(_conninfo("INTELLIGENCE")) as conn:
        assert conn.execute(
            "SELECT count(*) FROM knowledge.change_events WHERE subject = %s "
            "AND event_type = 'lesson.reviewed'",
            (pattern_id,),
        ).fetchone()[0] == 1



def test_fork_splits_a_pattern_and_rejects_a_split_too_small_to_remain_a_lesson() -> None:
    evidence = Evidence()
    # Four tickets whose fixes all touch the same files, so consolidate() makes one lesson of
    # four members -- enough to fork 2-and-2, since each side of a fork needs >=2 members.
    for ticket in ("501", "502", "503", "504"):
        evidence.defect(ticket)
    run(_conninfo("INTELLIGENCE"))
    pattern_id = evidence.pattern_id()
    keys = evidence.observation_keys(pattern_id)
    assert len(keys) == 4
    now = datetime.now(UTC)

    # Splitting out only one observation would leave the new pattern with a single member --
    # rejected before any write happens.
    with psycopg.connect(_conninfo("INTELLIGENCE")) as conn, conn.transaction():
        with pytest.raises(ValueError, match="at least 2"):
            lesson_store.fork(conn, pattern_id, keys[:1], "jane.doe", now)

    with psycopg.connect(_conninfo("INTELLIGENCE")) as conn, conn.transaction():
        new_pattern_id = lesson_store.fork(
            conn, pattern_id, keys[:2], "jane.doe", now,
            rationale="These two actually come from an unrelated regression.",
        )
    assert new_pattern_id != pattern_id
    with psycopg.connect(_conninfo("INTELLIGENCE")) as conn:
        assert conn.execute(
            "SELECT count(*) FROM knowledge.change_events WHERE subject = %s "
            "AND event_type = 'lesson.forked'",
            (pattern_id,),
        ).fetchone()[0] == 1

    original_revisions = evidence.lessons()
    assert original_revisions[-1][:4] == (2, "candidate", "unverified", "REVISE_EXISTING")
    assert original_revisions[-1][4] == 2  # 2 members remain on the original pattern
    with psycopg.connect(_conninfo("INTELLIGENCE")) as conn:
        new_members = conn.execute(
            "SELECT count(*) FROM knowledge.lesson_memberships WHERE pattern_id = %s",
            (new_pattern_id,),
        ).fetchone()[0]
    assert new_members == 2


def test_ambiguous_observation_requires_explicit_human_resolution() -> None:
    evidence = Evidence()
    evidence.defect("601", files=["/src/A.cs"])
    evidence.defect("602", files=["/src/A.cs"])
    evidence.defect("603", files=["/src/B.cs"])
    evidence.defect("604", files=["/src/B.cs"])
    run(_conninfo("INTELLIGENCE"))

    evidence.defect("605", files=["/src/A.cs", "/src/B.cs"])
    outcome = run(_conninfo("INTELLIGENCE"))
    assert outcome["human_review"] == 1

    with psycopg.connect(_conninfo("INTELLIGENCE")) as conn:
        observation = conn.execute(
            "SELECT observation_key::text, pattern_ids::text[] "
            "FROM knowledge.lesson_decisions WHERE decision = 'HUMAN_REVIEW' "
            "AND observation_key IN (SELECT observation_key FROM knowledge.lesson_observations "
            "WHERE source_instance = %s) ORDER BY decision_id DESC LIMIT 1",
            (evidence.org,),
        ).fetchone()
        assert observation is not None
        key, options = observation
        assert len(options) == 2
        with pytest.raises(ValueError, match="verified_by"):
            lesson_store.resolve_ambiguous(conn, key, options[0], "", datetime.now(UTC), "reviewed")
        with pytest.raises(ValueError, match="not a candidate"):
            lesson_store.resolve_ambiguous(
                conn, key, str(uuid.uuid4()), "engineer", datetime.now(UTC), "reviewed"
            )

    with psycopg.connect(_conninfo("INTELLIGENCE")) as conn, conn.transaction():
        chosen = lesson_store.resolve_ambiguous(
            conn, key, options[0], "engineer", datetime.now(UTC),
            "Confirmed the same fix behavior and affected files.",
        )
        assert chosen == options[0]
        assert conn.execute(
            "SELECT count(*) FROM knowledge.change_events WHERE subject = %s "
            "AND event_type = 'lesson.observation_resolved'",
            (chosen,),
        ).fetchone()[0] == 1


    with psycopg.connect(_conninfo("INTELLIGENCE")) as conn:
        assert conn.execute(
            "SELECT pattern_id::text FROM knowledge.lesson_memberships WHERE observation_key = %s",
            (key,),
        ).fetchone() == (chosen,)
        with pytest.raises(ValueError, match="pending HUMAN_REVIEW"):
            lesson_store.resolve_ambiguous(
                conn, key, chosen, "engineer", datetime.now(UTC), "repeat"
            )


def test_api_role_cannot_modify_lesson_history() -> None:
    evidence = Evidence()
    evidence.defect("701")
    evidence.defect("702")
    run(_conninfo("INTELLIGENCE"))
    pattern_id = evidence.pattern_id()

    with psycopg.connect(_conninfo("API")) as conn:
        with pytest.raises(psycopg.errors.InsufficientPrivilege):
            lesson_store.review(
                conn, pattern_id, "reviewed", "operator", datetime.now(UTC),
                cause_status="verified",
            )
        conn.rollback()


def test_same_changed_file_with_unrelated_bug_symptoms_is_not_a_lesson() -> None:
    evidence = Evidence()
    files = ["/src/PaymentService.cs"]
    evidence.defect("801", files=files, title="Barcode print fails after payment retry")
    evidence.defect("802", files=files, title="Card reader timeout causes authorization failure")
    evidence.defect("803", files=files, title="Refund amount displayed incorrectly after cancellation")
    run(_conninfo("INTELLIGENCE"))
    assert evidence.lessons() == []


def test_similar_bug_symptoms_form_one_unverified_lesson() -> None:
    evidence = Evidence()
    evidence.defect("811", title="Barcode printing fails after payment retry")
    evidence.defect("812", title="Barcode print fails after payment retry")
    evidence.defect("813", title="Payment retry causes barcode print failure")
    run(_conninfo("INTELLIGENCE"))
    assert evidence.lessons() == [(1, "candidate", "unverified", "CREATE_NEW", 3)]
