#!/usr/bin/env python3
"""Optional LEARN 08-13 checks against the *actual* VisionIQ CLIs and local DB.

Invoked by learn_e2e.py; a second invocation with 'probe' runs LRN-09 retrieval
in a controlled Python process. No ADO writes, no merge, no source modification.
A unit/integration result is never represented as full story acceptance.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
INTELLIGENCE = ROOT / 'src' / 'VisionIQ.Intelligence'


STORY_NAMES = {
    8: 'search index and published snapshot',
    9: 'hybrid retrieval',
    10: 'evidence-backed lessons',
    11: 'knowledge-base readiness',
    12: 'PR event ingestion',
    13: 'proof-driven lesson changes',
}


def selected_stories(scope: str) -> set[int]:
    if scope == '1-7':
        return set(range(1, 8))
    if scope == '8-13':
        return set(range(8, 14))
    if scope == '1-13':
        return set(range(1, 14))
    if scope == '14':
        return {14}
    match = re.fullmatch(r'LRN-(0[1-9]|1[0-4])', scope, re.IGNORECASE)
    if match:
        return {int(match.group(1))}
    raise ValueError('Scope must be 1-7, 8-13, 1-13, 14, or LRN-01..LRN-14')


def needs_foundation(scope: str) -> bool:
    return any(n <= 13 for n in selected_stories(scope))


def needs_knowledge(scope: str) -> bool:
    return any(8 <= n <= 13 for n in selected_stories(scope))


def add_blocked(results: list, add, story: int, reason: str) -> None:
    add(results, f'LRN-{story:02d} {STORY_NAMES[story]}', 'BLOCKED', reason)


def intelligence_env(env: dict, run_dir: Path) -> dict:
    prepared = dict(env)
    prepared['PYTHONPATH'] = str(INTELLIGENCE) + os.pathsep + prepared.get('PYTHONPATH', '')
    prepared['VISIONIQ_DB_INTEGRATION'] = '1'
    prepared['VISIONIQ_BENCHMARK_DIR'] = str(run_dir / 'benchmark')
    Path(prepared['VISIONIQ_BENCHMARK_DIR']).mkdir(parents=True, exist_ok=True)
    prepared['DB_TARGET'] = 'local'
    prepared['DB_SSL_MODE'] = 'disable'
    return prepared


def _anchor_id(fixture: dict) -> int:
    """Task or Bug that actually carries the selected native PR ArtifactLink."""
    return int(fixture.get('linked_work_item_id') or fixture.get('bug_id') or fixture['task_id'])


def source_key(db, fixture: dict) -> str | None:
    with db.cursor() as cur:
        cur.execute('''SELECT item_key FROM evidence.source_items
                       WHERE provider='azure_devops' AND rtrim(source_instance, '/')=%s
                       AND project_id=%s AND kind='work_item_current' AND external_id=%s
                         AND deleted_at IS NULL AND latest_revision_id IS NOT NULL''',
                    (fixture['organization_url'].rstrip('/'), fixture['project'], str(_anchor_id(fixture))))
        row = cur.fetchone()
    return str(row[0]) if row else None


def index_snapshot(db, fixture: dict) -> tuple[str, str, int, int] | None:
    """Published, current-revision fixture index with complete embeddings only."""
    with db.cursor() as cur:
        cur.execute('''SELECT sp.snapshot_id, ie.model_id,
                              count(DISTINCT c.chunk_key), count(DISTINCT ce.chunk_key)
                       FROM knowledge.snapshot_pointer sp
                       JOIN knowledge.snapshot_entries se ON se.snapshot_id=sp.snapshot_id
                       JOIN evidence.source_items i ON i.item_key=se.item_key
                       JOIN knowledge.index_entries ie ON ie.entry_key=se.entry_key
                       LEFT JOIN knowledge.index_chunks c ON c.entry_key=ie.entry_key
                       LEFT JOIN knowledge.chunk_embeddings ce
                         ON ce.chunk_key=c.chunk_key AND ce.model_id=ie.model_id
                       WHERE i.provider='azure_devops' AND rtrim(i.source_instance, '/')=%s
                         AND i.project_id=%s AND i.kind='work_item_current'
                         AND i.external_id=%s AND i.deleted_at IS NULL
                         AND ie.provider_revision_id=i.latest_revision_id
                       GROUP BY sp.snapshot_id, ie.model_id''',
                    (fixture['organization_url'].rstrip('/'), fixture['project'], str(_anchor_id(fixture))))
        row = cur.fetchone()
        return (str(row[0]), str(row[1]), int(row[2]), int(row[3])) if row else None


def lesson_state(db, fixture: dict) -> tuple[int, int]:
    """Version + decision counts in only the selected source/project/repository scope."""
    with db.cursor() as cur:
        cur.execute('''SELECT count(*) FROM knowledge.lesson_revisions r
                       JOIN knowledge.lesson_patterns p ON p.pattern_id=r.pattern_id
                       WHERE rtrim(p.source_instance, '/')=%s AND p.project_id=%s AND p.repository_id=%s''',
                    (fixture['organization_url'].rstrip('/'), fixture['project'], fixture['repository_id']))
        revisions = int(cur.fetchone()[0])
        cur.execute('''SELECT count(*) FROM knowledge.lesson_decisions d
                       JOIN knowledge.lesson_observations o ON o.observation_key=d.observation_key
                       WHERE rtrim(o.source_instance, '/')=%s AND o.project_id=%s AND o.repository_id=%s''',
                    (fixture['organization_url'].rstrip('/'), fixture['project'], fixture['repository_id']))
        decisions = int(cur.fetchone()[0])
    return revisions, decisions


def draft_lesson_observations(db, fixture: dict) -> int:
    """A draft PR must not be promoted to a completed historical fix lesson observation."""
    with db.cursor() as cur:
        cur.execute('''SELECT count(*) FROM knowledge.lesson_observations
                       WHERE rtrim(source_instance, '/')=%s AND project_id=%s AND repository_id=%s
                         AND ticket_id=%s AND fix_kind='pull_request' AND fix_id=%s''',
                    (fixture['organization_url'].rstrip('/'), fixture['project'], fixture['repository_id'],
                     str(_anchor_id(fixture)), str(fixture['pull_request_id'])))
        return int(cur.fetchone()[0])


def historical_lesson(db, fixture: dict, ticket_ids: list[str]) -> bool:
    """All requested distinct ADO tickets must support the SAME learned pattern."""
    if len(set(ticket_ids)) < 2:
        return False
    with db.cursor() as cur:
        cur.execute('''SELECT count(DISTINCT o.ticket_id) AS matched
                       FROM knowledge.lesson_patterns p
                       JOIN knowledge.lesson_memberships m ON m.pattern_id=p.pattern_id
                       JOIN knowledge.lesson_observations o ON o.observation_key=m.observation_key
                       JOIN knowledge.lesson_revisions r
                         ON r.pattern_id=p.pattern_id AND r.revision=p.latest_revision
                       WHERE rtrim(p.source_instance, '/')=%s AND p.project_id=%s AND p.repository_id=%s
                         AND o.ticket_id=ANY(%s::text[]) AND r.cause_status='unverified'
                       GROUP BY p.pattern_id
                       HAVING count(DISTINCT o.ticket_id)=%s''',
                    (fixture['organization_url'].rstrip('/'), fixture['project'], fixture['repository_id'],
                     ticket_ids, len(set(ticket_ids))))
        return cur.fetchone() is not None


def run_pytest(path: str, env: dict, run_dir: Path, runner, name: str, results: list) -> bool:
    """Reject zero-collected and skipped suites even when pytest exits zero."""
    add = runner.result_add
    target = ROOT / path
    if not target.is_file():
        add_blocked(results, add, int(re.search(r'LRN-(\d+)', name).group(1)), f'Missing {path}')
        return False
    log = run_dir / (Path(path).stem + '.log')
    rc = runner.run_command([sys.executable, '-m', 'pytest', '-q', '-r', 'a', str(target)], env, log)
    message = log.read_text(encoding='utf-8', errors='replace') if log.is_file() else ''
    match = re.search(r'(\d+) passed', message)
    passed = int(match.group(1)) if match else 0
    clean = rc == 0 and passed > 0 and not re.search(r'\d+ (?:skipped|failed|error)', message)
    add(results, name, 'PASS' if clean else 'FAIL',
        f'pytest exit={rc}, passed={passed}; {log.name}; database fixture tests, not live external acceptance')
    return clean


def run_dotnet_pr_event_tests(env: dict, run_dir: Path, runner, results: list) -> bool:
    add = runner.result_add
    project = ROOT / 'tests' / 'VisionIQ.Ingestor.Tests' / 'VisionIQ.Ingestor.Tests.csproj'
    required = [ROOT / 'tests' / 'VisionIQ.Ingestor.Tests' / x
                for x in ('PullRequestEventTests.cs', 'WebhookHttpTests.cs')]
    if not project.is_file() or not all(p.is_file() for p in required):
        add_blocked(results, add, 12, 'PR-event / HTTP test files or test project unavailable in this checkout')
        return False
    log = run_dir / 'lrn12-dotnet-tests.log'
    rc = runner.run_command(['dotnet', 'test', str(project), '--filter',
                             'FullyQualifiedName~PullRequestEventTests|FullyQualifiedName~WebhookHttpTests',
                             '--verbosity', 'quiet'], env, log)
    content = log.read_text(encoding='utf-8', errors='replace') if log.exists() else ''
    matches = re.findall(r'Passed:\s*(\d+)', content)
    passed = sum(int(i) for i in matches)
    ok = rc == 0 and passed > 0 and not re.search(r'Failed:\s*[1-9]', content)
    add(results, 'LRN-12 PR lifecycle and HTTP contract tests', 'PASS' if ok else 'FAIL',
        f'dotnet exit={rc}, passed={passed}; {log.name} (synthetic service-hook tests)')
    return ok


def run_stage_b(args: Any, env: dict, run_dir: Path, fixture: dict,
                results: list, runner: Any) -> None:
    """Run real CLIs + DB assertions, record uncovered user-level acceptance as BLOCKED."""
    add = runner.result_add
    chosen = selected_stories(args.scope) & set(range(8, 14))
    if not chosen:
        return
    if env.get('INGESTION_OUTPUT_MODE') != 'canonical':
        for n in sorted(chosen):
            add_blocked(results, add, n, 'Canonical PostgreSQL + private GCS required; JSONL is insufficient')
        return
    for secret in ('DB_INTELLIGENCE_USER', 'DB_INTELLIGENCE_PASSWORD', 'GCS_EVIDENCE_BUCKET'):
        if not env.get(secret):
            for n in sorted(chosen):
                add_blocked(results, add, n, f'Missing {secret} (never supply secrets in command arguments)')
            return
    pyenv = intelligence_env(env, run_dir)
    if not args.approve_knowledge_writes:
        for n in sorted(chosen):
            add_blocked(results, add, n, 'Knowledge writes not approved. Interactive: type KNOWLEDGE; noninteractive: -ApproveKnowledgeWrites')
        return

    # Index first as a dependency for retrieval even when LRN-09 is selected individually.
    need_index = 8 in chosen or 9 in chosen or 10 in chosen or 11 in chosen or 13 in chosen
    indexed = False
    if need_index:
        if not (INTELLIGENCE / 'app' / 'knowledge' / 'index_cli.py').is_file():
            add_blocked(results, add, 8, 'index_cli.py missing from local checkout')
        else:
            rc = runner.run_command([sys.executable, '-m', 'app.knowledge.index_cli'],
                                    pyenv, run_dir / 'lrn08-index.log')
            try:
                with runner.canonical_connection(env) as db:
                    row = index_snapshot(db, fixture)
                indexed = rc == 0 and row is not None and row[2] > 0 and row[2] == row[3]
                add(results, 'LRN-08 actual index and published fixture snapshot', 'PASS' if indexed else 'FAIL',
                    f'CLI exit={rc}; fixture chunks/vectors={(row[2], row[3]) if row else None}; '
                    f'model={row[1] if row else "none"}; see lrn08-index.log')
            except Exception as exc:
                add(results, 'LRN-08 actual index and published fixture snapshot', 'FAIL',
                    f'Index assertion unavailable ({type(exc).__name__}); see lrn08-index.log')
        if 8 in chosen and indexed:
            try:
                output = run_dir / 'lrn08-search-probe.json'
                with runner.canonical_connection(env) as db:
                    fixture_key = source_key(db, fixture)
                if not fixture_key:
                    raise ValueError('Fixture canonical work item missing')
                command = [
                    sys.executable, str(Path(__file__).resolve()), 'probe-index',
                    '--item-key', str(fixture_key),
                    '--output', str(output),
                ]
                rc = runner.run_command(command, pyenv, run_dir / 'lrn08-search-probe.log')
                probe = json.loads(output.read_text(encoding='utf-8')) if output.is_file() else {}
                ok = (rc == 0 and all(probe.get(k) is True for k in
                       ('keyword_found', 'vector_found', 'wrong_scope_hidden')))
                add(results, 'LRN-08 real keyword + vector retrieval of indexed fixture',
                    'PASS' if ok else 'FAIL',
                    f"keyword={probe.get('keyword_found')}, vector={probe.get('vector_found')}, "
                    f"wrong-scope excluded={probe.get('wrong_scope_hidden')}; "
                    f"keyword hits={probe.get('keyword_hits')}, vector hits={probe.get('vector_hits')}; "
                    "indexed canonical item, not a labelled quality benchmark")
            except Exception as exc:
                add(results, 'LRN-08 real keyword + vector retrieval of indexed fixture',
                    'FAIL', f"Search acceptance error ({type(exc).__name__})")
        if 'test' == pyenv.get('EMBEDDING_PROVIDER'):
            add(results, 'LRN-08 real Gemini embedding acceptance', 'BLOCKED',
                'EMBEDDING_PROVIDER=test uses hashing; real Vertex/Gemini accuracy and latency not validated')
        else:
            add(results, 'LRN-08 real embedding quality and cost benchmark', 'BLOCKED',
                'Needs labelled, approved corpus and real Vertex measurements (not just embedding success)')

    if 9 in chosen:
        if not indexed:
            add_blocked(results, add, 9, 'Cannot evaluate retrieval without a published indexed fixture')
        else:
            try:
                with runner.canonical_connection(env) as db:
                    key = source_key(db, fixture)
                if not key:
                    raise ValueError('Fixture canonical linked work item is missing')
                output = run_dir / 'lrn09-probe.json'
                command = [sys.executable, str(Path(__file__).resolve()), 'probe',
                           '--item-key', key, '--org', fixture['organization_url'],
                           '--project', fixture['project'], '--output', str(output)]
                rc = runner.run_command(command, pyenv, run_dir / 'lrn09-probe.log')
                probe = json.loads(output.read_text(encoding='utf-8')) if output.is_file() else {}
                ok = rc == 0 and all(probe.get(key) is True for key in ('found', 'citation', 'wrong_scope_hidden', 'live_check_denies'))
                add(results, 'LRN-09 retrieval, citation, scope and live-check hook', 'PASS' if ok else 'FAIL',
                    f'probe exit={rc}, fixture found={probe.get("found")}, citation={probe.get("citation")}, '
                    f'wrong-scope denied={probe.get("wrong_scope_hidden")}, live-check denied={probe.get("live_check_denies")}')
            except Exception as exc:
                add(results, 'LRN-09 retrieval, citation, scope and live-check hook', 'FAIL',
                    f'Retrieval probe failed ({type(exc).__name__}); see lrn09-probe.log')
        add(results, 'LRN-09 end-user live authorization and labelled retrieval quality', 'BLOCKED',
            'Requires approved delegated identity, provider-side live ACL check and real labelled queries; probe uses controlled hooks')

    if 10 in chosen or 13 in chosen:
        if not (INTELLIGENCE / 'app' / 'knowledge' / 'lessons_cli.py').is_file():
            for n in (10, 13):
                if n in chosen:
                    add_blocked(results, add, n, 'lessons_cli.py missing')
        else:
            rc = runner.run_command([sys.executable, '-m', 'app.knowledge.lessons_cli'],
                                    pyenv, run_dir / 'lrn10-lessons-1.log')
            try:
                with runner.canonical_connection(env) as db:
                    before = lesson_state(db, fixture)
                    draft_count = draft_lesson_observations(db, fixture)
                if fixture.get('linked_work_item_type', 'Bug' if fixture.get('bug_id') else 'Task') != 'Bug':
                    add(results, 'LRN-10 unmerged PR excluded from completed-fix lessons', 'SKIP',
                        'Linked work item is a Task; defect-only lesson qualification needs a Bug fixture')
                elif fixture.get('pr_status') == 'completed':
                    add(results, 'LRN-10 unmerged PR excluded from completed-fix lessons', 'SKIP',
                        'Selected PR is completed; this negative assertion requires an active or abandoned PR')
                else:
                    add(results, 'LRN-10 unmerged PR excluded from completed-fix lessons',
                        'PASS' if rc == 0 and draft_count == 0 else 'FAIL',
                        f'lessons CLI exit={rc}; {fixture.get("pr_lifecycle", "unknown")} PR lesson observations={draft_count}; expected 0')
                ids = [s.strip() for s in (args.historical_bug_ids or '').split(',') if s.strip()]
                if 10 in chosen:
                    if len(set(ids)) < 2 or not all(x.isdigit() for x in ids):
                        add_blocked(results, add, 10, 'Provide -HistoricalBugIds "101,102" for two real merged-fix bugs touching overlapping files')
                    else:
                        with runner.canonical_connection(env) as db:
                            complete = historical_lesson(db, fixture, ids)
                        add(results, 'LRN-10 repeated merged defects form an evidence-backed lesson',
                            'PASS' if complete else 'FAIL', f'Same pattern contains all {len(set(ids))} selected defect IDs')
                rc2 = runner.run_command([sys.executable, '-m', 'app.knowledge.lessons_cli'],
                                         pyenv, run_dir / 'lrn10-lessons-2.log')
                with runner.canonical_connection(env) as db:
                    after = lesson_state(db, fixture)
                add(results, 'LRN-10/13 lesson replay does not create new versions or decisions',
                    'PASS' if rc2 == 0 and after == before else 'FAIL',
                    f'CLI exit={rc2}; scoped revisions/decisions before={before} after={after}')
            except Exception as exc:
                add(results, 'LRN-10/13 lesson database checks', 'FAIL', f'{type(exc).__name__}; see lrn10-lessons-*.log')

    if 11 in chosen:
        ok = run_pytest('tests/intelligence/test_kb_readiness_db.py', pyenv, run_dir, runner,
                        'LRN-11 local database readiness and security fixtures', results)
        artifact = run_dir / 'benchmark' / 'kb-readiness-results.json'
        if ok:
            try:
                data = json.loads(artifact.read_text(encoding='utf-8'))
                valid = (isinstance(data, list) and len(data) >= 4 and all(
                    item.get('result') == 'passed' and str(item.get('snapshot_id', '')).startswith('kb-') for item in data))
                add(results, 'LRN-11 per-scenario evidence artifact', 'PASS' if valid else 'FAIL',
                    f'{len(data) if isinstance(data, list) else 0} detailed case records in {artifact.name}')
            except (OSError, ValueError):
                add(results, 'LRN-11 per-scenario evidence artifact', 'FAIL', 'Missing/invalid kb-readiness-results.json')
        add(results, 'LRN-11 real provider revocation and availability', 'BLOCKED',
            'Database fixtures do not prove live ADO/Confluence access changes or real outage recovery')

    if 12 in chosen:
        run_dotnet_pr_event_tests(pyenv, run_dir, runner, results)
        add(results, 'LRN-12 live ADO PR service-hook delivery', 'BLOCKED',
            'Requires an approved running webhook host and actual PR created/reviewed/merged/abandoned transitions; one historical PR snapshot is insufficient')

    if 13 in chosen:
        ok = run_pytest('tests/intelligence/test_lrn13_db.py', pyenv, run_dir, runner,
                        'LRN-13 local PR-event/revert proof tests', results)
        artifact = run_dir / 'benchmark' / 'lesson-replay.json'
        if ok:
            try:
                replay = json.loads(artifact.read_text(encoding='utf-8'))
                revisions = replay.get('history', {}).get('revisions', [])
                good = [x.get('status') for x in revisions] == ['candidate', 'disputed'] and all(
                    x.get('cause') == 'unverified' for x in revisions)
                add(results, 'LRN-13 candidate-to-disputed append-only lesson history', 'PASS' if good else 'FAIL',
                    'No unreviewed cause promoted; revisions preserved' if good else 'Missing expected replay history')
            except (OSError, ValueError):
                add(results, 'LRN-13 candidate-to-disputed append-only lesson history', 'FAIL',
                    'Invalid/missing lesson-replay.json')
        add(results, 'LRN-13 real PR lifecycle progression', 'BLOCKED',
            'Needs controlled completed PR and revert progression, not just one historical PR snapshot or synthetic DB scenario')


def probe_index(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description='Verify LRN-08 keyword and vector search')
    parser.add_argument('--item-key', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args(argv)
    import psycopg
    from psycopg.conninfo import make_conninfo
    from app.knowledge import search
    from app.knowledge.embeddings import from_environment

    conninfo = make_conninfo(
        '', host=os.environ['DB_HOST'], port=int(os.environ.get('DB_PORT', '5432')),
        dbname=os.environ['DB_NAME'], user=os.environ['DB_INTELLIGENCE_USER'],
        password=os.environ['DB_INTELLIGENCE_PASSWORD'],
        sslmode=os.environ.get('DB_SSL_MODE', 'disable'),
    )
    report = {
        'keyword_found': False, 'vector_found': False, 'wrong_scope_hidden': False,
        'keyword_hits': 0, 'vector_hits': 0, 'model': '',
    }
    try:
        with psycopg.connect(conninfo) as conn:
            with conn.cursor() as cur:
                cur.execute("""
                    SELECT c.content, c.acl_principals, ie.model_id
                    FROM knowledge.snapshot_pointer sp
                    JOIN knowledge.snapshot_entries se ON se.snapshot_id=sp.snapshot_id
                    JOIN knowledge.index_entries ie ON ie.entry_key=se.entry_key
                    JOIN knowledge.index_chunks c ON c.entry_key=ie.entry_key
                    JOIN evidence.source_items i ON i.item_key=se.item_key
                    JOIN evidence.source_revisions r
                      ON r.item_key=i.item_key
                     AND r.provider_revision_id=i.latest_revision_id
                    WHERE se.item_key=%s AND ie.provider_revision_id=i.latest_revision_id
                      AND i.deleted_at IS NULL AND r.acl_verified
                      AND jsonb_array_length(r.acl_principals) > 0
                    ORDER BY length(c.content) DESC, c.chunk_key LIMIT 1
                """, (args.item_key,))
                row = cur.fetchone()
            if row is None:
                raise ValueError('No current, verified published chunk')
            content, scopes, model = row
            query = str(content).strip()[:300]
            if len(query.split()) < 2:
                raise ValueError('Published chunk too short for test')
            embedder = from_environment()
            if embedder.model_id != model:
                raise ValueError('Query embedding model differs from published model')
            with conn.transaction():
                conn.execute('SET TRANSACTION READ ONLY')
                lexical = search.keyword(conn, query, list(scopes), limit=100, match='any')
                semantic = search.vector(conn, embedder, query, list(scopes), limit=100)
                wrong_scope = search.keyword(
                    conn, query, ['ado-project:https://invalid.example.test/denied'],
                    limit=100, match='any',
                )
            report = {
                'keyword_found': any(h.item_key == args.item_key for h in lexical),
                'vector_found': any(h.item_key == args.item_key for h in semantic),
                'wrong_scope_hidden': not any(h.item_key == args.item_key for h in wrong_scope),
                'keyword_hits': len(lexical), 'vector_hits': len(semantic),
                'model': model,
            }
    except (ValueError, psycopg.Error) as error:
        report['error_type'] = type(error).__name__
    Path(args.output).write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    return 0 if all(report[k] for k in
                    ('keyword_found', 'vector_found', 'wrong_scope_hidden')) else 1


def probe_retrieval(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description='Private LRN-09 retrieval probe: no raw evidence output')
    parser.add_argument('--item-key', required=True)
    parser.add_argument('--org', required=True)
    parser.add_argument('--project', required=True)
    parser.add_argument('--output', required=True)
    args = parser.parse_args(argv)
    import psycopg
    from psycopg.conninfo import make_conninfo
    from app.knowledge.embeddings import from_environment
    from app.knowledge.retrieval import retrieve

    db = make_conninfo('', host=os.environ['DB_HOST'], port=int(os.environ.get('DB_PORT','5432')),
                       dbname=os.environ['DB_NAME'], user=os.environ['DB_INTELLIGENCE_USER'],
                       password=os.environ['DB_INTELLIGENCE_PASSWORD'], sslmode=os.environ.get('DB_SSL_MODE','disable'))
    connect = lambda: psycopg.connect(db)
    embedder = from_environment()
    scope = f"ado-project:{args.org.rstrip('/')}/{args.project}"
    query = 'Ticket vending total incorrect expected fare 25 actual fare 30 payment kiosk'
    # This tests retrieval's live_check HOOK; it does not impersonate a real end-user.
    allowed = retrieve(connect, embedder, query, [scope], live_check=lambda key: key == args.item_key)
    denied = retrieve(connect, embedder, query, ['ado-project:https://dev.azure.com/nonexistent/Denied'],
                      live_check=lambda _: True)
    revoked = retrieve(connect, embedder, query, [scope], live_check=lambda _: False)
    result = next((x for x in allowed.results if x.item_key == args.item_key), None)
    proof = {'found': result is not None,
             'citation': bool(result and result.citation.get('revision') and result.citation.get('url')),
             'wrong_scope_hidden': all(x.item_key != args.item_key for x in denied.results),
             'live_check_denies': all(x.item_key != args.item_key for x in revoked.results),
             'model': embedder.model_id,
             'status': allowed.status,
             'snapshot': allowed.snapshot_id}
    Path(args.output).write_text(json.dumps(proof, indent=2) + '\n', encoding='utf-8')
    return 0 if all(proof[k] for k in ('found','citation','wrong_scope_hidden','live_check_denies')) else 1


if __name__ == '__main__':
    if len(sys.argv) > 1 and sys.argv[1] == 'probe-index':
        sys.path.insert(0, str(INTELLIGENCE))
        raise SystemExit(probe_index(sys.argv[2:]))
    if len(sys.argv) > 1 and sys.argv[1] == 'probe':
        sys.path.insert(0, str(INTELLIGENCE))
        raise SystemExit(probe_retrieval(sys.argv[2:]))
    raise SystemExit('Call this module through learn_e2e.py or with the probe subcommand')
