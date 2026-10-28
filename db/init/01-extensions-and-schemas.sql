-- Runs once when the local database volume is first created.
-- Cloud SQL needs the same extension and schemas; apply them there once (task FND-03).

CREATE EXTENSION IF NOT EXISTS vector;

-- Three schemas, each written only by the service that owns it (proposed in FND-03, decision D07):
--   workflow  : runs, events, advice ledger      (.NET API)
--   evidence  : source records, links, graph      (.NET Ingestor)
--   knowledge : chunks, embeddings, lessons       (Python service)
CREATE SCHEMA IF NOT EXISTS workflow;
CREATE SCHEMA IF NOT EXISTS evidence;
CREATE SCHEMA IF NOT EXISTS knowledge;

-- Per-service roles with write isolation come with task S02-15.
