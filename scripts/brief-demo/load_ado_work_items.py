"""Load a few chosen Azure DevOps work items into a LOCAL knowledge base, for a pilot.

    python scripts/brief-demo/load_ado_work_items.py --ids 116844 --scope <security_scope_id>

A brief needs the picked-up ticket (and any history it should draw on) in the knowledge base. The
full path is the LRN ingestion in canonical mode, which needs Cloud Storage. This script is the
small local stand-in for a pilot: it reads the listed work items from Azure DevOps, stores them
as evidence the way ingestion would, and indexes them.

What it does and does not do:

* Azure DevOps: READ ONLY. It sends GET requests for the listed work items and their comments
  with ADO_READ_PAT, and nothing else. It never creates, edits or comments on anything.
* Storage: the local PostgreSQL only (it refuses another host). The raw record is kept in memory
  for indexing and is not archived anywhere; the index keeps the redacted text.
* Scope: each item is readable by the ADO project's own access principal and by --scope, which
  must equal the pickup policy's security_scope_id for that project.
* It loads work items only: no pull requests, commits or fix links. Risk counts therefore show
  "no data"; advice can still draw on the other work items you load as history.

Settings come from the repository .env (ADO_ORG_URL, ADO_READ_PAT, database, embedding provider).
The embedding provider must be the one the service uses (EMBEDDING_PROVIDER=test for a local run).
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import sys
from pathlib import Path
from urllib.parse import quote

ROOT = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(ROOT), str(ROOT / "src" / "VisionIQ.Intelligence")]

import httpx  # noqa: E402
from db.manage import LocalConfig  # noqa: E402
from psycopg.conninfo import make_conninfo  # noqa: E402
from pydantic import SecretStr  # noqa: E402
from pydantic_settings import BaseSettings, SettingsConfigDict  # noqa: E402

from app.knowledge import embeddings, snapshots  # noqa: E402

API_VERSION = "7.1"
COMMENTS_API_VERSION = "7.1-preview.4"
MAX_ITEMS = 50
MAX_COMMENTS = 50


class AdoSettings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=ROOT / ".env", env_file_encoding="utf-8", extra="ignore", env_ignore_empty=True
    )
    ado_org_url: str
    ado_read_pat: SecretStr
    embedding_provider: str = "vertex"


def record_of(item: dict, comments: list[dict]) -> dict:
    """The work item in the shape ingestion stores and the indexer reads (LRN-01, LRN-08)."""
    fields = item.get("fields", {})
    tags = [tag.strip() for tag in (fields.get("System.Tags") or "").split(";") if tag.strip()]
    return {
        "id": item["id"],
        "rev": item.get("rev", 1),
        "workItemType": fields.get("System.WorkItemType"),
        "title": fields.get("System.Title", ""),
        "state": fields.get("System.State"),
        "areaPath": fields.get("System.AreaPath"),
        "tags": tags,
        "description": fields.get("System.Description"),
        "reproSteps": fields.get("Microsoft.VSTS.TCM.ReproSteps"),
        "systemInfo": fields.get("Microsoft.VSTS.TCM.SystemInfo"),
        "acceptanceCriteria": fields.get("Microsoft.VSTS.Common.AcceptanceCriteria"),
        "comments": [{"text": comment.get("text", "")} for comment in comments],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--ids", required=True, help="comma-separated work-item ids, e.g. 116844")
    parser.add_argument(
        "--scope", required=True, help="the policy's security_scope_id for the project"
    )
    parser.add_argument("--force", action="store_true", help="publish even if the check objects")
    args = parser.parse_args(argv)
    try:
        ids = sorted({int(part) for part in args.ids.split(",") if part.strip()})
    except ValueError:
        parser.error("--ids must be a comma-separated list of work-item numbers")
    if not ids or any(number <= 0 for number in ids) or len(ids) > MAX_ITEMS:
        parser.error(f"--ids needs 1 to {MAX_ITEMS} positive work-item numbers")

    database = LocalConfig()
    database.check()
    if database.db_host not in ("localhost", "127.0.0.1", "::1"):
        sys.stderr.write("Refusing to load: DB_HOST is not this machine.\n")
        return 2
    ado = AdoSettings()
    org = ado.ado_org_url.strip().rstrip("/")
    if not org.startswith("https://"):
        sys.stderr.write("ADO_ORG_URL must be an https URL.\n")
        return 2
    # The indexer must embed with the same provider the service searches with.
    os.environ.setdefault("EMBEDDING_PROVIDER", ado.embedding_provider)
    embedder = embeddings.from_environment()
    token = base64.b64encode(f":{ado.ado_read_pat.get_secret_value()}".encode()).decode()

    archive: dict[str, bytes] = {}
    keys: list[str] = []
    loaded: list[str] = []
    with (
        httpx.Client(
            headers={"Authorization": f"Basic {token}", "Accept": "application/json"}, timeout=30
        ) as http,
        database.connect("api") as api,
        api.transaction(),
    ):
        for number in ids:
            # GET only: this script never changes anything in Azure DevOps.
            response = http.get(
                f"{org}/_apis/wit/workitems/{number}",
                params={"$expand": "fields", "api-version": API_VERSION},
            )
            if response.status_code != 200 or "json" not in response.headers.get(
                "content-type", ""
            ):
                sys.stderr.write(f"Work item {number}: ADO answered HTTP {response.status_code}.\n")
                return 4
            item = response.json()
            project = item["fields"]["System.TeamProject"]
            answer = http.get(
                f"{org}/{quote(project)}/_apis/wit/workItems/{number}/comments",
                params={"$top": MAX_COMMENTS, "api-version": COMMENTS_API_VERSION},
            )
            comments = answer.json().get("comments", []) if answer.status_code == 200 else []
            record = record_of(item, comments)
            body = json.dumps(record, sort_keys=True).encode()
            revision = str(record["rev"])
            key = hashlib.sha256(
                f"azure_devops|{org}|{project}|work_item_current|{number}".encode()
            ).hexdigest()
            uri = f"mem://{key}/{revision}"
            archive[uri] = body
            keys.append(key)
            principals = sorted({f"ado-project:{org}/{project}", args.scope})
            api.execute(
                "INSERT INTO evidence.source_items (item_key, provider, source_instance, "
                "project_id, kind, external_id, latest_source_updated_at, first_observed_at, "
                "last_observed_at) VALUES (%s, 'azure_devops', %s, %s, 'work_item_current', %s, "
                "now(), now(), now()) "
                "ON CONFLICT (item_key) DO UPDATE SET last_observed_at = now()",
                (key, org, project, str(number)),
            )
            api.execute(
                "INSERT INTO evidence.source_revisions (item_key, provider_revision_id, "
                "source_updated_at, observed_at, content_sha256, source_url, "
                "retrieved_under_principal, redacted_payload, redaction_version, "
                "normalizer_version, raw_snapshot_uri, archive_status, acl_verified, "
                "acl_principals) VALUES (%s, %s, %s, now(), %s, %s, 'pilot-loader', %s, 'pilot', "
                "'pilot', %s, 'ARCHIVED', true, %s) "
                "ON CONFLICT (item_key, provider_revision_id) DO NOTHING",
                (
                    key,
                    revision,
                    item["fields"].get("System.ChangedDate"),
                    hashlib.sha256(body).hexdigest(),
                    f"{org}/{quote(project)}/_workitems/edit/{number}",
                    json.dumps(
                        {
                            "kind": "work_item_current",
                            "workItemType": record["workItemType"],
                            "state": record["state"],
                        }
                    ),
                    uri,
                    json.dumps(principals),
                ),
            )
            api.execute(
                "UPDATE evidence.source_items SET latest_revision_id = %s, "
                "latest_source_updated_at = %s WHERE item_key = %s",
                (revision, item["fields"].get("System.ChangedDate"), key),
            )
            loaded.append(f"#{number} {record['workItemType']} ({record['state']}, rev {revision})")

    conninfo = make_conninfo(
        "",
        # Docker publishes PostgreSQL on IPv4 only; "localhost" can resolve to ::1 and hang.
        host="127.0.0.1" if database.db_host == "localhost" else database.db_host,
        port=database.db_port,
        dbname=database.db_local_name,
        user=database.db_intelligence_user,
        password=database.db_intelligence_password.get_secret_value(),
        connect_timeout=10,
    )
    report, snapshot = snapshots.refresh(
        conninfo, archive.__getitem__, embedder, only=keys, force=args.force
    )
    sys.stdout.write(
        "Loaded from Azure DevOps (read-only): " + "; ".join(loaded) + "\n"
        f"Indexed {report.indexed} item(s) with {embedder.model_id}"
        f"{'' if not report.failed else f', {len(report.failed)} failed'}. "
        f"Knowledge snapshot: {snapshot.status}"
        f"{'' if snapshot.status == 'published' else ' (' + str(snapshot.reason) + ')'}.\n"
        f"Readable under scope: {args.scope}\n"
    )
    return 0 if snapshot.status == "published" and not report.failed else 3


if __name__ == "__main__":
    sys.exit(main())
