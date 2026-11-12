"""Seed a LOCAL database with a small synthetic history, so the brief flow can be tried end to end
without Azure DevOps, Confluence or Google Cloud.

    python scripts/brief-demo/seed_demo_history.py

It writes the master design's worked example (a payment retry breaks receipt printing) as if
ingestion had stored it: four past bugs, the pull requests that fixed them (one later reverted),
some ordinary changes, and the live ticket #4711 that the sample webhook picks up. It then
indexes them with the offline test embedder and publishes a knowledge snapshot.

Safety: everything is synthetic, nothing is read from or written to any source system, and the
script refuses anything but a local database (DB_TARGET=local on localhost). Database settings
come from the repository .env, exactly as for ``python -m db.manage``. Run it once per database:
the history is keyed by --org, so a second run with the same --org is refused.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT), str(ROOT / "src" / "VisionIQ.Intelligence")]

from db.manage import LocalConfig  # noqa: E402
from psycopg.conninfo import make_conninfo  # noqa: E402

from app.knowledge import lessons_cli, snapshots  # noqa: E402
from app.knowledge.embeddings import HashingEmbedder  # noqa: E402

RETRY_FIX = ["/src/Payments/RetryPolicy.cs", "/src/Kiosk/ReceiptPrinter.cs"]
LIVE_DEFECT = (
    "Kiosk payment captured, barcode print failed after retry.\n"
    "   at Shop.Payments.RetryPolicy.Execute(Int32 attempt) in /src/Payments/RetryPolicy.cs:line 42"
)
HISTORY = [
    (
        101,
        "Barcode print fails after payment retry",
        "Pay at kiosk, payment captured, retry printing: no barcode on the receipt. "
        "Fixed by making the retry policy idempotent.",
        "201",
        RETRY_FIX,
        False,
    ),
    (
        102,
        "Duplicate receipt printed on retry",
        "Retrying a captured payment prints twice. The retry counter fix was merged.",
        "202",
        RETRY_FIX,
        True,
    ),
    (
        103,
        "Login page CSS broken",
        "Login button overlaps the logo on small screens.",
        "203",
        ["/web/login.css"],
        False,
    ),
    (
        104,
        "Payment captured twice when network drops",
        "Gateway timeout during payment; the retry submits again.",
        "204",
        ["/src/Network/Gateway.cs"],
        False,
    ),
    # Four more defects in the payments area: enough for the hotspot sweep to flag src/Payments.
    *[
        (
            number,
            title,
            repro,
            str(number + 100),
            ["/src/Payments/PaymentSession.cs"],
            False,
        )
        for number, title, repro in [
            (105, "Payment session expires during 3-D Secure", "Bank page open for a minute."),
            (106, "Payment session lost after app resume", "Resume the app during checkout."),
            (107, "Payment session reused across users", "Second user sees a stale basket."),
            (108, "Payment session leak on cancel", "Cancel at the bank page; session stays."),
        ]
    ],
]


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--org", default="https://dev.azure.com/your-org")
    parser.add_argument("--project", default="VisionIQ-Demo")
    parser.add_argument("--scope", default="project:VisionIQ-Demo")
    parser.add_argument("--ticket", type=int, default=4711)
    parser.add_argument(
        "--force", action="store_true", help="publish even if the snapshot check objects"
    )
    args = parser.parse_args(argv)
    org = args.org.rstrip("/")

    settings = LocalConfig()
    settings.check()
    if settings.db_host not in ("localhost", "127.0.0.1", "::1"):
        sys.stderr.write("Refusing to seed: DB_HOST is not this machine.\n")
        return 2
    archive: dict[str, bytes] = {}
    keys: list[str] = []

    def item_key(kind: str, native: str) -> str:
        return hashlib.sha256(f"{org}|{args.project}|{kind}|{native}".encode()).hexdigest()

    # One transaction: the latest-revision foreign key is checked at commit.
    with settings.connect("api") as api, api.transaction():
        seeded = api.execute(
            "SELECT count(*) FROM evidence.source_items "
            "WHERE source_instance = %s AND project_id = %s",
            (org, args.project),
        ).fetchone()[0]
        if seeded:
            sys.stderr.write(f"{org}/{args.project} already has {seeded} items; nothing changed.\n")
            return 1
        tickets = [(number, title, repro) for number, title, repro, *_ in HISTORY] + [
            (args.ticket, "Barcode not printed after payment retry", LIVE_DEFECT)
        ]
        for number, title, repro in tickets:
            record = {"id": number, "workItemType": "Bug", "title": title, "reproSteps": repro}
            body = json.dumps(record).encode()
            key = item_key("work_item_current", str(number))
            uri = f"mem://{key}/1"
            archive[uri] = body
            keys.append(key)
            api.execute(
                "INSERT INTO evidence.source_items (item_key, provider, source_instance, "
                "project_id, kind, external_id, latest_revision_id, latest_source_updated_at, "
                "first_observed_at, last_observed_at) "
                "VALUES (%s, 'azure_devops', %s, %s, 'work_item_current', %s, '1', "
                "now(), now(), now())",
                (key, org, args.project, str(number)),
            )
            api.execute(
                "INSERT INTO evidence.source_revisions (item_key, provider_revision_id, "
                "source_updated_at, observed_at, content_sha256, source_url, "
                "retrieved_under_principal, redacted_payload, redaction_version, "
                "normalizer_version, raw_snapshot_uri, archive_status, acl_verified, "
                "acl_principals) "
                "VALUES (%s, '1', now(), now(), %s, %s, 'demo-seed', %s, 'demo', 'demo', %s, "
                "'ARCHIVED', true, %s)",
                (
                    key,
                    hashlib.sha256(body).hexdigest(),
                    f"{org}/{args.project}/_workitems/edit/{number}",
                    json.dumps({"kind": "work_item_current", "workItemType": "Bug"}),
                    uri,
                    json.dumps([args.scope]),
                ),
            )
        changes = [(pr, paths) for *_, pr, paths, _ in HISTORY]
        # Ordinary changes, so the hotspot sweep has activity to compare the fixes with.
        changes += [(str(900 + n), ["/web/site/page.css"]) for n in range(12)]
        changes += [(str(950 + n), ["/src/Catalog/Search.cs"]) for n in range(8)]
        for number, _, _, pr, _, reverted in HISTORY:
            api.execute(
                "INSERT INTO evidence.ticket_fix_links (association_key, source_instance, "
                "project_id, ticket_id, repository_id, pull_request_id, source_classification, "
                "revert_commit_shas) VALUES (%s, %s, %s, %s, 'demo-repo', %s, 'explicit', %s)",
                (
                    hashlib.sha256(f"{org}|{args.project}|{number}|{pr}".encode()).hexdigest(),
                    org,
                    args.project,
                    str(number),
                    pr,
                    json.dumps(["deadbeef"] if reverted else []),
                ),
            )
        for pr, paths in changes:
            api.execute(
                "INSERT INTO evidence.fix_change_sets (source_instance, project_id, repository_id, "
                "fix_kind, fix_id, paths, provenance_item_key, observed_at) "
                "VALUES (%s, %s, 'demo-repo', 'pull_request', %s, %s, %s, now())",
                (org, args.project, pr, paths, "a" * 64),
            )

    conninfo = make_conninfo(
        "",
        # Docker publishes PostgreSQL on IPv4 only; "localhost" can resolve to ::1 and hang.
        host="127.0.0.1" if settings.db_host == "localhost" else settings.db_host,
        port=settings.db_port,
        dbname=settings.db_local_name,
        user=settings.db_intelligence_user,
        password=settings.db_intelligence_password.get_secret_value(),
        connect_timeout=10,
    )
    _, snapshot = snapshots.refresh(
        conninfo, archive.__getitem__, HashingEmbedder(), only=keys, force=args.force
    )
    lessons_cli.run(conninfo)
    sys.stdout.write(
        f"Seeded {len(keys)} tickets and {len(changes)} changes under {org}/{args.project} "
        f"(scope {args.scope}).\nKnowledge snapshot: {snapshot.status}"
        f"{'' if snapshot.status == 'published' else ' - rerun with --force after reading why'}.\n"
        f"The live ticket is #{args.ticket}. Start the stack with EMBEDDING_PROVIDER=test.\n"
    )
    return 0 if snapshot.status == "published" else 3


if __name__ == "__main__":
    sys.exit(main())
