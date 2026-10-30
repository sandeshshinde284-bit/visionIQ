"""Evidence-backed dependency links across files, projects and approved repositories.

Only explicit imports, project references and declared Git source URLs produce
links. Ambiguous/unmatched references stay unresolved; no inferred runtime
blast radius or ticket-fix claim is produced.
"""

import json
import posixpath
import re
import tomllib
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import PurePosixPath
from urllib.parse import urlsplit

from app.code_intelligence.scanner import RepositoryScan


@dataclass(frozen=True)
class DependencyLink:
    source_repo: str
    source_commit: str
    source_path: str
    target_repo: str | None
    target_commit: str | None
    target_path: str | None
    declared_reference: str
    kind: str
    status: str
    evidence_line: int | None = None


@dataclass(frozen=True)
class ApprovedRepository:
    repo_id: str
    origin_url: str
    scan: RepositoryScan


def _normalized_remote(raw: str) -> str | None:
    """Normalize non-credential HTTPS Git URL; no fuzzy repo-name matching."""
    value = raw.strip().removeprefix("git+")
    split = urlsplit(value)
    if split.scheme != "https" or not split.hostname or split.username or split.password:
        return None
    if split.query or split.fragment or split.port:
        return None
    return f"https://{split.hostname.lower()}{split.path.rstrip('/').removesuffix('.git')}".lower()


def _git_url_candidates(path: str, content: str) -> tuple[str, ...]:
    try:
        if path.endswith(".json") or PurePosixPath(path).name == "package.json":
            data = json.loads(content)
            if not isinstance(data, dict):
                return ()
            deps = [
                *data.get("dependencies", {}).values(),
                *data.get("devDependencies", {}).values(),
            ]
            return tuple(x for x in deps if isinstance(x, str) and "https://" in x)
        if path.endswith(".toml"):
            parsed = tomllib.loads(content)
            deps = parsed.get("project", {}).get("dependencies", [])
            return tuple(x for x in deps if isinstance(x, str) and "https://" in x)
        if PurePosixPath(path).name.startswith("requirements"):
            return tuple(
                line.strip()
                for line in content.splitlines()
                if "git+https://" in line and not line.lstrip().startswith("#")
            )
    except (ValueError, TypeError, AttributeError, KeyError):
        return ()
    return ()


def _extract_remote(candidate: str) -> str | None:
    # A URL must be an explicit part of the declaration; do not guess from names.
    match = re.search(r"(?:git\+)?https://[^\s;\"'<>]+", candidate)
    if not match:
        return None
    token = match.group(0).split("@", maxsplit=1)[0] if "@v" in match.group(0) else match.group(0)
    # Strip a pip version suffix only when it follows a .git URL.
    token = re.split(r"\.git@", token, maxsplit=1)[0]
    return _normalized_remote(token)


def _python_targets(path: str, reference: str) -> tuple[str, ...]:
    if reference.startswith("."):
        dots = len(reference) - len(reference.lstrip("."))
        parts = path.split("/")[:-1]
        if len(parts) < dots - 1:
            return ()
        parent = parts[: len(parts) - dots + 1] if dots > 1 else parts
        module = ".".join([*parent, reference[dots:]]) if reference[dots:] else ".".join(parent)
    else:
        module = reference
    base = module.replace(".", "/")
    return (f"{base}.py", f"{base}/__init__.py") if base else ()


def resolve_dependencies(
    source: ApprovedRepository,
    approved_repositories: tuple[ApprovedRepository, ...] = (),
    read_blob: object | None = None,
) -> tuple[DependencyLink, ...]:
    """Resolve only explicit evidence against the committed file inventories.

    read_blob(path) must return the UTF-8 content of that *committed* file.
    It is only needed for manifests; scanner analyses already contain imports.
    """
    file_paths = {item.path for item in source.scan.files}
    output: list[DependencyLink] = []
    for analysis in source.scan.analyses:
        for ref in analysis.imports:
            if not analysis.path.endswith(".py"):
                # Other languages have namespaces that aren't resolvable to
                # source files without an approved language-specific strategy.
                continue
            matches = [p for p in _python_targets(analysis.path, ref.target) if p in file_paths]
            output.append(
                DependencyLink(
                    source_repo=source.repo_id,
                    source_commit=source.scan.commit_sha,
                    source_path=analysis.path,
                    target_repo=source.repo_id if len(matches) == 1 else None,
                    target_commit=source.scan.commit_sha if len(matches) == 1 else None,
                    target_path=matches[0] if len(matches) == 1 else None,
                    declared_reference=ref.target,
                    kind="PYTHON_IMPORT",
                    status="RESOLVED_EXPLICIT" if len(matches) == 1 else "DECLARED_UNRESOLVED",
                    evidence_line=ref.line,
                )
            )

    if read_blob is None:
        return tuple(output)

    by_url = {
        key: target
        for target in approved_repositories
        if (key := _normalized_remote(target.origin_url)) is not None
    }
    for file in source.scan.files:
        if not (file.path.endswith(".csproj") or file.is_manifest):
            continue
        try:
            content = read_blob(file.path)
        except (OSError, UnicodeError, ValueError):
            continue

        if file.path.endswith(".csproj"):
            try:
                root = ET.fromstring(content)
            except ET.ParseError:
                continue
            for element in root.iter():
                if element.tag.rsplit("}", maxsplit=1)[-1] != "ProjectReference":
                    continue
                declared = element.attrib.get("Include", "")
                if not declared or posixpath.isabs(declared):
                    continue
                target = posixpath.normpath(
                    posixpath.join(posixpath.dirname(file.path), declared.replace("\\", "/"))
                )
                valid = not target.startswith("../") and target in file_paths
                output.append(
                    DependencyLink(
                        source_repo=source.repo_id,
                        source_commit=source.scan.commit_sha,
                        source_path=file.path,
                        target_repo=source.repo_id if valid else None,
                        target_commit=source.scan.commit_sha if valid else None,
                        target_path=target if valid else None,
                        declared_reference=declared,
                        kind="PROJECT_REFERENCE",
                        status="RESOLVED_EXPLICIT" if valid else "DECLARED_UNRESOLVED",
                    )
                )

        for declaration in _git_url_candidates(file.path, content):
            remote = _extract_remote(declaration)
            if remote is None:
                continue
            target = by_url.get(remote)
            if target is None or target.repo_id == source.repo_id:
                continue
            output.append(
                DependencyLink(
                    source_repo=source.repo_id,
                    source_commit=source.scan.commit_sha,
                    source_path=file.path,
                    target_repo=target.repo_id,
                    target_commit=target.scan.commit_sha,
                    target_path=None,
                    declared_reference=remote,
                    kind="DECLARED_GIT_DEPENDENCY",
                    status="DECLARED_TARGET_REPOSITORY",
                )
            )
    return tuple(output)
