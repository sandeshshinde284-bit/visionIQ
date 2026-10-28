# Database

PostgreSQL with pgvector. Local development uses Docker; the shared development database is Cloud SQL.
The same migrations run against both.

## Local

1. Copy `.env.example` to `.env` and set `DB_LOCAL_PASSWORD` (any password, it is only for your machine).
2. `docker compose up -d postgres`
3. `docker compose ps` shows the container as healthy.
4. `./scripts/use-db.ps1 local` points the services at it.

Start over with a clean local database (local only, this deletes its data): `docker compose down -v`

## Cloud SQL (development database)

1. Set `DB_CLOUD_INSTANCE`, `DB_CLOUD_NAME`, `DB_CLOUD_USER` and `DB_CLOUD_PASSWORD` in `.env`.
2. `gcloud auth application-default login` (your own Google account).
3. In a separate terminal, start the Cloud SQL Auth Proxy: `cloud-sql-proxy --port 5433 <project:region:instance>`
4. `./scripts/use-db.ps1 cloud` points the services at `localhost:5433`. It asks for confirmation first.

Rules for the cloud database: never run destructive migrations or reset scripts against it, and never
point every laptop at the demo database. Use a separate development database.

## Switching

`./scripts/use-db.ps1 local` or `./scripts/use-db.ps1 cloud` rewrites `DB_TARGET`, `DB_HOST`, `DB_PORT`,
`DB_NAME`, `DB_USER`, `DB_PASSWORD` and `DB_SSL_MODE` in `.env`. The services only read those variables,
so they do not need to know which database they are talking to.

## Schemas and migrations

`workflow`, `evidence` and `knowledge`, each written only by its owning service (proposed in FND-03,
decision D07). The first two are managed from .NET (EF Core), `knowledge` from Python (Alembic).
`db/init/01-extensions-and-schemas.sql` creates the extension and the empty schemas on first start.
