using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Hosting.Sqlite;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Sqlite.Test;

/// <summary>
/// Pins SQLite vector search: the adapter embeds nodes at write time, and
/// <see cref="SqliteVectorMeshQuery"/> ranks free-text search + autocomplete by cosine similarity.
/// A deterministic stub embedder maps text to a tiny "semantic bucket" vector so ordering is
/// predictable without a real model.
/// </summary>
public class SqliteVectorSearchTests
{
    private static readonly JsonSerializerOptions Options = new();

    private sealed class StubEmbedder : ITextEmbedder
    {
        public int Dimensions => 4;

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
            => Task.FromResult<float[]?>(Vec(text));

        // 4 buckets: tech / fruit / animal / a small constant so every vector has non-zero norm.
        private static float[] Vec(string text)
        {
            var t = (text ?? "").ToLowerInvariant();
            float tech = t.Contains("laptop") || t.Contains("computer") || t.Contains("device") ? 1f : 0f;
            float fruit = t.Contains("banana") || t.Contains("apple") || t.Contains("fruit") ? 1f : 0f;
            float animal = t.Contains("dog") || t.Contains("cat") || t.Contains("pet") ? 1f : 0f;
            return [tech, fruit, animal, 0.01f];
        }
    }

    private static Task<T> Run<T>(IObservable<T> obs) => obs.FirstAsync().ToTask();

    private static async Task SeedShop(SqliteStorageAdapter adapter)
    {
        await Run(adapter.Write(new MeshNode("laptop", "Shop") { Name = "Laptop computer", NodeType = "Item" }, Options));
        await Run(adapter.Write(new MeshNode("banana", "Shop") { Name = "Banana fruit", NodeType = "Item" }, Options));
        await Run(adapter.Write(new MeshNode("dog", "Shop") { Name = "Dog pet", NodeType = "Item" }, Options));
    }

    [Fact]
    public async Task Free_text_search_ranks_the_semantically_nearest_node_first()
    {
        using var adapter = new SqliteStorageAdapter("Data Source=:memory:", embedder: new StubEmbedder());
        await SeedShop(adapter);

        var provider = new SqliteVectorMeshQuery(adapter, new StubEmbedder());
        var change = await Run(provider.Query<MeshNode>(new MeshQueryRequest { Query = "laptop device" }, Options));

        change.Items.Should().NotBeEmpty();
        change.Items[0].Path.Should().Be("Shop/laptop");
        change.Scores.Should().NotBeNull();
        change.Scores![0].Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Structured_filter_is_preserved_alongside_vector_ranking()
    {
        using var adapter = new SqliteStorageAdapter("Data Source=:memory:", embedder: new StubEmbedder());
        await SeedShop(adapter);
        await Run(adapter.Write(new MeshNode("laptopDoc", "Shop") { Name = "Laptop computer", NodeType = "Markdown" }, Options));

        var provider = new SqliteVectorMeshQuery(adapter, new StubEmbedder());
        // nodeType:Item must exclude the Markdown node even though it embeds identically.
        var change = await Run(provider.Query<MeshNode>(
            new MeshQueryRequest { Query = "laptop nodeType:Item" }, Options));

        change.Items.Should().NotBeEmpty();
        change.Items.Select(n => n.NodeType).Should().AllBe("Item");
        change.Items[0].Path.Should().Be("Shop/laptop");
    }

    [Fact]
    public async Task Autocomplete_ranks_by_vector_similarity()
    {
        using var adapter = new SqliteStorageAdapter("Data Source=:memory:", embedder: new StubEmbedder());
        await SeedShop(adapter);

        var provider = new SqliteVectorMeshQuery(adapter, new StubEmbedder());
        var results = await Run(provider.Autocomplete("", "computer device", Options));

        results.Should().NotBeEmpty();
        results.First().Path.Should().Be("Shop/laptop");
    }

    [Fact]
    public async Task Vector_provider_is_inert_without_an_embedder()
    {
        using var adapter = new SqliteStorageAdapter("Data Source=:memory:"); // no embedder
        await Run(adapter.Write(new MeshNode("laptop", "Shop") { Name = "Laptop", NodeType = "Item" }, Options));

        var provider = new SqliteVectorMeshQuery(adapter, embedder: null);
        var change = await Run(provider.Query<MeshNode>(new MeshQueryRequest { Query = "laptop" }, Options));

        // Still emits an (empty) Initial so the aggregator's merge counter advances.
        change.ChangeType.Should().Be(QueryChangeType.Initial);
        change.Items.Should().BeEmpty();
    }
}
