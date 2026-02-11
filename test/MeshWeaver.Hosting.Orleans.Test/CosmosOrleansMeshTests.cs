using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Cosmos;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Tests that verify Orleans + Cosmos DB integration end-to-end.
/// Requires Docker to run the Cosmos DB emulator container.
/// </summary>
[Collection("CosmosEmulator")]
public class CosmosOrleansMeshTests(ITestOutputHelper output) : CosmosOrleansTestBase(output)
{
    [Fact]
    public async Task PingPongWithCosmosPersistence()
    {
        var client = await GetClientAsync();
        var response = await client
            .AwaitResponse(new PingRequest(), o => o.WithTarget(OrleansTestMeshNodeAttribute.Address)
                , TestContext.Current.CancellationToken
            );
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }

    [Fact]
    public async Task CosmosStorageAdapter_WriteAndRead()
    {
        var cosmosClient = CosmosEmulatorFixture.SharedClient!;
        var db = cosmosClient.GetDatabase(CosmosEmulatorFixture.DatabaseName);
        var nodesContainer = db.GetContainer("nodes");
        var partitionsContainer = db.GetContainer("partitions");

        var adapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);
        var jsonOptions = StorageImporter.CreateFullImportOptions();
        var ct = TestContext.Current.CancellationToken;

        // Write a test node
        var node = new MeshNode("TestNode", "TestNamespace") { Name = "Test Node" };
        await adapter.WriteAsync(node, jsonOptions, ct);

        // Read it back
        var readNode = await adapter.ReadAsync("TestNamespace/TestNode", jsonOptions, ct);
        readNode.Should().NotBeNull();
        readNode!.Id.Should().Be("TestNode");
        readNode.Namespace.Should().Be("TestNamespace");
        readNode.Name.Should().Be("Test Node");
    }

    [Fact]
    public async Task CosmosStorageAdapter_ListChildren()
    {
        var cosmosClient = CosmosEmulatorFixture.SharedClient!;
        var db = cosmosClient.GetDatabase(CosmosEmulatorFixture.DatabaseName);
        var nodesContainer = db.GetContainer("nodes");
        var partitionsContainer = db.GetContainer("partitions");

        var adapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);
        var jsonOptions = StorageImporter.CreateFullImportOptions();
        var ct = TestContext.Current.CancellationToken;

        // Write parent and child nodes
        var parent = new MeshNode("Parent", "") { Name = "Parent" };
        var child1 = new MeshNode("Child1", "Parent") { Name = "Child 1" };
        var child2 = new MeshNode("Child2", "Parent") { Name = "Child 2" };

        await adapter.WriteAsync(parent, jsonOptions, ct);
        await adapter.WriteAsync(child1, jsonOptions, ct);
        await adapter.WriteAsync(child2, jsonOptions, ct);

        // List children
        var (nodePaths, _) = await adapter.ListChildPathsAsync("Parent", ct);
        nodePaths.Should().Contain("Parent/Child1");
        nodePaths.Should().Contain("Parent/Child2");
    }

    [Fact]
    public async Task CosmosStorageAdapter_DeleteNode()
    {
        var cosmosClient = CosmosEmulatorFixture.SharedClient!;
        var db = cosmosClient.GetDatabase(CosmosEmulatorFixture.DatabaseName);
        var nodesContainer = db.GetContainer("nodes");
        var partitionsContainer = db.GetContainer("partitions");

        var adapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);
        var jsonOptions = StorageImporter.CreateFullImportOptions();
        var ct = TestContext.Current.CancellationToken;

        // Write and then delete a node
        var node = new MeshNode("ToDelete", "TestNs") { Name = "Delete Me" };
        await adapter.WriteAsync(node, jsonOptions, ct);

        var exists = await adapter.ExistsAsync("TestNs/ToDelete", ct);
        exists.Should().BeTrue();

        await adapter.DeleteAsync("TestNs/ToDelete", ct);

        var readNode = await adapter.ReadAsync("TestNs/ToDelete", jsonOptions, ct);
        readNode.Should().BeNull();
    }
}

/// <summary>
/// Orleans test base that configures the silo with Cosmos DB persistence.
/// </summary>
public abstract class CosmosOrleansTestBase(ITestOutputHelper output) : TestBase(output)
{
    protected TestCluster Cluster { get; private set; } = null!;

    protected static Address CreateClientAddress(string? id = null) => new Address("client", id ?? "1");

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<CosmosTestSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    protected async Task<IMessageHub> GetClientAsync(Func<MessageHubConfiguration, MessageHubConfiguration>? config = null)
    {
        var client = ClientMesh.ServiceProvider.CreateMessageHub(CreateClientAddress(), config ?? ConfigureClient);
        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);
        return client;
    }

    protected IMessageHub ClientMesh => Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();

    protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration) =>
        configuration;

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>
/// Silo configurator that registers Cosmos DB persistence using the shared emulator.
/// </summary>
public class CosmosTestSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.ConfigureServices(services =>
        {
            // Register Cosmos persistence from the shared emulator client
            var cosmosClient = CosmosEmulatorFixture.SharedClient
                ?? throw new InvalidOperationException("Cosmos emulator not started. Ensure [Collection(\"CosmosEmulator\")] is applied.");

            var db = cosmosClient.GetDatabase(CosmosEmulatorFixture.DatabaseName);
            var nodesContainer = db.GetContainer("nodes");
            var partitionsContainer = db.GetContainer("partitions");
            var adapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);
            services.AddPersistence(adapter);
        });

        hostBuilder.UseOrleansMeshServer()
            .ConfigurePortalMesh();
    }
}
