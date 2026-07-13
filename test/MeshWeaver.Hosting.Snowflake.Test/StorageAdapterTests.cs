using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Snowflake.Test;

/// <summary>
/// 1:1 port of the PG test project's <c>StorageAdapterTests</c> — same scenarios, same
/// assertions — against <see cref="SnowflakeStorageAdapter"/>. Every test first green-skips
/// when no Snowflake endpoint (LocalStack emulator / real account) is available.
/// </summary>
[Collection("Snowflake")]
public class StorageAdapterTests
{
    private readonly SnowflakeFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public StorageAdapterTests(SnowflakeFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WriteAndReadNode()
    {
        _fixture.SkipUnlessAvailable();
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        var node = new MeshNode("Story1", "ACME/Project")
        {
            Name = "Story One",
            NodeType = "Story"
        };

        await adapter.Write(node, _options).Should().Within(30.Seconds()).Emit();
        var result = await adapter.Read("ACME/Project/Story1", _options).Should().Within(30.Seconds()).Emit();

        result.Should().NotBeNull();
        result!.Id.Should().Be("Story1");
        result.Namespace.Should().Be("ACME/Project");
        result.Name.Should().Be("Story One");
        result.NodeType.Should().Be("Story");
        result.Path.Should().Be("ACME/Project/Story1");
    }

    [Fact]
    public async Task ReadNonExistentNodeReturnsNull()
    {
        _fixture.SkipUnlessAvailable();
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        var result = await adapter.Read("nonexistent/path", _options).Should().Within(30.Seconds()).Emit();
        result.Should().BeNull();
    }

    [Fact]
    public async Task WriteUpsertUpdatesNode()
    {
        _fixture.SkipUnlessAvailable();
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        var node1 = new MeshNode("N1", "ns") { Name = "Original" };
        await adapter.Write(node1, _options).Should().Within(30.Seconds()).Emit();

        var node2 = new MeshNode("N1", "ns") { Name = "Updated" };
        await adapter.Write(node2, _options).Should().Within(30.Seconds()).Emit();

        var result = await adapter.Read("ns/N1", _options).Should().Within(30.Seconds()).Emit();
        result!.Name.Should().Be("Updated");
    }

    [Fact]
    public async Task DeleteNode()
    {
        _fixture.SkipUnlessAvailable();
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        var node = new MeshNode("ToDelete", "ns");
        await adapter.Write(node, _options).Should().Within(30.Seconds()).Emit();

        await adapter.Delete("ns/ToDelete").Should().Within(30.Seconds()).Emit();
        var result = await adapter.Read("ns/ToDelete", _options).Should().Within(30.Seconds()).Emit();
        result.Should().BeNull();
    }

    [Fact]
    public async Task ExistsReturnsTrueForExistingNode()
    {
        _fixture.SkipUnlessAvailable();
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        var node = new MeshNode("Exists", "ns");
        await adapter.Write(node, _options).Should().Within(30.Seconds()).Emit();

        await adapter.Exists("ns/Exists").Should().Within(30.Seconds()).Be(true);
        await adapter.Exists("ns/NotExists").Should().Within(30.Seconds()).Be(false);
    }

    [Fact]
    public async Task ListChildPaths()
    {
        _fixture.SkipUnlessAvailable();
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        await adapter.Write(new MeshNode("A", "parent"), _options).Should().Within(30.Seconds()).Emit();
        await adapter.Write(new MeshNode("B", "parent"), _options).Should().Within(30.Seconds()).Emit();
        await adapter.Write(new MeshNode("C", "other"), _options).Should().Within(30.Seconds()).Emit();

        var (nodePaths, _) = await adapter.ListChildPaths("parent").Should().Within(30.Seconds()).Emit();
        nodePaths.Should().BeEquivalentTo(new[] { "parent/A", "parent/B" }, JsonSerializerOptions.Default);
    }

    [Fact]
    public async Task RootLevelNodes()
    {
        _fixture.SkipUnlessAvailable();
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        await adapter.Write(new MeshNode("Root1") { Name = "Root" }, _options).Should().Within(30.Seconds()).Emit();
        var result = await adapter.Read("Root1", _options).Should().Within(30.Seconds()).Emit();

        result.Should().NotBeNull();
        result!.Id.Should().Be("Root1");
        result.Namespace.Should().BeNull();
        result.Path.Should().Be("Root1");
    }

    [Fact]
    public async Task WriteNodeWithContent()
    {
        _fixture.SkipUnlessAvailable();
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
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

        await adapter.Write(node, _options).Should().Within(30.Seconds()).Emit();
        var result = await adapter.Read("ns/WithContent", _options).Should().Within(30.Seconds()).Emit();

        result.Should().NotBeNull();
        result!.Content.Should().NotBeNull();
    }

    [Fact]
    public async Task PartitionObjectsCrud()
    {
        _fixture.SkipUnlessAvailable();
        await _fixture.CleanData().Should().Within(60.Seconds()).Emit();
        var adapter = _fixture.StorageAdapter;

        var objects = new List<object>
        {
            new TestPartitionObject("obj1", "value1"),
            new TestPartitionObject("obj2", "value2")
        };

        await adapter.SavePartitionObjects("node1", "sub1", objects, _options).Should().Within(30.Seconds()).Emit();

        var loaded = await adapter.GetPartitionObjects("node1", "sub1", _options)
            .ToList().Should().Within(30.Seconds()).Emit();

        loaded.Should().HaveCount(2);

        // Check max timestamp
        var maxTs = await adapter.GetPartitionMaxTimestamp("node1", "sub1").Should().Within(30.Seconds()).Emit();
        maxTs.Should().NotBeNull();

        // Delete
        await adapter.DeletePartitionObjects("node1", "sub1").Should().Within(30.Seconds()).Emit();
        var afterDelete = await adapter.GetPartitionObjects("node1", "sub1", _options)
            .ToList().Should().Within(30.Seconds()).Emit();
        afterDelete.Should().BeEmpty();
    }

    private record TestPartitionObject(string Id, string Value);
}
