using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Hosting.Sqlite;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Sqlite.Test;

/// <summary>
/// Exercises the full <see cref="IStorageAdapter"/> contract against the SQLite adapter — the
/// counterpart to the Postgres <c>StorageAdapterTests</c>, but in-process (no container): each test
/// gets a fresh <c>Data Source=:memory:</c> adapter.
/// </summary>
public class SqliteStorageAdapterTests
{
    private static readonly JsonSerializerOptions Options = new();

    private static SqliteStorageAdapter NewInMemory() => new("Data Source=:memory:");

    private static Task<T> Run<T>(IObservable<T> obs) => obs.FirstAsync().ToTask();

    [Fact]
    public async Task Write_then_Read_round_trips_the_node()
    {
        using var adapter = NewInMemory();
        var node = new MeshNode("Story1", "ACME/Project") { Name = "Story One", NodeType = "Story" };

        await Run(adapter.Write(node, Options));
        var read = await Run(adapter.Read("ACME/Project/Story1", Options));

        read.Should().NotBeNull();
        read!.Id.Should().Be("Story1");
        read.Namespace.Should().Be("ACME/Project");
        read.Path.Should().Be("ACME/Project/Story1");
        read.Name.Should().Be("Story One");
        read.NodeType.Should().Be("Story");
    }

    [Fact]
    public async Task Read_missing_path_returns_null()
    {
        using var adapter = NewInMemory();
        (await Run(adapter.Read("ACME/Nope", Options))).Should().BeNull();
    }

    [Fact]
    public async Task Write_is_an_upsert()
    {
        using var adapter = NewInMemory();
        await Run(adapter.Write(new MeshNode("S", "ACME") { Name = "v1" }, Options));
        await Run(adapter.Write(new MeshNode("S", "ACME") { Name = "v2" }, Options));

        var read = await Run(adapter.Read("ACME/S", Options));
        read!.Name.Should().Be("v2");

        var children = await Run(adapter.ListChildPaths("ACME"));
        children.NodePaths.Should().ContainSingle().Which.Should().Be("ACME/S");
    }

    [Fact]
    public async Task Delete_removes_the_node()
    {
        using var adapter = NewInMemory();
        await Run(adapter.Write(new MeshNode("S", "ACME"), Options));
        (await Run(adapter.Exists("ACME/S"))).Should().BeTrue();

        var deleted = await Run(adapter.Delete("ACME/S"));
        deleted.Should().Be("ACME/S");
        (await Run(adapter.Exists("ACME/S"))).Should().BeFalse();
        (await Run(adapter.Read("ACME/S", Options))).Should().BeNull();
    }

    [Fact]
    public async Task Exists_reflects_presence()
    {
        using var adapter = NewInMemory();
        (await Run(adapter.Exists("ACME/S"))).Should().BeFalse();
        await Run(adapter.Write(new MeshNode("S", "ACME"), Options));
        (await Run(adapter.Exists("ACME/S"))).Should().BeTrue();
    }

    [Fact]
    public async Task ReadMany_returns_all_present_nodes()
    {
        using var adapter = NewInMemory();
        await Run(adapter.Write(new MeshNode("A", "NS"), Options));
        await Run(adapter.Write(new MeshNode("B", "NS"), Options));

        IStorageAdapter sut = adapter; // ReadMany is an interface default method
        var nodes = await sut.ReadMany(["NS/A", "NS/B", "NS/Missing"], Options).ToList().ToTask();

        var paths = nodes.Select(n => n.Path).ToList();
        paths.Should().HaveCount(2);
        paths.Should().Contain(["NS/A", "NS/B"]);
    }

    [Fact]
    public async Task ListChildPaths_returns_child_nodes_and_intermediate_directories()
    {
        using var adapter = NewInMemory();
        await Run(adapter.Write(new MeshNode("Story1", "ACME/Project"), Options));
        await Run(adapter.Write(new MeshNode("Story2", "ACME/Project"), Options));
        await Run(adapter.Write(new MeshNode("Deep", "ACME/Project/Sub"), Options));

        var children = await Run(adapter.ListChildPaths("ACME/Project"));

        var nodePaths = children.NodePaths.ToList();
        nodePaths.Should().HaveCount(2);
        nodePaths.Should().Contain(["ACME/Project/Story1", "ACME/Project/Story2"]);
        children.DirectoryPaths.Should().Contain("ACME/Project/Sub");
    }

    [Fact]
    public async Task ListChildPaths_at_root_lists_top_segments_as_directories()
    {
        using var adapter = NewInMemory();
        await Run(adapter.Write(new MeshNode("S", "ACME/Project"), Options));
        await Run(adapter.Write(new MeshNode("S", "Globex/Team"), Options));

        var root = await Run(adapter.ListChildPaths(null));
        root.DirectoryPaths.Should().Contain(["ACME", "Globex"]);
    }

    [Fact]
    public async Task FindBestPrefixMatch_returns_longest_matching_ancestor()
    {
        using var adapter = NewInMemory();
        await Run(adapter.Write(new MeshNode("acme", "Organization"), Options));
        await Run(adapter.Write(new MeshNode("Settings", "Organization/acme"), Options));

        var (node, matched) = await Run(
            adapter.FindBestPrefixMatch("Organization/acme/Settings/Deep/Leaf", Options));

        node.Should().NotBeNull();
        node!.Path.Should().Be("Organization/acme/Settings");
        matched.Should().Be(3);
    }

    [Fact]
    public async Task FindBestPrefixMatch_no_match_returns_null_zero()
    {
        using var adapter = NewInMemory();
        var (node, matched) = await Run(adapter.FindBestPrefixMatch("Nothing/Here", Options));
        node.Should().BeNull();
        matched.Should().Be(0);
    }

    [Fact]
    public async Task ResolvePath_delegates_to_prefix_match()
    {
        using var adapter = NewInMemory();
        await Run(adapter.Write(new MeshNode("acme", "Organization"), Options));
        var (node, matched) = await Run(adapter.ResolvePath("Organization/acme/Child", Options));
        node!.Path.Should().Be("Organization/acme");
        matched.Should().Be(2);
    }

    [Fact]
    public async Task PartitionObjects_save_get_round_trip()
    {
        using var adapter = NewInMemory();
        await Run(adapter.SavePartitionObjects("ACME/Doc", "Source", ["alpha", "beta"], Options));

        var objects = await adapter.GetPartitionObjects("ACME/Doc", "Source", Options).ToList().ToTask();

        objects.Should().HaveCount(2);
        var values = objects.Select(o => o!.ToString()).ToList();
        values.Should().Contain(["alpha", "beta"]);
    }

    [Fact]
    public async Task PartitionObjects_delete_removes_them()
    {
        using var adapter = NewInMemory();
        await Run(adapter.SavePartitionObjects("ACME/Doc", "Source", ["x"], Options));
        await Run(adapter.DeletePartitionObjects("ACME/Doc", "Source"));

        var objects = await adapter.GetPartitionObjects("ACME/Doc", "Source", Options).ToList().ToTask();
        objects.Should().BeEmpty();
    }

    [Fact]
    public async Task PartitionObjects_max_timestamp_is_null_when_empty_and_set_after_save()
    {
        using var adapter = NewInMemory();
        (await Run(adapter.GetPartitionMaxTimestamp("ACME/Doc"))).Should().BeNull();

        await Run(adapter.SavePartitionObjects("ACME/Doc", null, ["x"], Options));
        (await Run(adapter.GetPartitionMaxTimestamp("ACME/Doc"))).Should().NotBeNull();
    }

    [Fact]
    public async Task Changes_feed_emits_Updated_on_write()
    {
        using var adapter = NewInMemory();
        var first = adapter.Changes.FirstAsync().ToTask();

        await Run(adapter.Write(new MeshNode("S", "ACME"), Options));

        var change = await first.WaitAsync(TimeSpan.FromSeconds(5));
        change.Kind.Should().Be(DataChangeKind.Updated);
        change.Path.Should().Be("ACME/S");
    }

    [Fact]
    public async Task Changes_feed_emits_Deleted_on_delete()
    {
        using var adapter = NewInMemory();
        await Run(adapter.Write(new MeshNode("S", "ACME"), Options));

        var next = adapter.Changes.FirstAsync().ToTask();
        await Run(adapter.Delete("ACME/S"));

        var change = await next.WaitAsync(TimeSpan.FromSeconds(5));
        change.Kind.Should().Be(DataChangeKind.Deleted);
        change.Path.Should().Be("ACME/S");
    }

    [Fact]
    public async Task Data_persists_across_reopen_for_a_file_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"memex-sqlite-{Guid.NewGuid():N}.db");
        try
        {
            using (var adapter = new SqliteStorageAdapter($"Data Source={path}"))
                await Run(adapter.Write(new MeshNode("S", "ACME") { Name = "durable" }, Options));

            using (var reopened = new SqliteStorageAdapter($"Data Source={path}"))
            {
                var read = await Run(reopened.Read("ACME/S", Options));
                read.Should().NotBeNull();
                read!.Name.Should().Be("durable");
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Concurrent_writes_are_serialised_and_all_land()
    {
        using var adapter = NewInMemory();
        var writes = Enumerable.Range(0, 50)
            .Select(i => adapter.Write(new MeshNode($"N{i}", "ACME"), Options).FirstAsync().ToTask());

        await Task.WhenAll(writes);

        var children = await Run(adapter.ListChildPaths("ACME"));
        children.NodePaths.Should().HaveCount(50);
    }
}
