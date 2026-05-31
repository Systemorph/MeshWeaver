using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;
using MeshWeaver.Fixture;

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
    public void WriteAndReadNode()
    {
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        var node = new MeshNode("Story1", "ACME/Project")
        {
            Name = "Story One",
            NodeType = "Story"
        };

        adapter.Write(node, _options).Should().Within(30.Seconds()).Emit();
        var result = adapter.Read("ACME/Project/Story1", _options).Should().Within(30.Seconds()).Emit();

        result.Should().NotBeNull();
        result!.Id.Should().Be("Story1");
        result.Namespace.Should().Be("ACME/Project");
        result.Name.Should().Be("Story One");
        result.NodeType.Should().Be("Story");
        result.Path.Should().Be("ACME/Project/Story1");
    }

    [Fact]
    public void ReadNonExistentNodeReturnsNull()
    {
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        var result = adapter.Read("nonexistent/path", _options).Should().Within(30.Seconds()).Emit();
        result.Should().BeNull();
    }

    [Fact]
    public void WriteUpsertUpdatesNode()
    {
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        var node1 = new MeshNode("N1", "ns") { Name = "Original" };
        adapter.Write(node1, _options).Should().Within(30.Seconds()).Emit();

        var node2 = new MeshNode("N1", "ns") { Name = "Updated" };
        adapter.Write(node2, _options).Should().Within(30.Seconds()).Emit();

        var result = adapter.Read("ns/N1", _options).Should().Within(30.Seconds()).Emit();
        result!.Name.Should().Be("Updated");
    }

    [Fact]
    public void DeleteNode()
    {
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        var node = new MeshNode("ToDelete", "ns");
        adapter.Write(node, _options).Should().Within(30.Seconds()).Emit();

        adapter.Delete("ns/ToDelete").Should().Within(30.Seconds()).Emit();
        var result = adapter.Read("ns/ToDelete", _options).Should().Within(30.Seconds()).Emit();
        result.Should().BeNull();
    }

    [Fact]
    public void ExistsReturnsTrueForExistingNode()
    {
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        var node = new MeshNode("Exists", "ns");
        adapter.Write(node, _options).Should().Within(30.Seconds()).Emit();

        adapter.Exists("ns/Exists").Should().Within(30.Seconds()).Be(true);
        adapter.Exists("ns/NotExists").Should().Within(30.Seconds()).Be(false);
    }

    [Fact]
    public void ListChildPaths()
    {
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        adapter.Write(new MeshNode("A", "parent"), _options).Should().Within(30.Seconds()).Emit();
        adapter.Write(new MeshNode("B", "parent"), _options).Should().Within(30.Seconds()).Emit();
        adapter.Write(new MeshNode("C", "other"), _options).Should().Within(30.Seconds()).Emit();

        var (nodePaths, _) = adapter.ListChildPaths("parent").Should().Within(30.Seconds()).Emit();
        nodePaths.Should().BeEquivalentTo(new[] { "parent/A", "parent/B" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public void RootLevelNodes()
    {
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        adapter.Write(new MeshNode("Root1") { Name = "Root" }, _options).Should().Within(30.Seconds()).Emit();
        var result = adapter.Read("Root1", _options).Should().Within(30.Seconds()).Emit();

        result.Should().NotBeNull();
        result!.Id.Should().Be("Root1");
        result.Namespace.Should().BeNull();
        result.Path.Should().Be("Root1");
    }

    [Fact]
    public void WriteNodeWithContent()
    {
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
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

        adapter.Write(node, _options).Should().Within(30.Seconds()).Emit();
        var result = adapter.Read("ns/WithContent", _options).Should().Within(30.Seconds()).Emit();

        result.Should().NotBeNull();
        result!.Content.Should().NotBeNull();
    }

    [Fact]
    public void PartitionObjectsCrud()
    {
        _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        var objects = new List<object>
        {
            new TestPartitionObject("obj1", "value1"),
            new TestPartitionObject("obj2", "value2")
        };

        adapter.SavePartitionObjects("node1", "sub1", objects, _options).Should().Within(30.Seconds()).Emit();

        var loaded = adapter.GetPartitionObjects("node1", "sub1", _options)
            .ToList().Should().Within(30.Seconds()).Emit();

        loaded.Should().HaveCount(2);

        // Check max timestamp
        var maxTs = adapter.GetPartitionMaxTimestamp("node1", "sub1").Should().Within(30.Seconds()).Emit();
        maxTs.Should().NotBeNull();

        // Delete
        adapter.DeletePartitionObjects("node1", "sub1").Should().Within(30.Seconds()).Emit();
        var afterDelete = adapter.GetPartitionObjects("node1", "sub1", _options)
            .ToList().Should().Within(30.Seconds()).Emit();
        afterDelete.Should().BeEmpty();
    }

    private record TestPartitionObject(string Id, string Value);
}
