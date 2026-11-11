# Defect-Intelligence (VisionIQ)

VisionIQ learns from engineering history (tickets, pull requests, commits, builds) and briefs engineers
on every new defect or enhancement. AI proposes; engineers decide. Nothing is merged automatically.

This repository is a hybrid build: an ASP.NET Core 10 API and a Python (FastAPI) intelligence service, both
backed by PostgreSQL with pgvector. Google Cloud infrastructure and manual deployments exist, but the integrated source-to-Brief/DEFECT cloud acceptance is still pending.

## Layout

| Path | What it is |
| --- | --- |
| `src/VisionIQ.Api` | ASP.NET Core 10 API (port 5100) |
| `src/VisionIQ.Intelligence` | Python FastAPI service (port 8000), package `app` |
| `tests/VisionIQ.Api.Tests` | xUnit tests for the API |
| `tests/intelligence` | pytest tests for the Python service |
| `db/` | Database start-up SQL and notes |
| `scripts/` | `check-env.ps1` (machine check) and `use-db.ps1` (local or cloud database target) |
| `requirements.txt` | Python development/CI dependencies: runtime plus test tools |
| `requirements-runtime.txt` / `requirements-runtime.lock.txt` | Runtime dependencies, and their pinned, hash-verified lock used by the Docker images (regenerate the lock with `pip-compile --generate-hashes --strip-extras`) |

## Run locally (Windows PowerShell, repository root)

1. Check your machine: `powershell -ExecutionPolicy Bypass -File .\scripts\check-env.ps1`
2. Create `.env` from `.env.example` and set distinct `DB_LOCAL_PASSWORD`, `DB_API_PASSWORD` and `DB_INTELLIGENCE_PASSWORD` (never commit `.env`).
3. Select the local database: `powershell -ExecutionPolicy Bypass -File .\scripts\use-db.ps1 local`
4. Start everything (including one-time migration/role bootstrap): `docker compose up --build -d`
5. Check both services: `http://localhost:5100/health` and `http://localhost:8000/health`

Each service can also run on the host without Docker (the database still runs in Docker):

```powershell
# Python (from src/VisionIQ.Intelligence, with a virtual environment active)
python -m uvicorn app.main:app --host 127.0.0.1 --port 8000 --reload

# .NET (from the repository root)
dotnet run --project src/VisionIQ.Api
```

## Database foundation

SQLAlchemy/Alembic handles database migrations, with PostgreSQL enforcing separate .NET and Python runtime permissions. See [db/README.md](db/README.md). The bootstrap supports existing local volumes without deleting data. Cloud SQL exists in the approved development project; validate permissions, connectivity and migrations before a manual deployment.

## Checks

```powershell
dotnet build VisionIQ.sln
dotnet test VisionIQ.sln
dotnet format VisionIQ.sln --verify-no-changes

python -m pytest
python -m ruff check .
python -m ruff format --check .
```

Python 3.12 and the .NET 10 SDK are the pinned versions (`.python-version`, `global.json`).

## Current documentation

- [Start here](docs/START-HERE/README.md)
- [Single living LRN-01–13 LEARN handbook](docs/LEARN-PHASE-COMPLETE/LEARN-HANDBOOK.md)
- [Operations, manual deployment, cloud and troubleshooting](docs/OPERATIONS-AND-INFRASTRUCTURE/README.md)

LEARN is operated manually during the hackathon. Merge history or isolated fixture tests do not prove live source/cloud acceptance; automatic PR-triggered learning and Pub/Sub knowledge-change publication are deferred.
