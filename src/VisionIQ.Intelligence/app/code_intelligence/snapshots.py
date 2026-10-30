"""Durable last-known-good scan snapshots for restart-safe parser failure isolation.

Local snapshots are an interim integration boundary until LRN-03 provides
canonical ACL-aware persistence. Never store raw source code or credentials.
"""

import hashlib
import json
import os
import tempfile
from dataclasses import asdict
from pathlib import Path

from app.code_intelligence.adapters import (
    CallReference,
    ImportReference,
    SymbolReference,
)
from app.code_intelligence.scanner import FileAnalysis, GitFile, RepositoryScan, scan_repository

SNAPSHOT_VERSION = 1
MAX_SNAPSHOT_BYTES = 12 * 1024 * 1024


def _target(cache_dir: str | Path, repo_root: str | Path) -> Path:
    root = str(Path(repo_root).resolve())
    digest = hashlib.sha256(root.encode("utf-8")).hexdigest()
    return Path(cache_dir).resolve() / f"git-scan-{digest}.json"


def save_last_good(scan: RepositoryScan, cache_dir: str | Path) -> Path:
    """Atomically preserve only a COMPLETE snapshot; never replace it with PARTIAL."""
    if scan.status != "COMPLETE":
        raise ValueError("Only complete scans can replace last-good evidence")
    directory = Path(cache_dir).resolve()
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    path = _target(directory, scan.repo_root)
    payload = json.dumps(
        {"version": SNAPSHOT_VERSION, "snapshot": asdict(scan)},
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    if len(payload) > MAX_SNAPSHOT_BYTES:
        raise ValueError("Snapshot exceeds safe size limit")
    fd, temp_name = tempfile.mkstemp(prefix=".git-scan-", suffix=".tmp", dir=directory)
    try:
        if os.name != "nt":
            os.fchmod(fd, 0o600)
        with os.fdopen(fd, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp_name, path)
    finally:
        if os.path.exists(temp_name):
            os.unlink(temp_name)
    return path


def load_last_good(cache_dir: str | Path, repo_root: str | Path) -> RepositoryScan | None:
    """Fail closed on corrupt, mismatched or unrecognized cached metadata."""
    path = _target(cache_dir, repo_root)
    if not path.is_file():
        return None
    if path.stat().st_size > MAX_SNAPSHOT_BYTES:
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        if data.get("version") != SNAPSHOT_VERSION:
            return None
        snap = data["snapshot"]
        if snap["status"] != "COMPLETE":
            return None
        if str(Path(snap["repo_root"]).resolve()) != str(Path(repo_root).resolve()):
            return None
        files = tuple(GitFile(**item) for item in snap["files"])
        analyses = tuple(
            FileAnalysis(
                path=item["path"],
                blob_sha=item["blob_sha"],
                symbol_analysis=item["symbol_analysis"],
                adapter_name=item["adapter_name"],
                adapter_version=item["adapter_version"],
                symbols=tuple(SymbolReference(**s) for s in item["symbols"]),
                imports=tuple(ImportReference(**s) for s in item["imports"]),
                calls=tuple(CallReference(**s) for s in item["calls"]),
                diagnostics=tuple(item["diagnostics"]),
            )
            for item in snap["analyses"]
        )
        return RepositoryScan(
            repo_root=snap["repo_root"],
            commit_sha=snap["commit_sha"],
            status="COMPLETE",
            files=files,
            analyses=analyses,
            errors=(),
            last_good=None,
        )
    except (OSError, KeyError, TypeError, ValueError, UnicodeError):
        return None


def scan_with_recovery(
    repository: str | Path,
    cache_dir: str | Path,
    registry: object,
) -> RepositoryScan:
    """Persist good scan; preserve it through subsequent parser failures/restarts."""
    last_good = load_last_good(cache_dir, repository)
    scan = scan_repository(repository, registry=registry, previous=last_good)
    if scan.status == "COMPLETE":
        save_last_good(scan, cache_dir)
    return scan
