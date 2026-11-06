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

from app.knowledge.lessons_cli import run  # noqa: E402

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

    def defect(self, ticket, kind="Bug", files=FILES, review=None, deleted=False, reverts=()):
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
                (item, json.dumps({"kind": "work_item_current", "workItemType": kind})),
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
