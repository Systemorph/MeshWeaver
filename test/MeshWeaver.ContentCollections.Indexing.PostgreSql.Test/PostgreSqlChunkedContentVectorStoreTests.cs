using System.Collections.Immutable;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.ContentCollections.Indexing.PostgreSql;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.PostgreSql.Test;

/// <summary>
/// Postgres INTEGRATION tests for <see cref="PostgreSqlChunkedContentVectorStore"/>. Spins up a
/// <c>pgvector/pgvector:pg17</c> container via Testcontainers, exactly like the sibling
/// <c>MeshWeaver.Hosting.PostgreSql.Test</c> fixtures (e.g. <c>VectorEmbeddingTests</c>) — so this
/// runs in CI (Docker present) and is SKIPPED locally when Docker is unavailable (the container
/// start throws in <see cref="InitializeAsync"/>; the unit tests in
/// <c>PostgreSqlContentChunkSchemaTests</c> pass regardless).
/// </summary>
public class PostgreSqlChunkedContentVectorStoreTests : IAsyncLifetime
{
    private const int Dimensions = 8;
    private PostgreSqlContainer? _container;
    private PostgreSqlChunkedContentVectorStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithDatabase("content_vector_test")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();

        await _container.StartAsync();

        // No IoPoolRegistry → the store falls back to IoPool.Unbounded (still off the calling
        // thread, never FromAsync). The store builds its OWN UseVector() data source internally.
        _store = new PostgreSqlChunkedContentVectorStore(
            _container.GetConnectionString(),
            ioPoolRegistry: null,
            dimensions: Dimensions);
    }

    public async ValueTask DisposeAsync()
    {
        _store?.Dispose();
        if (_container is not null)
            await _container.DisposeAsync();
    }

    [Fact]
    public async Task Provision_Replace_GetHash_Search_AndReplaceAgain_NoDupes()
    {
        const string collection = "Docs";
        const string file = "Docs/intro.md";
        const string hash = "hash-v1";

        // A deterministic 8-dim embedding for each chunk: a one-hot vector at the chunk index.
        ContentChunk Chunk(int idx, string text, float[] embedding) => new(
            CollectionPath: collection,
            FilePath: file,
            ChunkIndex: idx,
            Text: text,
            ContentHash: hash,
            Embedding: embedding,
            Metadata: ImmutableDictionary<string, string>.Empty.Add("k", $"v{idx}"));

        var chunks = new[]
        {
            Chunk(0, "alpha chunk", OneHot(0)),
            Chunk(1, "bravo chunk", OneHot(1)),
            Chunk(2, "charlie chunk", OneHot(2)),
        };

        // Provision the partition schema (idempotent; collection "Docs" → schema "docs") + replace the
        // file's chunks. The CRUD methods derive + provision the schema themselves too.
        await _store.EnsureProvisioned("docs").Should().Within(60.Seconds()).Emit();
        await _store.ReplaceFileChunks(collection, file, chunks).Should().Within(30.Seconds()).Emit();

        // GetFileHash returns the recorded whole-file hash.
        var storedHash = await _store.GetFileHash(collection, file).Should().Within(30.Seconds()).Emit();
        storedHash.Should().Be(hash);

        // GetFileHash for an unindexed file is null (the hash gate's "never indexed").
        var missing = await _store.GetFileHash(collection, "Docs/absent.md").Should().Within(30.Seconds()).Emit();
        missing.Should().BeNull();

        // Search: the nearest chunk to OneHot(1) is chunk index 1 ("bravo chunk").
        var nearest = await _store.Search(collection, OneHot(1), topK: 1).Should().Within(30.Seconds()).Emit();
        nearest.Should().HaveCount(1);
        nearest[0].ChunkIndex.Should().Be(1);
        nearest[0].Text.Should().Be("bravo chunk");
        nearest[0].FilePath.Should().Be(file);
        // The round-tripped chunk carries its metadata + embedding back.
        nearest[0].Embedding.Should().NotBeNull();
        nearest[0].Metadata.Should().NotBeNull();

        // Re-index the SAME file with a new (smaller) chunk set + new hash. Delete-then-insert
        // must wholly replace — no dupes, no stale chunk index 2.
        var v2 = new[]
        {
            Chunk2("delta chunk", OneHot(3)),
            Chunk2("echo chunk", OneHot(4)),
        };
        await _store.ReplaceFileChunks(collection, file, v2).Should().Within(30.Seconds()).Emit();

        var hashV2 = await _store.GetFileHash(collection, file).Should().Within(30.Seconds()).Emit();
        hashV2.Should().Be("hash-v2");

        // topK larger than the row count returns exactly the 2 surviving chunks — no dupes, and
        // none of the v1 chunks linger.
        var all = await _store.Search(collection, OneHot(3), topK: 50).Should().Within(30.Seconds()).Emit();
        all.Should().HaveCount(2);
        all.Should().Contain(c => c.Text == "delta chunk");
        all.Should().Contain(c => c.Text == "echo chunk");
        all.Should().NotContain(c => c.Text == "alpha chunk");
        all.Should().NotContain(c => c.Text == "charlie chunk");

        // The nearest to OneHot(3) is the "delta chunk".
        var nearestV2 = await _store.Search(collection, OneHot(3), topK: 1).Should().Within(30.Seconds()).Emit();
        nearestV2[0].Text.Should().Be("delta chunk");

        ContentChunk Chunk2(string text, float[] embedding) => new(
            CollectionPath: collection,
            FilePath: file,
            ChunkIndex: text == "delta chunk" ? 0 : 1,
            Text: text,
            ContentHash: "hash-v2",
            Embedding: embedding,
            Metadata: null);
    }

    [Fact]
    public async Task ReplaceWithEmptySet_ClearsHash_AndRemovesChunks()
    {
        const string collection = "Empty";
        const string file = "Empty/blank.bin";

        var chunk = new ContentChunk(collection, file, 0, "some text", "h1", OneHot(0));
        await _store.ReplaceFileChunks(collection, file, [chunk]).Should().Within(60.Seconds()).Emit();
        (await _store.GetFileHash(collection, file).Should().Within(30.Seconds()).Emit()).Should().Be("h1");

        // Replacing with an empty set removes the chunks and clears the hash (mirrors the
        // in-memory store's NoText behaviour — a later content gain forces a re-attempt).
        await _store.ReplaceFileChunks(collection, file, []).Should().Within(30.Seconds()).Emit();

        (await _store.GetFileHash(collection, file).Should().Within(30.Seconds()).Emit()).Should().BeNull();
        (await _store.Search(collection, OneHot(0), topK: 10).Should().Within(30.Seconds()).Emit())
            .Should().BeEmpty();
    }

    private static float[] OneHot(int index)
    {
        var v = new float[Dimensions];
        v[index % Dimensions] = 1f;
        return v;
    }
}
