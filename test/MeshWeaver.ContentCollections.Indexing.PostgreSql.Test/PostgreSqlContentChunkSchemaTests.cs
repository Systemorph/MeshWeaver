using MeshWeaver.ContentCollections.Indexing.PostgreSql;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.PostgreSql.Test;

/// <summary>
/// Unit tests over the DDL string. No Postgres / Docker — these always run locally and in CI and
/// pin the schema's load-bearing shape (the table set, the HNSW cosine index, the parameterised
/// vector width, the hash-gate table).
/// </summary>
public class PostgreSqlContentChunkSchemaTests
{
    [Fact]
    public void SchemaScript_DeclaresBothTables_HnswIndex_VectorWidth_AndHashGate()
    {
        var ddl = PostgreSqlContentChunkSchema.GetSchemaScript(1536);

        // The pgvector extension, mirrored from PostgreSqlSchemaInitializer.
        ddl.Should().Contain("CREATE EXTENSION IF NOT EXISTS vector");

        // The chunk table + the hash-gate file table.
        ddl.Should().Contain("CREATE TABLE IF NOT EXISTS content_chunks");
        ddl.Should().Contain("CREATE TABLE IF NOT EXISTS content_files");

        // Identity PK + the unique chunk key (no dupes on re-index).
        ddl.Should().Contain("BIGINT      GENERATED ALWAYS AS IDENTITY PRIMARY KEY");
        ddl.Should().Contain("UNIQUE (collection_path, file_path, chunk_index)");

        // content_files PK is (collection_path, file_path) — the hash gate.
        ddl.Should().Contain("PRIMARY KEY (collection_path, file_path)");
        ddl.Should().Contain("content_hash    TEXT        NOT NULL");

        // Vector column at the requested width + JSONB metadata + source_address.
        ddl.Should().Contain("embedding       vector(1536)");
        ddl.Should().Contain("metadata        JSONB");
        ddl.Should().Contain("source_address  TEXT");

        // The HNSW cosine index — the nearest-neighbour search path.
        ddl.Should().Contain("USING hnsw (embedding vector_cosine_ops)");

        // The (collection_path, file_path) lookup index.
        ddl.Should().Contain("idx_content_chunks_collection_file");

        // The DO $migrate$ dim-change drop/recreate block (copied from GetMeshSchemaScript).
        ddl.Should().Contain("DO $migrate$");
        ddl.Should().Contain("ALTER TABLE content_chunks ALTER COLUMN embedding TYPE vector(1536) USING NULL");
    }

    [Fact]
    public void SchemaScript_ParameterisesTheVectorDimension()
    {
        var ddl = PostgreSqlContentChunkSchema.GetSchemaScript(384);

        ddl.Should().Contain("embedding       vector(384)");
        ddl.Should().Contain("vector(384) USING NULL");
        ddl.Should().NotContain("vector(1536)");
    }
}
