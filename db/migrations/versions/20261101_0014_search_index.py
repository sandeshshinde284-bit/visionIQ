"""LRN-08: keyword (full-text) and vector (pgvector) search index over redacted chunks.

Revision ID: 20261101_0014
Revises: 20261101_0013
"""

from alembic import op

revision = "20261101_0014"
down_revision = "20261101_0013"
branch_labels = None
depends_on = None


def upgrade() -> None:
    """Derived, rebuildable index: every chunk keeps its source, revision and access list."""
    op.execute(
        """
        CREATE TABLE knowledge.index_items (
            item_key character(64) PRIMARY KEY CHECK (item_key ~ '^[0-9a-f]{64}$'),
            provider_revision_id text NOT NULL,
            source_content_sha256 character(64) NOT NULL,
            acl_principals jsonb NOT NULL CHECK (jsonb_typeof(acl_principals) = 'array'),
            chunker_version text NOT NULL,
            redaction_version text NOT NULL,
            model_id text NOT NULL,
            chunk_count integer NOT NULL CHECK (chunk_count >= 0),
            indexed_at timestamptz NOT NULL
        );
        CREATE TABLE knowledge.index_chunks (
            chunk_key character(64) PRIMARY KEY CHECK (chunk_key ~ '^[0-9a-f]{64}$'),
            item_key character(64) NOT NULL
                REFERENCES knowledge.index_items (item_key) ON DELETE CASCADE,
            provider_revision_id text NOT NULL,
            chunk_index integer NOT NULL CHECK (chunk_index >= 0),
            section text NOT NULL,
            content text NOT NULL CHECK (length(content) BETWEEN 1 AND 2000),
            content_sha256 character(64) NOT NULL,
            provider text NOT NULL,
            kind text NOT NULL,
            project_id text NOT NULL,
            repository_id text NULL,
            source_url text NULL,
            source_updated_at timestamptz NULL,
            acl_principals jsonb NOT NULL
                CHECK (jsonb_typeof(acl_principals) = 'array'
                       AND jsonb_array_length(acl_principals) > 0),
            redaction_version text NOT NULL,
            chunker_version text NOT NULL,
            tsv tsvector GENERATED ALWAYS AS (to_tsvector('english'::regconfig, content)) STORED
        );
        CREATE INDEX index_chunks_tsv ON knowledge.index_chunks USING gin (tsv);
        CREATE INDEX index_chunks_acl ON knowledge.index_chunks USING gin (acl_principals);
        CREATE INDEX index_chunks_item ON knowledge.index_chunks (item_key);
        CREATE TABLE knowledge.chunk_embeddings (
            chunk_key character(64) NOT NULL
                REFERENCES knowledge.index_chunks (chunk_key) ON DELETE CASCADE,
            model_id text NOT NULL CHECK (length(model_id) BETWEEN 1 AND 200),
            dimension integer NOT NULL CHECK (dimension = 768),
            embedding vector(768) NOT NULL,
            embedded_at timestamptz NOT NULL,
            PRIMARY KEY (chunk_key, model_id),
            CHECK (vector_dims(embedding) = dimension)
        );
        CREATE INDEX chunk_embeddings_hnsw ON knowledge.chunk_embeddings
            USING hnsw (embedding vector_cosine_ops)
        """
    )


def downgrade() -> None:
    """The index is derived data and can be rebuilt; dropping it loses no source evidence."""
    op.execute("DROP TABLE knowledge.chunk_embeddings")
    op.execute("DROP TABLE knowledge.index_chunks")
    op.execute("DROP TABLE knowledge.index_items")
