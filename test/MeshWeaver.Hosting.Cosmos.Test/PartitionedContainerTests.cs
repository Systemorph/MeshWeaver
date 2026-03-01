using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Cosmos;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace MeshWeaver.Hosting.Cosmos.Test;

/// <summary>
/// Tests for partitioned store factory and routing persistence.
/// Uses the pre-created 'nodes' and 'partitions' containers from the fixture
/// to avoid exceeding the emulator's partition limit.
/// </summary>
[Collection("CosmosEmulator")]
public class PartitionedContainerTests
{
    private readonly CosmosEmulatorFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public PartitionedContainerTests(CosmosEmulatorFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void SanitizeContainerName_VariousInputs()
    {
        CosmosPartitionedStoreFactory.SanitizeContainerName("ACME")
            .Should().Be("acme");

        CosmosPartitionedStoreFactory.SanitizeContainerName("My_Company")
            .Should().Be("my-company");

        CosmosPartitionedStoreFactory.SanitizeContainerName("AB")
            .Should().Be("ab0", "short names should be padded to 3 chars");

        CosmosPartitionedStoreFactory.SanitizeContainerName("Valid-Name-99")
            .Should().Be("valid-name-99");
    }

    [Fact]
    public async Task SaveAndRead_ViaStorageAdapter()
    {
        // Test write/read using the pre-created 'nodes' container directly
        var client = CosmosEmulatorFixture.SharedClient!;
        var database = client.GetDatabase(CosmosEmulatorFixture.DatabaseName);
        var nodesContainer = database.GetContainer("nodes");
        var partitionsContainer = database.GetContainer("partitions");
        var adapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);

        var node = new MeshNode("TestItem1", "PartitionTest") { Name = "Test Item" };
        await adapter.WriteAsync(node, _options, TestContext.Current.CancellationToken);

        var read = await adapter.ReadAsync("PartitionTest/TestItem1", _options, TestContext.Current.CancellationToken);
        read.Should().NotBeNull();
        read!.Name.Should().Be("Test Item");
        read.Id.Should().Be("TestItem1");
        read.Namespace.Should().Be("PartitionTest");
    }

    [Fact]
    public async Task Delete_ViaStorageAdapter()
    {
        var client = CosmosEmulatorFixture.SharedClient!;
        var database = client.GetDatabase(CosmosEmulatorFixture.DatabaseName);
        var nodesContainer = database.GetContainer("nodes");
        var partitionsContainer = database.GetContainer("partitions");
        var adapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);

        // Write two nodes in different namespaces
        var nodeA = new MeshNode("DelItem", "DelRegion1") { Name = "Region1 Item" };
        var nodeB = new MeshNode("DelItem", "DelRegion2") { Name = "Region2 Item" };
        await adapter.WriteAsync(nodeA, _options, TestContext.Current.CancellationToken);
        await adapter.WriteAsync(nodeB, _options, TestContext.Current.CancellationToken);

        // Delete from Region1
        await adapter.DeleteAsync("DelRegion1/DelItem", TestContext.Current.CancellationToken);

        var readA = await adapter.ReadAsync("DelRegion1/DelItem", _options, TestContext.Current.CancellationToken);
        readA.Should().BeNull();

        // Region2 should still have its item
        var readB = await adapter.ReadAsync("DelRegion2/DelItem", _options, TestContext.Current.CancellationToken);
        readB.Should().NotBeNull();
        readB!.Name.Should().Be("Region2 Item");
    }

    [Fact]
    public async Task ListChildPaths_ReturnsDirectChildren()
    {
        var client = CosmosEmulatorFixture.SharedClient!;
        var database = client.GetDatabase(CosmosEmulatorFixture.DatabaseName);
        var nodesContainer = database.GetContainer("nodes");
        var partitionsContainer = database.GetContainer("partitions");
        var adapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);

        // Write children under a parent namespace
        var child1 = new MeshNode("Child1", "ListTest/Parent") { Name = "Child One" };
        var child2 = new MeshNode("Child2", "ListTest/Parent") { Name = "Child Two" };
        await adapter.WriteAsync(child1, _options, TestContext.Current.CancellationToken);
        await adapter.WriteAsync(child2, _options, TestContext.Current.CancellationToken);

        var (nodePaths, _) = await adapter.ListChildPathsAsync("ListTest/Parent", TestContext.Current.CancellationToken);

        nodePaths.Should().Contain("ListTest/Parent/Child1");
        nodePaths.Should().Contain("ListTest/Parent/Child2");
    }
}
