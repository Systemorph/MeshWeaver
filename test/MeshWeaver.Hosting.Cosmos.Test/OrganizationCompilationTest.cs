using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Cosmos;
using MeshWeaver.Hosting.Orleans;
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

namespace MeshWeaver.Hosting.Cosmos.Test;

/// <summary>
/// Tests that verify Organization node type compilation works end-to-end
/// when loading data from Cosmos DB in an Orleans silo.
/// </summary>
[Collection("CosmosEmulator")]
public class OrganizationCompilationTest(ITestOutputHelper output) : TestBase(output)
{
    private static string SamplesGraphData =>
        Path.Combine(AppContext.BaseDirectory, "SamplesGraph", "Data");

    protected TestCluster Cluster { get; private set; } = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Import Organization data from file system into Cosmos
        await ImportOrganizationDataToCosmos();

        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<OrganizationTestSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    private static async Task ImportOrganizationDataToCosmos()
    {
        var cosmosClient = CosmosEmulatorFixture.SharedClient
            ?? throw new InvalidOperationException("Cosmos emulator not started.");

        var db = cosmosClient.GetDatabase(CosmosEmulatorFixture.DatabaseName);
        var nodesContainer = db.GetContainer("nodes");
        var partitionsContainer = db.GetContainer("partitions");

        var source = new FileSystemStorageAdapter(SamplesGraphData);
        var target = new CosmosStorageAdapter(nodesContainer, partitionsContainer);
        var importer = new StorageImporter(source, target);

        // Import only Organization and its children (Code files)
        var result = await importer.ImportAsync(new StorageImportOptions
        {
            RootPath = "Organization",
            JsonOptions = StorageImporter.CreateFullImportOptions()
        });

        result.NodesImported.Should().BeGreaterThan(0,
            "Organization.json and Code files should be imported into Cosmos");
    }

    [Fact(Timeout = 60000)]
    public async Task OrganizationTypeCompiles_FromCosmosData()
    {
        // Arrange: create a client hub
        var clientMesh = Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();
        var client = clientMesh.ServiceProvider.CreateMessageHub(
            new Address("client", "orgtest"),
            config => config);

        await Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>()
            .RegisterStreamAsync(client.Address, client.DeliverMessage);

        // Act: send a PingRequest to an Organization instance node
        // This triggers MessageHubGrain activation, which calls EnsureNodeAssemblyAsync
        // to compile the Organization type from Cosmos-stored CodeConfiguration
        var orgAddress = new Address("Organization", "testorg");

        var act = async () => await client
            .AwaitResponse(new PingRequest(),
                o => o.WithTarget(orgAddress),
                new CancellationTokenSource(30.Seconds()).Token);

        // Assert: this should succeed if compilation works correctly.
        // If code files are not found, this will throw (the known issue).
        var response = await act();
        response.Should().NotBeNull();
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>
/// Silo configurator that registers Cosmos persistence AND graph configuration,
/// enabling dynamic compilation of node types (e.g., Organization) from Cosmos data.
/// </summary>
public class OrganizationTestSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    private static string SamplesGraphData =>
        Path.Combine(AppContext.BaseDirectory, "SamplesGraph", "Data");

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.ConfigureServices(services =>
        {
            var cosmosClient = CosmosEmulatorFixture.SharedClient
                ?? throw new InvalidOperationException("Cosmos emulator not started.");

            var db = cosmosClient.GetDatabase(CosmosEmulatorFixture.DatabaseName);
            var nodesContainer = db.GetContainer("nodes");
            var partitionsContainer = db.GetContainer("partitions");
            var adapter = new CosmosStorageAdapter(nodesContainer, partitionsContainer);
            services.AddCosmosPersistence(adapter);
        });

        hostBuilder.UseOrleansMeshServer()
            .ConfigurePortalMesh()
            .AddJsonGraphConfiguration(SamplesGraphData);
    }
}
