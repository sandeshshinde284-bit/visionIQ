# CLAUDE.md

Guidance for Claude Code when working in this repository.

## Project

**Defect-Intelligence** — hackathon project. GitHub: `sandesh-shinde_exakisc/Defect-Intelligence`.

The repo is at an early stage: only `README.md` is committed so far. Update this file with the
tech stack, build/test commands, and architecture as they are added.

## Repository layout

- `README.md` — project readme (currently UTF-16 encoded; keep or convert to UTF-8 deliberately).
- `Defect-Intelligence/` — untracked folder that contains its **own nested `.git`** repo pointing at
  the same remote. Don't commit it into the outer repo as-is (it would become an embedded repo);
  decide whether to work from the folder or the root and remove the other.

## Tooling

- **GitHub MCP** is configured in `.mcp.json` (GitHub's hosted MCP server). It reads the token from
  the `GITHUB_PERSONAL_ACCESS_TOKEN` environment variable — never hardcode tokens in repo files.
- Shared Claude Code settings live in `.claude/settings.json`; personal overrides go in
  `.claude/settings.local.json` (git-ignored).

## Conventions

- Platform is Windows; the shell available to Claude is PowerShell, with Git Bash also available.
- Branch from `main` for changes; open PRs against `main`.
- Ask before pushing, merging PRs, or other actions visible on GitHub.
