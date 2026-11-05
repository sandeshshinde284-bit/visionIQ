"""LRN-07 graph projection from LRN-06 complete commit-pinned scan metadata.

No raw source code or guessed symbols are stored. The provenance reference
points to the exact immutable LRN-03 code-scan revision. A Git/ADO commit link
is made only when both sides share approved project + repository native IDs
and exactly the same full commit SHA.
"""

import hashlib
import struct
import subprocess
from dataclasses import dataclass
from datetime import UTC
from typing import TYPE_CHECKING

import psycopg

if TYPE_CHECKING:
    from app.code_intelligence.canonical import ScanEvidence
    from app.code_intelligence.relationships import DependencyLink
    from app.code_intelligence.scanner import RepositoryScan


def _hash(*values: str) -> str:
    """Hash length-prefixed UTF-8 values, matching the .NET graph contract."""
    blob = b"".join(
        struct.pack("<i", len(encoded)) + encoded
        for encoded in (value.encode("utf-8") for value in values)
    )
    return hashlib.sha256(blob).hexdigest()


@dataclass(frozen=True, slots=True)
class GraphCodeNode:
    """A file, symbol or commit linked to a canonical code-scan evidence item."""

    anchor: str
    kind: str
    native_id: str

    def key(self) -> str:
        """Return a source-scoped key, not a global filename or symbol name."""
        if len(self.anchor) != 64 or not self.native_id or len(self.native_id) > 2048:
            raise ValueError("Invalid source-bound graph node identity")
        return _hash(self.anchor, self.kind.title().replace("_", ""), self.native_id)


@dataclass(frozen=True, slots=True)
class GraphCodeEdge:
    """A source-provenance-backed structural edge from a complete Git scan."""

    source: GraphCodeNode
    target: GraphCodeNode
    kind: str


def project_scan_edges(
    scan: "RepositoryScan", evidence: "ScanEvidence"
) -> tuple[GraphCodeEdge, ...]:
    """Build only committed file and structurally parsed symbol relationships."""
    if scan.status != "COMPLETE" or scan.commit_sha.lower() != evidence.revision:
        raise ValueError("Only matching, complete Git revisions can produce graph evidence")

    commit = GraphCodeNode(evidence.item_key, "commit", evidence.revision)
    edges: list[GraphCodeEdge] = []
    paths = {file.path: file.blob_sha for file in scan.files}
    if len(paths) != len(scan.files):
        raise ValueError("Duplicate Git file path in graph scan")
    for file in sorted(scan.files, key=lambda item: item.path):
        file_node = GraphCodeNode(evidence.item_key, "file", file.path)
        edges.append(GraphCodeEdge(commit, file_node, "commit_file"))

    for analysis in scan.analyses:
        if analysis.path not in paths or paths[analysis.path] != analysis.blob_sha:
            raise ValueError("Graph symbols must match the committed source blob")
        if analysis.symbol_analysis != "AVAILABLE":
            continue
        file_node = GraphCodeNode(evidence.item_key, "file", analysis.path)
        for symbol in analysis.symbols:
            name = symbol.qualified_name or symbol.name
            native = f"{analysis.path}::{name}:{symbol.start_line}"
            if symbol.kind == "test":
                # LRN-07: tests are first-class, so "defect -> fix -> test" can be queried.
                edges.append(
                    GraphCodeEdge(
                        file_node,
                        GraphCodeNode(evidence.item_key, "test_case", native),
                        "file_test",
                    )
                )
                continue
            edges.append(
                GraphCodeEdge(
                    file_node, GraphCodeNode(evidence.item_key, "symbol", native), "file_symbol"
                )
            )
    return tuple(edges)


def project_declared_dependencies(
    scan: "RepositoryScan",
    evidence: "ScanEvidence",
    dependencies: tuple["DependencyLink", ...],
    approved_targets: dict[str, "ScanEvidence"],
) -> tuple[GraphCodeEdge, ...]:
    """Emit only resolved imports and approved source-declared cross-repo dependencies."""
    if scan.status != "COMPLETE" or scan.commit_sha.lower() != evidence.revision:
        raise ValueError("Dependencies require a matching complete Git scan")
    paths = {file.path for file in scan.files}
    output: list[GraphCodeEdge] = []
    for link in dependencies:
        if (
            link.source_repo != evidence.identity.repository_id
            or link.source_commit.lower() != evidence.revision
            or link.source_path not in paths
        ):
            raise ValueError("Declared dependency has an unapproved source identity")

        source = GraphCodeNode(evidence.item_key, "file", link.source_path)
        if (
            link.status == "RESOLVED_EXPLICIT"
            and link.kind in {"PYTHON_IMPORT", "PROJECT_REFERENCE"}
            and link.target_repo == evidence.identity.repository_id
            and link.target_commit == evidence.revision
            and link.target_path in paths
        ):
            target = GraphCodeNode(evidence.item_key, "file", link.target_path)
            if source.key() != target.key():
                output.append(GraphCodeEdge(source, target, "file_dependency"))
        elif (
            link.status == "DECLARED_TARGET_REPOSITORY"
            and link.kind == "DECLARED_GIT_DEPENDENCY"
            and link.target_repo is not None
        ):
            approved = approved_targets.get(link.target_repo)
            if approved is None:
                continue
            if (
                approved.identity.repository_id != link.target_repo
                or approved.revision != link.target_commit
                or approved.identity.project_id == ""
                or approved.item_key == evidence.item_key
            ):
                raise ValueError("Cross-repository target does not match approved evidence")
            output.append(
                GraphCodeEdge(
                    source,
                    GraphCodeNode(approved.item_key, "repository", link.target_repo),
                    "repository_dependency",
                )
            )
    return tuple(output)


def project_fix_change_edges(
    connection_string: str,
    scan_item_key: str,
    project_id: str,
    repository_id: str,
    approved_ado_instance: str,
) -> int:
    """Review R7-01/R7-02: a direct edge from a commit or pull request to each file it changed
    (evidence.fix_change_sets), independent of whether that exact revision was ever a scan's HEAD.

    File node identity (``GraphCodeNode(scan_item_key, "file", path)``) is anchored to the
    repository's own code-scan item, which is stable across every commit ever scanned for that
    repository -- so this can be written for any historical fix once the repository has been
    scanned at least once, without re-scanning that exact historical commit. Provenance is the
    fix's own current ADO revision (never the scan's), fetched here so only a confirmed, still
    current (not tombstoned) revision can be cited -- the same guard already used to bridge
    ``commit_file`` edges to an ADO commit node above.
    """
    written = 0
    with (
        psycopg.connect(connection_string, connect_timeout=10) as connection,
        connection.cursor() as cursor,
    ):
        cursor.execute(
            """
            SELECT fcs.fix_kind, fcs.fix_id, fcs.paths, fcs.provenance_item_key,
                   si.latest_revision_id, sr.source_updated_at
            FROM evidence.fix_change_sets AS fcs
            JOIN evidence.source_items AS si ON si.item_key = fcs.provenance_item_key
            JOIN evidence.source_revisions AS sr
              ON sr.item_key = si.item_key AND sr.provider_revision_id = si.latest_revision_id
            WHERE fcs.source_instance = %s AND fcs.project_id = %s AND fcs.repository_id = %s
              AND si.latest_revision_id IS NOT NULL AND si.deleted_at IS NULL
            """,
            (approved_ado_instance, project_id, repository_id),
        )
        rows = cursor.fetchall()

        edges: list[tuple[GraphCodeEdge, str, str, object]] = []
        for (
            fix_kind,
            fix_id,
            paths,
            provenance_item_key,
            latest_revision_id,
            source_updated_at,
        ) in rows:
            relation = "commit_changed_file" if fix_kind == "commit" else "pr_changed_file"
            node_kind = "commit" if fix_kind == "commit" else "pull_request"
            fix_node = GraphCodeNode(provenance_item_key, node_kind, fix_id)
            for path in paths:
                edges.append(
                    (
                        GraphCodeEdge(
                            fix_node, GraphCodeNode(scan_item_key, "file", path), relation
                        ),
                        provenance_item_key,
                        latest_revision_id,
                        source_updated_at,
                    )
                )

        nodes: dict[str, GraphCodeNode] = {}
        for edge, _, _, _ in edges:
            nodes[edge.source.key()] = edge.source
            nodes[edge.target.key()] = edge.target
        for key, node in nodes.items():
            cursor.execute(
                """
                INSERT INTO evidence.graph_nodes (node_key, anchor_item_key, node_kind, native_id)
                VALUES (%s, %s, %s, %s)
                ON CONFLICT (node_key) DO NOTHING
                """,
                (key, node.anchor, node.kind, node.native_id),
            )
            cursor.execute(
                "SELECT anchor_item_key, node_kind, native_id FROM evidence.graph_nodes "
                "WHERE node_key = %s",
                (key,),
            )
            if cursor.fetchone() != (node.anchor, node.kind, node.native_id):
                raise ValueError("Graph node collision with immutable source identity")

        for edge, provenance_item_key, provenance_revision_id, source_updated_at in edges:
            relation_key = _hash(
                edge.source.key(),
                edge.target.key(),
                edge.kind.title().replace("_", ""),
                provenance_item_key,
            )
            assertion = _hash(relation_key, provenance_revision_id)
            canonical_time = (
                source_updated_at.astimezone(UTC).strftime("%Y-%m-%dT%H:%M:%S.%f") + "0+00:00"
            )
            content = _hash(assertion, "Explicit", "Active", canonical_time, "")
            cursor.execute(
                """
                INSERT INTO evidence.graph_assertions (
                    assertion_key, relationship_key, from_node_key, to_node_key,
                    relation_type, classification, assertion_state,
                    provenance_item_key, provenance_revision_id,
                    source_asserted_at, content_sha256
                ) VALUES (
                    %s, %s, %s, %s, %s, 'explicit', 'active', %s, %s, %s, %s
                ) ON CONFLICT (assertion_key) DO NOTHING
                """,
                (
                    assertion,
                    relation_key,
                    edge.source.key(),
                    edge.target.key(),
                    edge.kind,
                    provenance_item_key,
                    provenance_revision_id,
                    source_updated_at,
                    content,
                ),
            )
            cursor.execute(
                "SELECT content_sha256 FROM evidence.graph_assertions WHERE assertion_key = %s",
                (assertion,),
            )
            if cursor.fetchone()[0] != content:
                raise ValueError("Immutable structural graph assertion conflict")
            written += 1
    return written


def project_rename_edges(
    scan: "RepositoryScan", evidence: "ScanEvidence", previous_commit: str | None
) -> tuple[GraphCodeEdge, ...]:
    """M-16: 'file_renamed' edges (old path -> new path) between the previous and this scan.

    Uses Git rename detection on the two pinned commits. If they cannot be compared
    (e.g. a shallow clone without the previous commit), no rename edges are produced;
    the scan itself is unaffected.
    """
    if (
        not previous_commit
        or previous_commit == scan.commit_sha
        or len(previous_commit) != 40
        or not all(c in "0123456789abcdef" for c in previous_commit)
    ):
        return ()
    from app.code_intelligence.impact import _diff_names

    try:
        changes = _diff_names(scan.repo_root, previous_commit, scan.commit_sha)
    except (ValueError, OSError, subprocess.SubprocessError):
        return ()
    current = {file.path for file in scan.files}
    edges: list[GraphCodeEdge] = []
    for change, old, new in changes:
        if change.startswith("R") and old and new and old != new and new in current:
            edges.append(
                GraphCodeEdge(
                    GraphCodeNode(evidence.item_key, "file", old),
                    GraphCodeNode(evidence.item_key, "file", new),
                    "file_renamed",
                )
            )
    return tuple(edges)


def latest_scanned_commit(connection_string: str, item_key: str) -> str | None:
    """The commit of the latest stored scan for this repository item, read before ingesting."""
    with psycopg.connect(connection_string, connect_timeout=10) as connection:
        row = connection.execute(
            "SELECT latest_revision_id FROM evidence.source_items WHERE item_key = %s",
            (item_key,),
        ).fetchone()
    return row[0] if row else None


def persist_code_graph(
    scan: "RepositoryScan",
    evidence: "ScanEvidence",
    connection_string: str,
    approved_ado_instance: str | None = None,
    declared_dependencies: tuple["DependencyLink", ...] = (),
    approved_targets: dict[str, "ScanEvidence"] | None = None,
    previous_commit: str | None = None,
) -> int:
    """Upsert typed code graph assertions with immutable provenance in one transaction."""
    edges = (
        *project_scan_edges(scan, evidence),
        *project_rename_edges(scan, evidence, previous_commit),
        *project_declared_dependencies(
            scan, evidence, declared_dependencies, approved_targets or {}
        ),
    )
    asserted_at = evidence.source_updated_at.astimezone(UTC)
    proof_revision = evidence.revision
    with (
        psycopg.connect(connection_string, connect_timeout=10) as connection,
        connection.cursor() as cursor,
    ):
        ado_commits: list[str] = []
        if approved_ado_instance:
            cursor.execute(
                """
                SELECT item_key FROM evidence.source_items
                WHERE provider = 'azure_devops' AND source_instance = %s
                  AND project_id = %s AND repository_id = %s
                  AND kind = 'commit' AND lower(external_id) = %s
                  AND latest_revision_id IS NOT NULL
                ORDER BY item_key
                """,
                (
                    approved_ado_instance,
                    evidence.identity.project_id,
                    evidence.identity.repository_id,
                    proof_revision,
                ),
            )
            ado_commits = [row[0] for row in cursor.fetchall()]
        if len(ado_commits) > 1:
            # Multiple approved ADO organizations may reuse project/repo IDs.
            # Avoid assigning a cross-tenant relationship without a resolved source.
            raise ValueError("Ambiguous ADO commit scope; graph bridge requires source identity")
        if ado_commits:
            observed_commit = GraphCodeNode(ado_commits[0], "commit", proof_revision)
            edges = (
                *edges,
                *(
                    GraphCodeEdge(observed_commit, edge.target, edge.kind)
                    for edge in edges
                    if edge.kind == "commit_file"
                ),
            )

        nodes: dict[str, GraphCodeNode] = {}
        for edge in edges:
            nodes[edge.source.key()] = edge.source
            nodes[edge.target.key()] = edge.target
        for key, node in nodes.items():
            cursor.execute(
                """
                INSERT INTO evidence.graph_nodes
                (node_key, anchor_item_key, node_kind, native_id)
                VALUES (%s, %s, %s, %s)
                ON CONFLICT (node_key) DO NOTHING
                """,
                (key, node.anchor, node.kind, node.native_id),
            )
            cursor.execute(
                """
                SELECT anchor_item_key, node_kind, native_id
                FROM evidence.graph_nodes WHERE node_key = %s
                """,
                (key,),
            )
            if cursor.fetchone() != (node.anchor, node.kind, node.native_id):
                raise ValueError("Graph node collision with immutable source identity")

        for edge in edges:
            relation_key = _hash(
                edge.source.key(),
                edge.target.key(),
                edge.kind.title().replace("_", ""),
                evidence.item_key,
            )
            assertion = _hash(relation_key, proof_revision)
            # Same source revision must never change an existing assertion.
            canonical_time = asserted_at.strftime("%Y-%m-%dT%H:%M:%S.%f") + "0+00:00"
            content = _hash(assertion, "Explicit", "Active", canonical_time, "")
            cursor.execute(
                """
                INSERT INTO evidence.graph_assertions (
                    assertion_key, relationship_key, from_node_key, to_node_key,
                    relation_type, classification, assertion_state,
                    provenance_item_key, provenance_revision_id,
                    source_asserted_at, content_sha256
                ) VALUES (
                    %s, %s, %s, %s, %s, 'explicit', 'active', %s, %s, %s, %s
                ) ON CONFLICT (assertion_key) DO NOTHING
                """,
                (
                    assertion,
                    relation_key,
                    edge.source.key(),
                    edge.target.key(),
                    edge.kind,
                    evidence.item_key,
                    proof_revision,
                    asserted_at,
                    content,
                ),
            )
            cursor.execute(
                "SELECT content_sha256 FROM evidence.graph_assertions WHERE assertion_key = %s",
                (assertion,),
            )
            if cursor.fetchone()[0] != content:
                raise ValueError("Immutable structural graph assertion conflict")
    return len(edges)
