# VisionIQ Database Foundation

PostgreSQL 16 plus pgvector. Python SQLAlchemy and Alembic provide the single schema migration system. The .NET service does not require EF Core solely for schema migrations.

| Schema | Runtime writer | Other runtime |
| --- | --- | --- |
| workflow | .NET API | read-only |
| evidence | .NET API/ingestor | read-only |
| knowledge | Python Intelligence | read-only |

All schema objects are created by a separate admin/migration identity. Runtime identities do not have CREATE permission. Future business-table migrations are added to the same Alembic chain with explicit schema qualification.

## Local setup

1. Copy .env.example to .env if necessary. Set DB_LOCAL_PASSWORD, DB_API_PASSWORD and DB_INTELLIGENCE_PASSWORD to three different nonempty secrets. Do not change DB_LOCAL_PASSWORD on an existing volume unless you also rotate the actual PostgreSQL password.
2. Run: powershell -ExecutionPolicy Bypass -File .\scripts\use-db.ps1 local
3. Run: docker compose up --build -d
4. Run: docker compose ps -a
5. Check logs: docker compose logs --tail=100 db-migrate postgres

The one-shot db-migrate container runs after PostgreSQL becomes healthy and before the API containers start. It applies Alembic migrations, creates/updates runtime roles, grants table/sequence permissions and tests permitted and rejected writes. It is rerunnable on an existing volume. Do NOT delete Docker volumes to apply migrations.

## Running migrations directly

From the repository root after installing root requirements.txt and configuring the local .env:

    python -m db.manage migrate
    python -m db.manage roles
    python -m db.manage verify

Or run all steps: python -m db.manage all

To inspect migration history: python -m alembic -c db/alembic.ini history

To opt into the real PostgreSQL pytest role test:

    $env:VISIONIQ_DB_INTEGRATION = '1'
    .\.venv\Scripts\python.exe -m pytest tests/intelligence/test_database_foundation.py
    Remove-Item Env:\VISIONIQ_DB_INTEGRATION

Existing db/init SQL only executes on a brand-new Docker volume. Alembic is used for subsequent revisions; the first migration is safe when the three schemas and vector extension already exist.

## Security and scope

DB_LOCAL_USER remains the local database owner/admin; do not pass its password to the .NET or Python runtime containers. Database roles are independent and have SELECT on every service schema, but INSERT/UPDATE/DELETE only on owned schemas. Default grants apply to future objects created by DB_LOCAL_USER. Cloud SQL/Google Cloud IAM, Cloud SQL roles and deployment are not configured by this local-only command; coordinate them with FND-04. DB_TARGET=cloud is intentionally refused.
