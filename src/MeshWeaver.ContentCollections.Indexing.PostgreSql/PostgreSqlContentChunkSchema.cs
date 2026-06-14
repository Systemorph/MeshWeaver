namespace MeshWeaver.ContentCollections.Indexing.PostgreSql;

/// <summary>
/// The pgvector DDL for the separate vector store backing a
/// <see cref="PostgreSqlChunkedContentVectorStore"/>. Two tables in the connection's default
/// schema:
/// <list type="bullet">
///   <item><c>content_chunks</c> — one row per indexed text window, carrying its
///     <c>embedding vector({dim})</c> with an HNSW cosine index for nearest-neighbour search.</item>
///   <item><c>content_files</c> — one row per indexed file, recording the whole-file
///     <c>content_hash</c> the hash gate compares (skip-unchanged).</item>
/// </list>
/// <para>Mirrors <c>PostgreSqlSchemaInitializer.GetMeshSchemaScript</c>: <c>CREATE EXTENSION IF
/// NOT EXISTS vector</c>, the <c>embedding vector({dim})</c> column, the
/// <c>USING hnsw (embedding vector_cosine_ops)</c> index, and the <c>DO $migrate$</c> dim-change
/// drop/recreate block (so changing the embedder's dimensionality re-creates the column + index
/// instead of failing on the stale width).</para>
/// </summary>
public static class PostgreSqlContentChunkSchema
{
    /// <summary>
    /// The idempotent provisioning script for one separate-Postgres vector store, parameterised by
    /// the embedding <paramref name="dim"/>. Every statement is <c>IF NOT EXISTS</c> /
    /// <c>CREATE OR REPLACE</c> / a guarded <c>DO</c> block, so it is safe to run on every boot.
    /// </summary>
    public static string GetSchemaScript(int dim) => $$"""
        CREATE EXTENSION IF NOT EXISTS vector;

        -- content_files: the hash gate. One row per (collection, file); content_hash is the
        -- whole-file SHA-256 the indexer compares to decide whether a file changed.
        CREATE TABLE IF NOT EXISTS content_files (
            collection_path TEXT        NOT NULL,
            file_path       TEXT        NOT NULL,
            content_hash    TEXT        NOT NULL,
            last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            PRIMARY KEY (collection_path, file_path)
        );

        -- content_chunks: one indexed text window + its embedding. Replaced wholesale per file
        -- (delete-then-insert) so re-indexing never leaves dupes.
        CREATE TABLE IF NOT EXISTS content_chunks (
            id              BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
            collection_path TEXT        NOT NULL,
            file_path       TEXT        NOT NULL,
            chunk_index     INT         NOT NULL,
            source_address  TEXT,
            content_hash    TEXT,
            chunk_text      TEXT,
            metadata        JSONB,
            embedding       vector({{dim}}),
            last_modified   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
            UNIQUE (collection_path, file_path, chunk_index)
        );

        CREATE INDEX IF NOT EXISTS idx_content_chunks_collection_file
            ON content_chunks (collection_path, file_path);

        -- HNSW cosine index — the nearest-neighbour search path (embedding <=> query).
        CREATE INDEX IF NOT EXISTS idx_content_chunks_embedding
            ON content_chunks USING hnsw (embedding vector_cosine_ops);

        -- Migrate the embedding column if the embedder's dimensionality changed since the table
        -- was created. Copied from PostgreSqlSchemaInitializer.GetMeshSchemaScript: read the
        -- current declared width from pg_attribute; if it differs from {{dim}}, drop the HNSW
        -- index, re-type the column (NULLing existing vectors — they need re-embedding at the new
        -- width anyway), and re-create the index.
        DO $migrate$
        DECLARE cur_dim INT;
        BEGIN
            SELECT atttypmod INTO cur_dim FROM pg_attribute
            WHERE attrelid = 'content_chunks'::regclass AND attname = 'embedding' AND atttypmod > 0;
            IF cur_dim IS NOT NULL AND cur_dim != {{dim}} THEN
                DROP INDEX IF EXISTS idx_content_chunks_embedding;
                ALTER TABLE content_chunks ALTER COLUMN embedding TYPE vector({{dim}}) USING NULL;
                CREATE INDEX idx_content_chunks_embedding
                    ON content_chunks USING hnsw (embedding vector_cosine_ops);
            END IF;
        END $migrate$;
        """;
}
