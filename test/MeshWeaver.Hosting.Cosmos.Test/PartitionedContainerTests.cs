using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using Microsoft.Azure.Cosmos;
using Xunit;

namespace MeshWeaver.Hosting.Cosmos.Test;

[Collection("CosmosEmulator")]
public class PartitionedContainerTests
{
    private readonly CosmosEmulatorFixture _fixture;
    private readonly JsonSerializerOptions _options = new();

    public PartitionedContainerTests(CosmosEmulatorFixture fixture)
    {
        _fixture = fixture;
    }

    private Task<CosmosPartitionedStoreFactory> CreateFactoryAsync()
    {
        // Reuse the pre-created cosmostest database from the fixture.
        // The emulator's partition service cannot handle rapid container creation
        // across multiple databases in a single test run.
        return Task.FromResult(new CosmosPartitionedStoreFactory(
            CosmosEmulatorFixture.SharedClient!,
            CosmosEmulatorFixture.DatabaseName));
    }

    [Fact]
    public async Task CreateStore_CreatesContainers()
    {
        var factory = await CreateFactoryAsync();

        await factory.CreateStoreAsync("Sales", TestContext.Current.CancellationToken);

        // Verify containers exist
        var client = CosmosEmulatorFixture.SharedClient!;
        var database = client.GetDatabase(CosmosEmulatorFixture.DatabaseName);

        var nodesContainer = database.GetContainer("sales-nodes");
        var props = await nodesContainer.ReadContainerAsync(cancellationToken: TestContext.Current.CancellationToken);
        props.Resource.Id.Should().Be("sales-nodes");

        var partitionsContainer = database.GetContainer("sales-partitions");
        var pProps = await partitionsContainer.ReadContainerAsync(cancellationToken: TestContext.Current.CancellationToken);
        pProps.Resource.Id.Should().Be("sales-partitions");
    }

    [Fact]
    public async Task SaveAndRead_AcrossContainers_DataIsolated()
    {
        var factory = await CreateFactoryAsync();

        var storeA = await factory.CreateStoreAsync("Vendor", TestContext.Current.CancellationToken);
        var storeB = await factory.CreateStoreAsync("Client", TestContext.Current.CancellationToken);

        // Save to Vendor partition
        var nodeA = MeshNode.FromPath("Vendor/Products") with { Name = "Vendor Products" };
        await storeA.PersistenceCore.SaveNodeAsync(nodeA, _options, TestContext.Current.CancellationToken);

        // Save to Client partition
        var nodeB = MeshNode.FromPath("Client/Orders") with { Name = "Client Orders" };
        await storeB.PersistenceCore.SaveNodeAsync(nodeB, _options, TestContext.Current.CancellationToken);

        // Read back from Vendor
        var readA = await storeA.PersistenceCore.GetNodeAsync("Vendor/Products", _options, TestContext.Current.CancellationToken);
        readA.Should().NotBeNull();
        readA!.Name.Should().Be("Vendor Products");

        // Vendor should not have Client data
        var readCross = await storeA.PersistenceCore.GetNodeAsync("Client/Orders", _options, TestContext.Current.CancellationToken);
        readCross.Should().BeNull("Client data should not be in Vendor container");

        // Read back from Client
        var readB = await storeB.PersistenceCore.GetNodeAsync("Client/Orders", _options, TestContext.Current.CancellationToken);
        readB.Should().NotBeNull();
        readB!.Name.Should().Be("Client Orders");
    }

    [Fact]
    public async Task RoutingCore_SaveRoutes_ByFirstSegment()
    {
        var factory = await CreateFactoryAsync();
        var router = new RoutingPersistenceServiceCore(factory);

        var nodeA = MeshNode.FromPath("Org1/Report") with { Name = "Org1 Report" };
        var nodeB = MeshNode.FromPath("Org2/Report") with { Name = "Org2 Report" };

        await router.SaveNodeAsync(nodeA, _options, TestContext.Current.CancellationToken);
        await router.SaveNodeAsync(nodeB, _options, TestContext.Current.CancellationToken);

        var readA = await router.GetNodeAsync("Org1/Report", _options, TestContext.Current.CancellationToken);
        readA.Should().NotBeNull();
        readA!.Name.Should().Be("Org1 Report");

        var readB = await router.GetNodeAsync("Org2/Report", _options, TestContext.Current.CancellationToken);
        readB.Should().NotBeNull();
        readB!.Name.Should().Be("Org2 Report");
    }

    [Fact]
    public async Task RoutingCore_GetChildren_RootLevel_ReturnsFromAllPartitions()
    {
        var factory = await CreateFactoryAsync();
        var router = new RoutingPersistenceServiceCore(factory);

        var nodeA = MeshNode.FromPath("Dept1") with { Name = "Department 1" };
        var nodeB = MeshNode.FromPath("Dept2") with { Name = "Department 2" };

        await router.SaveNodeAsync(nodeA, _options, TestContext.Current.CancellationToken);
        await router.SaveNodeAsync(nodeB, _options, TestContext.Current.CancellationToken);

        var children = new List<MeshNode>();
        await foreach (var child in router.GetChildrenAsync(null, _options))
            children.Add(child);

        children.Should().Contain(n => n.Path == "Dept1");
        children.Should().Contain(n => n.Path == "Dept2");
    }

    [Fact]
    public async Task DiscoverPartitions_FindsExistingContainers()
    {
        var factory = await CreateFactoryAsync();

        // Create some partitions
        await factory.CreateStoreAsync("Discover_A", TestContext.Current.CancellationToken);
        await factory.CreateStoreAsync("Discover_B", TestContext.Current.CancellationToken);

        // Create a fresh factory and discover
        var freshFactory = await CreateFactoryAsync();
        var partitions = await freshFactory.DiscoverPartitionsAsync(TestContext.Current.CancellationToken);

        partitions.Should().Contain("discover-a");
        partitions.Should().Contain("discover-b");
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
    public async Task Delete_InOnePartition_DoesNotAffectOther()
    {
        var factory = await CreateFactoryAsync();
        var router = new RoutingPersistenceServiceCore(factory);

        var nodeA = MeshNode.FromPath("Region1/Item") with { Name = "Region1 Item" };
        var nodeB = MeshNode.FromPath("Region2/Item") with { Name = "Region2 Item" };

        await router.SaveNodeAsync(nodeA, _options, TestContext.Current.CancellationToken);
        await router.SaveNodeAsync(nodeB, _options, TestContext.Current.CancellationToken);

        // Delete from Region1
        await router.DeleteNodeAsync("Region1/Item", ct: TestContext.Current.CancellationToken);

        var readA = await router.GetNodeAsync("Region1/Item", _options, TestContext.Current.CancellationToken);
        readA.Should().BeNull();

        // Region2 should still have its item
        var readB = await router.GetNodeAsync("Region2/Item", _options, TestContext.Current.CancellationToken);
        readB.Should().NotBeNull();
        readB!.Name.Should().Be("Region2 Item");
    }
}
