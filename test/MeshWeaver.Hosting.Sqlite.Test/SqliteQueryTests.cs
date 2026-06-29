using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Hosting.Sqlite;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Sqlite.Test;

/// <summary>
/// Proves that queries (<c>scope:</c> / <c>nodeType:</c> / free-text) work against the SQLite adapter
/// through the SAME generic <see cref="StorageAdapterMeshQueryProvider"/> that FileSystem / InMemory
/// use — i.e. SQLite gets full query support from its <c>ListChildPaths</c> + <c>Read</c> primitives,
/// with no SQLite-specific SQL generator. The mesh-parity counterpart to the Postgres QueryTests.
/// </summary>
public class SqliteQueryTests
{
    private static readonly JsonSerializerOptions Options = new();

    private static async Task<SqliteStorageAdapter> SeedAsync()
    {
        var a = new SqliteStorageAdapter("Data Source=:memory:");
        await a.Write(new MeshNode("Story1", "ACME/Project") { NodeType = "Story" }, Options).FirstAsync().ToTask();
        await a.Write(new MeshNode("Story2", "ACME/Project") { NodeType = "Story" }, Options).FirstAsync().ToTask();
        await a.Write(new MeshNode("Task1", "ACME/Project") { NodeType = "Task" }, Options).FirstAsync().ToTask();
        await a.Write(new MeshNode("Deep", "ACME/Project/Sub") { NodeType = "Story" }, Options).FirstAsync().ToTask();
        return a;
    }

    private static Task<QueryResultChange<MeshNode>> QueryAsync(StorageAdapterMeshQueryProvider provider, string query)
        => provider.Query<MeshNode>(new MeshQueryRequest { Query = query }, Options)
            .Where(c => c.ChangeType == QueryChangeType.Initial)
            .FirstAsync()
            .ToTask();

    [Fact]
    public async Task Query_scope_children_returns_only_direct_children()
    {
        using var adapter = await SeedAsync();
        var provider = new StorageAdapterMeshQueryProvider(adapter);

        var change = await QueryAsync(provider, "path:ACME/Project scope:children");

        var paths = change.Items.Select(n => n.Path).ToList();
        paths.Should().Contain(["ACME/Project/Story1", "ACME/Project/Story2", "ACME/Project/Task1"]);
        paths.Should().NotContain("ACME/Project/Sub/Deep");
    }

    [Fact]
    public async Task Query_nodeType_filters_by_type()
    {
        using var adapter = await SeedAsync();
        var provider = new StorageAdapterMeshQueryProvider(adapter);

        var change = await QueryAsync(provider, "path:ACME/Project scope:children nodeType:Story");

        var paths = change.Items.Select(n => n.Path).ToList();
        paths.Should().Contain(["ACME/Project/Story1", "ACME/Project/Story2"]);
        paths.Should().NotContain("ACME/Project/Task1");
    }

    [Fact]
    public async Task Query_scope_descendants_includes_nested_nodes()
    {
        using var adapter = await SeedAsync();
        var provider = new StorageAdapterMeshQueryProvider(adapter);

        var change = await QueryAsync(provider, "path:ACME/Project scope:descendants");

        change.Items.Select(n => n.Path).Should().Contain("ACME/Project/Sub/Deep");
    }
}
