"""LRN-07 review R7-01/R7-02: a fix's own changed files, as a direct graph edge.

Revision ID: 20261104_0020
Revises: 20261104_0019

Adds 'commit_changed_file' and 'pr_changed_file': from a commit or pull request directly to the
files it changed (evidence.fix_change_sets), independent of whether that exact commit was ever the
HEAD of a code scan. The existing 'commit_file' relation means "this file existed in the tree that
was scanned" and is written only for the one commit a scan happened to run against; it cannot
answer "what did this historical fix change". This is also why a
defect -> fix -> changed file -> symbol/test query does not need a 4th hop (R7-02): it goes
ticket -> commit/PR (1) -> file (2, this new edge, skipping the "every file in the tree" relation)
-> symbol/test (3), fitting the existing 3-hop limit.

File node identity is anchored to a repository's code-scan item key (app/code_intelligence/graph.py:
GraphCodeNode(scan_item_key, "file", path)), which is stable across every commit scanned for that
repository -- so this edge can be written for any historical fix once the repository has been
scanned at least once, without re-scanning that exact historical commit.
"""

from alembic import op

revision = "20261104_0020"
down_revision = "20261104_0019"
branch_labels = None
depends_on = None

_RELATIONS = (
    "'ticket_pr','pr_commit','commit_file','file_symbol',"
    "'file_dependency','repository_dependency','ticket_commit',"
    "'design_related','ci_commit','file_renamed','file_test',"
    "'tested_by','automated_by','ci_pull_request',"
    "'commit_changed_file','pr_changed_file'"
)


def upgrade() -> None:
    """Allow the two new relation types; no table shape changes."""
    op.execute(
        "ALTER TABLE evidence.graph_assertions DROP CONSTRAINT graph_assertions_relation_type_check"
    )
    op.execute(
        "ALTER TABLE evidence.graph_assertions ADD CONSTRAINT "
        f"graph_assertions_relation_type_check CHECK (relation_type IN ({_RELATIONS}))"
    )


def downgrade() -> None:
    """Block destructive rollback of relationship history."""
    raise RuntimeError(
        "Graph relationship history cannot be removed by downgrade; use a forward migration."
    )
