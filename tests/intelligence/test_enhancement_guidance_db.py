"""ENH-01 against real PostgreSQL (opt-in, after `python -m db.manage all`).

PROP-02: recorded references are read from the dependency graph with the caller's access scopes
and from the latest scan only. Then one enhancement goes all the way: accepted by the push
handler, claimed by the analysis worker, answered with stored guidance in eight sections.
"""

import hashlib
import os

import pytest

psycopg = pytest.importorskip("psycopg")

from corpus_support import Corpus, conninfo  # noqa: E402
from jsonschema import Draft202012Validator  # noqa: E402
from test_brief_pipeline_db import (  # noqa: E402
    CONTRACTS,
    accept,
    drain,
    intelligence,
    live_ticket,
    request_event,
    stored,
    worker,
)
from test_retrieval_db import demo_history  # noqa: E402

from app.brief import schema  # noqa: E402
from app.brief.guidance import SECTION_CODES  # noqa: E402
from app.brief.history import PostgresBriefSources  # noqa: E402
from app.events.envelope import EventEnvelope  # noqa: E402
from app.knowledge.embeddings import HashingEmbedder  # noqa: E402

pytestmark = pytest.mark.skipif(
    os.environ.get("VISIONIQ_DB_INTEGRATION") != "1",
    reason="Set VISIONIQ_DB_INTEGRATION=1 after bootstrapping PostgreSQL",
)

WHEN = "2026-11-01T00:00:00Z"
PROJECT = "src/Payments/Payments.csproj"


def _key(*parts: str) -> str:
    return hashlib.sha256("|".join(parts).encode()).hexdigest()


def scan(corpus: Corpus, repository: str, revision: str = "c1", scope: str | None = None) -> str:
    """A code-scan evidence item of ``repository``, readable under the corpus's scope."""
    item = corpus.add("code_scan", {"id": f"scan-{repository}"}, revision=revision, scope=scope)
    with psycopg.connect(conninfo("API")) as connection:
        connection.execute(
            "UPDATE evidence.source_items SET repository_id = %s WHERE item_key = %s",
            (repository, item),
        )
    return item


def node(item: str, kind: str, native: str) -> str:
    key = _key(item, kind, native)
    with psycopg.connect(conninfo("API")) as connection:
        connection.execute(
            "INSERT INTO evidence.graph_nodes (node_key, anchor_item_key, node_kind, native_id) "
            "VALUES (%s, %s, %s, %s) ON CONFLICT (node_key) DO NOTHING",
            (key, item, kind, native),
        )
    return key


def edge(source: str, target: str, relation: str, item: str, revision: str = "c1") -> None:
    with psycopg.connect(conninfo("API")) as connection:
        connection.execute(
            "INSERT INTO evidence.graph_assertions (assertion_key, relationship_key, "
            "from_node_key, to_node_key, relation_type, classification, assertion_state, "
            "provenance_item_key, provenance_revision_id, source_asserted_at, observed_at, "
            "content_sha256) VALUES (%s, %s, %s, %s, %s, 'explicit', 'active', %s, %s, %s, %s, "
            "repeat('0', 64))",
            (
                _key(source, target, relation, revision),
                _key(source, target, relation),
                source,
                target,
                relation,
                item,
                revision,
                WHEN,
                WHEN,
            ),
        )


def refers(item: str, source: str, target: str, revision: str = "c1") -> None:
    """``source`` (a file) refers to ``target`` (a file), as the code scan records it."""
    edge(node(item, "file", source), node(item, "file", target), "file_dependency", item, revision)


def sources(corpus: Corpus, scope: str | None = None) -> PostgresBriefSources:
    return PostgresBriefSources(
        intelligence, HashingEmbedder(), [scope or corpus.scope], lambda key: True
    )


def test_a_file_is_found_through_its_project_and_a_python_file_directly() -> None:
    corpus = Corpus()
    item = scan(corpus, "kiosk")
    refers(item, "src/Kiosk/Kiosk.csproj", PROJECT)
    refers(item, "tests/Payments.Tests/Payments.Tests.csproj", PROJECT)
    refers(item, "tools/Build.csproj", "src/All.csproj")  # an outer project: not the innermost
    refers(item, "app/print.py", "app/retry.py")

    found = sources(corpus).dependents(
        "kiosk", ["/src/Payments/Api/PaymentResponse.cs", "/app/retry.py", "/docs/readme.md"]
    )

    by_target = {entry.target: entry for entry in found}
    assert set(by_target) == {PROJECT, "app/retry.py"}  # nothing is recorded for the readme
    assert by_target[PROJECT].dependents == (
        "src/Kiosk/Kiosk.csproj",
        "tests/Payments.Tests/Payments.Tests.csproj",
    )
    assert by_target["app/retry.py"].dependents == ("app/print.py",)
    assert by_target[PROJECT].item_key == item and by_target[PROJECT].revision == "c1"
    assert not any(entry.cross_repository for entry in found)


def test_only_the_latest_scan_of_a_repository_counts() -> None:
    corpus = Corpus()
    scan(corpus, "kiosk", revision="c0")
    item = scan(corpus, "kiosk", revision="c1")  # the same item, scanned again
    refers(item, "src/Old/Old.csproj", PROJECT, revision="c0")  # a reference that is gone
    refers(item, "src/Kiosk/Kiosk.csproj", PROJECT, revision="c1")

    (found,) = sources(corpus).dependents("kiosk", ["src/Payments/RetryPolicy.cs"])

    assert found.dependents == ("src/Kiosk/Kiosk.csproj",)


def test_file_dependencies_never_fall_back_to_another_readable_repository() -> None:
    corpus = Corpus()
    other = scan(corpus, "other-repo")
    refers(other, "src/Other/Consumer.csproj", PROJECT)

    # Both scans are readable under the same scope; a matching path in the other
    # repository must not supply guidance for a repository with no matching edges.
    assert sources(corpus).dependents("kiosk", ["src/Payments/RetryPolicy.cs"]) == []

    own = scan(corpus, "kiosk")
    refers(own, "src/Kiosk/Kiosk.csproj", PROJECT)

    found = sources(corpus).dependents("kiosk", ["src/Payments/RetryPolicy.cs"])
    assert len(found) == 1
    assert found[0].dependents == ("src/Kiosk/Kiosk.csproj",)
    assert found[0].item_key == own


def test_references_are_read_only_under_the_callers_access_scope() -> None:
    corpus = Corpus()
    item = scan(corpus, "kiosk")
    refers(item, "src/Kiosk/Kiosk.csproj", PROJECT)
    hidden = scan(corpus, "secret", scope="ado-project:https://dev.azure.com/other/Secret")
    refers(hidden, "src/Secret/Secret.csproj", PROJECT)

    (found,) = sources(corpus).dependents("kiosk", ["src/Payments/RetryPolicy.cs"])

    assert found.dependents == ("src/Kiosk/Kiosk.csproj",)  # the other project's file is not shown
    assert sources(corpus, "ado-project:nobody").dependents("kiosk", [PROJECT]) == []
    assert (
        PostgresBriefSources(intelligence, HashingEmbedder(), [], lambda key: True).dependents(
            "kiosk", [PROJECT]
        )
        == []
    )


def test_other_repositories_that_declare_a_dependency_are_named_when_readable() -> None:
    corpus = Corpus()
    kiosk = scan(corpus, "kiosk")
    recon = scan(corpus, "recon")
    edge(
        node(recon, "file", "deps/kiosk.gitmodules"),
        node(kiosk, "repository", "kiosk"),
        "repository_dependency",
        recon,
    )
    unreadable = scan(corpus, "billing", scope="ado-project:https://dev.azure.com/other/Billing")
    edge(
        node(unreadable, "file", "requirements.txt"),
        node(kiosk, "repository", "kiosk"),
        "repository_dependency",
        unreadable,
    )

    (found,) = sources(corpus).dependents("kiosk", [])

    assert found.cross_repository and found.target == "kiosk"
    assert found.dependents == ("recon: deps/kiosk.gitmodules",)  # billing is not readable
    assert sources(corpus).dependents("", []) == []


def test_an_enhancement_is_answered_with_stored_guidance_in_eight_sections() -> None:
    corpus = Corpus()
    demo_history(corpus)
    item = scan(corpus, "repo-1")
    refers(item, "src/Kiosk/Kiosk.csproj", PROJECT)
    first = request_event(corpus, live_ticket(corpus))
    envelope = EventEnvelope.model_validate(
        {
            **first.model_dump(mode="json"),
            "data": {**first.data, "ticket_kind": "enhancement", "repository_id": "repo-1"},
        }
    )
    CONTRACTS.validate(envelope.model_dump(mode="json"))

    accept(envelope)
    drain(worker())

    result = stored(envelope.run_id)
    status, result_id, state, _reasons, attempts = result["request"]
    brief_id, _brief_state, _band, document, _accepted_at, _ = result["brief"]
    assert (status, attempts) == ("completed", 1) and result_id == brief_id
    assert list(Draft202012Validator(schema.json_schema()).iter_errors(document)) == []

    assert document["kind"] == "enhancement"
    assert [section["section"] for section in document["guidance"]] == list(SECTION_CODES)
    sections = {section["section"]: section for section in document["guidance"]}
    # The files in scope belong to Payments.csproj, which Kiosk.csproj refers to: written by code.
    assert sections["contracts_and_apis"]["status"] == "covered"
    reference = next(e for e in document["evidence"] if e["kind"] == "code_dependency")
    assert reference["title"] == f"{PROJECT} is referenced by 1 other file"
    (contract,) = [a for a in document["advice"] if a["section"] == "contracts_and_apis"]
    assert contract["evidence_ids"] == [reference["id"]]
    assert "src/Kiosk/Kiosk.csproj" in contract["statement"]
    # What the history shows fills its sections; the rest are missing, not guessed.
    assert sections["related_past_changes"]["status"] == "covered"
    assert sections["do_and_dont"]["status"] == "covered"  # ticket 102's fix was reverted
    assert sections["tests_to_add"] == {
        "section": "tests_to_add",
        "title": "Tests to add",
        "status": "missing",
        "advice_ids": [],
    }
    assert all(a["section"] in SECTION_CODES for a in document["advice"])
    assert state in {"PUBLISHED", "PARTIAL"}

    # The guidance prompt was used, and the reply carries the advice ids like a brief's.
    assert (
        "enhancement_approach",
        "primary",
        "offline",
        "offline-extractive-1",
        "brief.enhancement@v1",
        "OK",
    ) in result["calls"]
    assert not any(call[0] == "brief_draft" for call in result["calls"])
    ((payload,),) = result["events"]
    CONTRACTS.validate(payload)
    assert payload["data"]["advice_ids"] == [a["advice_id"] for a in document["advice"]]
