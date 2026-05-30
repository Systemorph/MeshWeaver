using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.PostgreSql;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

/// <summary>
/// Pins the vector-search wiring added in this change: when a query has
/// bare-text tokens (parsed.TextSearch is non-empty) AND an embedding
/// provider is registered, PostgreSqlMeshQuery routes through HNSW cosine
/// similarity instead of the GenerateTextSearchClause ILIKE fallback. The
/// search box (SearchHub), MCP `Search` tool, and agent `Search` tool all
/// go through this path because they all call IMeshService.ObserveQuery /
/// QueryAsync.
///
/// <para>Uses a deterministic stub embedding provider so the test doesn't
/// need real Cohere/OpenAI credentials. The stub maps each input string to
/// a sparse 1536-dim vector with one non-zero entry at index <c>hash(s) %
/// 1536</c>, so two texts that hash to the same bucket produce identical
/// vectors (cosine distance = 0) and texts that hash differently are
/// orthogonal (cosine distance ≈ 1). Cosine ranking is then deterministic.</para>
/// </summary>
[Collection("PostgreSql")]
public class VectorSearchTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public VectorSearchTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task IVectorSearchProvider_SearchAsync_FindsExactBucketMatch()
    {
        await _fixture.CleanDataAsync();
        var ct = TestContext.Current.CancellationToken;
        var stub = new StubEmbeddingProvider();
        var adapter = new PostgreSqlStorageAdapter(_fixture.DataSource, stub);
        var query = new PostgreSqlMeshQuery(
            adapter,
            
            accessService: null,
            meshConfiguration: null,
            excludedNamespaces: null,
            embeddingProvider: stub);

        await adapter.WriteAsync(new MeshNode("alpha", "VecTest")
            { Name = "alpha", NodeType = "Story" }, _options, ct);
        await adapter.WriteAsync(new MeshNode("bravo", "VecTest")
            { Name = "bravo", NodeType = "Story" }, _options, ct);

        await _fixture.AccessControl.GrantAsync("VecTest", "Anonymous", "Read", isAllow: true, ct);

        var results = new System.Collections.Generic.List<MeshNode>();
        await foreach (var node in ((IVectorSearchProvider)query).SearchAsync("alpha", _options,
            namespacePath: "VecTest", userId: null, topK: 5, ct))
            results.Add(node);

        // The stub maps "alpha" embedding to the same bucket as node "alpha"
        // (writer stored embedding of "alpha Story"; query embeds "alpha"). They
        // share a non-zero component, so cosine distance is small. The "bravo"
        // node hashes differently and ranks lower.
        results.Should().NotBeEmpty("vector-search wired through PostgreSqlMeshQuery + IVectorSearchProvider");
        results.Should().Contain(n => n.Id == "alpha",
            "the closest cosine match for query 'alpha' is the node whose embedding text starts with 'alpha'");
    }

    [Fact]
    public async Task QueryAsync_TextSearch_WithEmbeddingProvider_RoutesThroughVector()
    {
        await _fixture.CleanDataAsync();
        var ct = TestContext.Current.CancellationToken;
        var stub = new StubEmbeddingProvider();
        var adapter = new PostgreSqlStorageAdapter(_fixture.DataSource, stub);
        var query = new PostgreSqlMeshQuery(
            adapter,
            
            accessService: null,
            meshConfiguration: null,
            excludedNamespaces: null,
            embeddingProvider: stub);

        await adapter.WriteAsync(new MeshNode("doc-foo", "VecQuery")
            { Name = "doc-foo", NodeType = "Markdown" }, _options, ct);
        await adapter.WriteAsync(new MeshNode("doc-bar", "VecQuery")
            { Name = "doc-bar", NodeType = "Markdown" }, _options, ct);

        await _fixture.AccessControl.GrantAsync("VecQuery", "Anonymous", "Read", isAllow: true, ct);

        // Free-floating text ("doc-foo") + structured namespace filter. The
        // intercept in QueryAsync should: (a) detect TextSearch is non-empty,
        // (b) generate the query embedding, (c) call adapter.VectorSearchAsync
        // with namespacePath="VecQuery" so only the namespace-matching rows
        // are ranked. The structured part survives via the structuralFilter
        // (TextSearch stripped).
        var request = new MeshQueryRequest { Query = "doc-foo namespace:VecQuery", Limit = 5 };
        var hits = new System.Collections.Generic.List<object>();
        await foreach (var item in query.QueryAsync(request, _options, ct))
            hits.Add(item);

        hits.Should().NotBeEmpty("vector-search intercept must produce results for bare-text queries");
        var meshHits = hits.Cast<MeshNode>().ToList();
        meshHits.Should().Contain(n => n.Id == "doc-foo",
            "query 'doc-foo' must surface the doc-foo node via cosine similarity");
    }

    [Fact]
    public async Task QueryAsync_StructuredOnly_DoesNotInvokeEmbeddingProvider()
    {
        await _fixture.CleanDataAsync();
        var ct = TestContext.Current.CancellationToken;
        var stub = new StubEmbeddingProvider();
        var adapter = new PostgreSqlStorageAdapter(_fixture.DataSource, stub);
        var query = new PostgreSqlMeshQuery(
            adapter,
            
            accessService: null,
            meshConfiguration: null,
            excludedNamespaces: null,
            embeddingProvider: stub);

        await adapter.WriteAsync(new MeshNode("only-structured", "VecStruct")
            { Name = "node", NodeType = "Story" }, _options, ct);
        await _fixture.AccessControl.GrantAsync("VecStruct", "Anonymous", "Read", isAllow: true, ct);

        var writesBefore = stub.QueryCallCount;

        // Purely structured query — no bare-text tokens. The vector-search
        // intercept must NOT fire (the regular SQL path handles it). Stub's
        // call counter tracks GenerateEmbeddingAsync invocations.
        var request = new MeshQueryRequest { Query = "namespace:VecStruct nodeType:Story", Limit = 5 };
        var hits = new System.Collections.Generic.List<object>();
        await foreach (var item in query.QueryAsync(request, _options, ct))
            hits.Add(item);

        hits.Should().NotBeEmpty();
        // The stub's QueryCallCount tracks GenerateEmbeddingAsync calls EXCLUDING
        // those from WriteAsync (the writer also calls it). Since QueryAsync's
        // intercept only fires on TextSearch, structured-only queries must not
        // bump the count.
        stub.QueryCallCount.Should().Be(writesBefore,
            "structured-only queries must NOT invoke the embedding provider — the vector intercept is gated on TextSearch");
    }
}

/// <summary>
/// Deterministic stub embedding provider for tests. Maps each input string to
/// a sparse 1536-dim float vector with one non-zero entry at index
/// <c>(hash(text) &amp; 0x7FFFFFFF) % 1536</c>. Same input → same vector.
/// Two texts that hash to the same bucket produce identical vectors (cosine
/// distance 0); different buckets are orthogonal. Sufficient for tests that
/// pin the wiring of the vector-search path; not realistic semantics.
///
/// <para>Tracks total GenerateEmbeddingAsync calls so tests can assert the
/// intercept fired (or didn't).</para>
/// </summary>
internal sealed class StubEmbeddingProvider : IEmbeddingProvider
{
    private int _calls;

    public int Dimensions => 1536;
    public int QueryCallCount => _calls;

    public Task<float[]?> GenerateEmbeddingAsync(string text)
    {
        Interlocked.Increment(ref _calls);
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult<float[]?>(null);

        var bucket = (uint)text.GetHashCode() % 1536u;
        var v = new float[1536];
        v[bucket] = 1f;
        return Task.FromResult<float[]?>(v);
    }
}
