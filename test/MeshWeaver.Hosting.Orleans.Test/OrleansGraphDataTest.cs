using System;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Orleans.TestingHost.InProcess;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Integration tests that verify Orleans routing works end-to-end
/// with FileSystem persistence and graph data from samples/Graph/Data.
/// Replicates the portal pattern from Memex.Portal.Distributed/Program.cs.
/// </summary>
public class OrleansGraphDataTest(ITestOutputHelper output) : TestBase(output)
{
    private static string SamplesGraphData =>
        Path.Combine(AppContext.BaseDirectory, "SamplesGraph", "Data");

    protected TestCluster Cluster { get; private set; } = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<GraphDataSiloConfigurator>();
        builder.AddClientBuilderConfigurator<TestClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    /// <summary>
    /// Creates a portal-like hub (similar to PortalApplication.cs) that
    /// registers with the routing service and can subscribe to remote streams.
    /// </summary>
    private async Task<IMessageHub> CreatePortalHubAsync()
    {
        var meshHub = Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();
        var routingService = Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>();

        // Create a portal hub under the mesh hub, like PortalApplication does
        var portalHub = meshHub.GetHostedHub(
            AddressExtensions.CreatePortalAddress(),
            config => config
                .AddLayoutClient()
                .WithInitialization(async (hub, _) =>
                {
                    var registration = await routingService.RegisterStreamAsync(hub);
                    hub.RegisterForDisposal(registration);
                }))!;

        // Wait briefly for initialization to complete
        await Task.Delay(500);
        return portalHub;
    }

    [Fact(Timeout = 60000)]
    public async Task OrganizationSearch_ShouldRender()
    {
        var portal = await CreatePortalHubAsync();
        var organizationAddress = new Address("Organization");

        // First ping to ensure Organization grain is activated and compiled
        var pingResponse = await portal
            .AwaitResponse(new PingRequest(),
                o => o.WithTarget(organizationAddress),
                new CancellationTokenSource(30.Seconds()).Token);
        Output.WriteLine($"Ping response: {pingResponse.Message.GetType().Name}");

        var workspace = portal.GetWorkspace();
        var reference = new LayoutAreaReference("Search");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            organizationAddress,
            reference);

        var value = await stream.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Should().NotBe(default(JsonElement), "Search view should render for Organization");
    }

    [Fact(Timeout = 60000)]
    public async Task OrganizationDefault_ShouldRender()
    {
        var portal = await CreatePortalHubAsync();
        var organizationAddress = new Address("Organization");

        var pingResponse = await portal
            .AwaitResponse(new PingRequest(),
                o => o.WithTarget(organizationAddress),
                new CancellationTokenSource(30.Seconds()).Token);
        Output.WriteLine($"Ping response: {pingResponse.Message.GetType().Name}");

        var workspace = portal.GetWorkspace();
        var reference = new LayoutAreaReference(string.Empty);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            organizationAddress,
            reference);

        var value = await stream.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();
        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Should().NotBe(default(JsonElement), "Default view should render for Organization");
    }

    [Fact(Timeout = 30000)]
    public async Task DiagnosticTest_CheckSiloServices()
    {
        // Access the silo's service provider to check if persistence is set up correctly
        var siloHandle = (InProcessSiloHandle)Cluster.Silos[0];
        var siloServiceProvider = siloHandle.SiloHost.Services;

        var persistenceCore = siloServiceProvider.GetRequiredService<IPersistenceServiceCore>();
        Output.WriteLine($"IPersistenceServiceCore type: {persistenceCore.GetType().Name}");
        persistenceCore.Should().BeOfType<FileSystemPersistenceService>(
            "FileSystem persistence should override InMemory persistence");

        var storageAdapter = siloServiceProvider.GetService<IStorageAdapter>();
        Output.WriteLine($"IStorageAdapter type: {storageAdapter?.GetType().Name ?? "null"}");
        storageAdapter.Should().NotBeNull("FileSystemStorageAdapter should be registered");

        // Check if Organization node exists in persistence
        var persistence = siloServiceProvider.GetRequiredService<IPersistenceService>();
        var orgNode = await persistence.GetNodeAsync("Organization", TestContext.Current.CancellationToken);
        Output.WriteLine($"Organization node from persistence: {orgNode?.Path ?? "null"}");
        orgNode.Should().NotBeNull("Organization node should exist in FileSystem persistence");

        // Check the mesh catalog resolution
        var meshCatalog = siloServiceProvider.GetRequiredService<IMeshCatalog>();
        var resolution = await meshCatalog.ResolvePathAsync("Organization");
        Output.WriteLine($"ResolvePathAsync('Organization'): Prefix={resolution?.Prefix}, Remainder={resolution?.Remainder}");
        resolution.Should().NotBeNull("Organization path should resolve");
    }

    [Fact(Timeout = 60000)]
    public async Task PingOrganization()
    {
        var portal = await CreatePortalHubAsync();
        var organizationAddress = new Address("Organization");

        Output.WriteLine("Sending PingRequest to Organization via Orleans routing...");
        var response = await portal
            .AwaitResponse(new PingRequest(),
                o => o.WithTarget(organizationAddress),
                new CancellationTokenSource(30.Seconds()).Token);

        Output.WriteLine($"Received response: {response.Message.GetType().Name}");
        response.Should().NotBeNull();
        response.Message.Should().BeOfType<PingResponse>();
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>
/// Silo configurator that sets up FileSystem persistence with graph data,
/// mirroring the Memex.Portal.Distributed setup but using FileSystem instead of Cosmos.
/// </summary>
public class GraphDataSiloConfigurator : ISiloConfigurator, IHostConfigurator
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
        hostBuilder.UseOrleansMeshServer()
            .AddFileSystemPersistence(SamplesGraphData)
            .ConfigurePortalMesh()  // Already includes AddKernel()
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}
