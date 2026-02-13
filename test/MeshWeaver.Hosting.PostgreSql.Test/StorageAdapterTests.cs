using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

[Collection("PostgreSql")]
public class StorageAdapterTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public StorageAdapterTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WriteAndReadNode()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        var node = new MeshNode("Story1", "ACME/Project")
        {
            Name = "Story One",
            NodeType = "Story",
            Description = "A test story"
        };

        await adapter.WriteAsync(node, _options);
        var result = await adapter.ReadAsync("ACME/Project/Story1", _options);

        result.Should().NotBeNull();
        result!.Id.Should().Be("Story1");
        result.Namespace.Should().Be("ACME/Project");
        result.Name.Should().Be("Story One");
        result.NodeType.Should().Be("Story");
        result.Description.Should().Be("A test story");
        result.Path.Should().Be("ACME/Project/Story1");
    }

    [Fact]
    public async Task ReadNonExistentNodeReturnsNull()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        var result = await adapter.ReadAsync("nonexistent/path", _options);
        result.Should().BeNull();
    }

    [Fact]
    public async Task WriteUpsertUpdatesNode()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        var node1 = new MeshNode("N1", "ns") { Name = "Original" };
        await adapter.WriteAsync(node1, _options);

        var node2 = new MeshNode("N1", "ns") { Name = "Updated" };
        await adapter.WriteAsync(node2, _options);

        var result = await adapter.ReadAsync("ns/N1", _options);
        result!.Name.Should().Be("Updated");
    }

    [Fact]
    public async Task DeleteNode()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        var node = new MeshNode("ToDelete", "ns");
        await adapter.WriteAsync(node, _options);

        await adapter.DeleteAsync("ns/ToDelete");
        var result = await adapter.ReadAsync("ns/ToDelete", _options);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ExistsReturnsTrueForExistingNode()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        var node = new MeshNode("Exists", "ns");
        await adapter.WriteAsync(node, _options);

        (await adapter.ExistsAsync("ns/Exists")).Should().BeTrue();
        (await adapter.ExistsAsync("ns/NotExists")).Should().BeFalse();
    }

    [Fact]
    public async Task ListChildPaths()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        await adapter.WriteAsync(new MeshNode("A", "parent"), _options);
        await adapter.WriteAsync(new MeshNode("B", "parent"), _options);
        await adapter.WriteAsync(new MeshNode("C", "other"), _options);

        var (nodePaths, _) = await adapter.ListChildPathsAsync("parent");
        nodePaths.Should().BeEquivalentTo("parent/A", "parent/B");
    }

    [Fact]
    public async Task RootLevelNodes()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        await adapter.WriteAsync(new MeshNode("Root1") { Name = "Root" }, _options);
        var result = await adapter.ReadAsync("Root1", _options);

        result.Should().NotBeNull();
        result!.Id.Should().Be("Root1");
        result.Namespace.Should().BeNull();
        result.Path.Should().Be("Root1");
    }

    [Fact]
    public async Task WriteNodeWithContent()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        var content = new Dictionary<string, object>
        {
            ["status"] = "Open",
            ["priority"] = "High"
        };

        var node = new MeshNode("WithContent", "ns")
        {
            Content = content
        };

        await adapter.WriteAsync(node, _options);
        var result = await adapter.ReadAsync("ns/WithContent", _options);

        result.Should().NotBeNull();
        result!.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task PartitionObjectsCrud()
    {
        await _fixture.CleanDataAsync();
        var adapter = _fixture.StorageAdapter;

        var objects = new List<object>
        {
            new TestPartitionObject("obj1", "value1"),
            new TestPartitionObject("obj2", "value2")
        };

        await adapter.SavePartitionObjectsAsync("node1", "sub1", objects, _options);

        var loaded = new List<object>();
        await foreach (var obj in adapter.GetPartitionObjectsAsync("node1", "sub1", _options))
        {
            loaded.Add(obj);
        }

        loaded.Should().HaveCount(2);

        // Check max timestamp
        var maxTs = await adapter.GetPartitionMaxTimestampAsync("node1", "sub1");
        maxTs.Should().NotBeNull();

        // Delete
        await adapter.DeletePartitionObjectsAsync("node1", "sub1");
        var afterDelete = new List<object>();
        await foreach (var obj in adapter.GetPartitionObjectsAsync("node1", "sub1", _options))
        {
            afterDelete.Add(obj);
        }
        afterDelete.Should().BeEmpty();
    }

    private record TestPartitionObject(string Id, string Value);
}
