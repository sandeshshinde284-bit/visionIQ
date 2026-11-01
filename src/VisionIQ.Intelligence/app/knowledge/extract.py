"""LRN-08: turn one archived evidence record (the ingestor's JSON) into titled text sections."""

from __future__ import annotations

from dataclasses import dataclass

from app.knowledge.chunking import Section, html_to_text

INDEXED_KINDS = ("work_item_current", "pull_request", "commit", "design_page", "design_decision")


@dataclass(frozen=True)
class Document:
    label: str
    title: str
    sections: list[Section]


def _text(record: dict, name: str) -> str:
    value = record.get(name)
    return html_to_text(value) if isinstance(value, str) else ""


def extract(kind: str, record: dict) -> Document:
    """Searchable text of one record; unknown kinds are rejected (never indexed by accident)."""
    if kind == "work_item_current":
        context = " | ".join(
            part
            for part in (
                f"Area: {record['areaPath']}" if record.get("areaPath") else "",
                f"Tags: {', '.join(record['tags'])}" if record.get("tags") else "",
                f"State: {record['state']}" if record.get("state") else "",
            )
            if part
        )
        return Document(
            f"{record.get('workItemType', 'Work item')} {record.get('id', '')}".strip(),
            str(record.get("title", "")),
            [
                Section("Description", _text(record, "description")),
                Section("Repro steps", _text(record, "reproSteps")),
                Section("System info", _text(record, "systemInfo")),
                Section("Acceptance criteria", _text(record, "acceptanceCriteria")),
                Section("Context", context),
            ],
        )
    if kind == "pull_request":
        sections = [Section("Description", _text(record, "description"))]
        for thread in record.get("reviewThreads") or []:
            where = f" on {thread['filePath']}" if thread.get("filePath") else ""
            body = "\n\n".join(
                html_to_text(c.get("content", "")) for c in thread.get("comments") or []
            )
            sections.append(Section(f"Review comment{where}", body))
        return Document(
            f"Pull request {record.get('id', '')}".strip(), str(record.get("title", "")), sections
        )
    if kind == "commit":
        message = str(record.get("comment", ""))
        title = message.splitlines()[0] if message else ""
        return Document(
            f"Commit {str(record.get('commitId', ''))[:12]}".strip(),
            title,
            [Section("Commit message", message)],
        )
    if kind == "design_page":
        sections = [
            Section(s.get("heading") or "Page", s.get("text", ""))
            for s in record.get("sections") or []
        ]
        if not any(s.text.strip() for s in sections):
            sections = [Section("Page", str(record.get("text", "")))]
        return Document("Confluence page", str(record.get("title", "")), sections)
    if kind == "design_decision":
        return Document(
            "Design decision",
            str(record.get("title", "")),
            [
                Section(
                    "Decision",
                    "\n\n".join(
                        f"{label}: {record[name]}"
                        for label, name in (("Status", "status"), ("Outcome", "outcome"))
                        if record.get(name)
                    ),
                )
            ],
        )
    raise ValueError(f"Kind {kind!r} is not indexed")
