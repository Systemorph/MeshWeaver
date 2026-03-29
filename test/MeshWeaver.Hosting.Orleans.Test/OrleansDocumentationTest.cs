using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Data;
using MeshWeaver.Documentation;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Orleans.Hosting;
using Orleans.TestingHost;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Tests that Documentation static nodes (from DocumentationNodeProvider) work
/// correctly with Orleans routing — same setup as the distributed portal.
/// Verifies: path resolution, search with names/icons, layout area loading.
/// </summary>
public class OrleansDocumentationTest(ITestOutputHelper output) : TestBase(output)
{
    protected TestCluster Cluster { get; private set; } = null!;

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<DocSiloConfigurator>();
        builder.AddClientBuilderConfigurator<DocClientConfigurator>();
        Cluster = builder.Build();
        await Cluster.DeployAsync();
    }

    private async Task<IMessageHub> CreatePortalHubAsync()
    {
        var meshHub = Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();
        var routingService = Cluster.Client.ServiceProvider.GetRequiredService<IRoutingService>();

        var portalHub = meshHub.GetHostedHub(
            AddressExtensions.CreatePortalAddress(),
            config => config
                .AddLayoutClient()
                .WithInitialization(async (hub, _) =>
                {
                    var registration = await routingService.RegisterStreamAsync(hub);
                    hub.RegisterForDisposal(registration);
                }))!;

        await Task.Delay(500);
        return portalHub;
    }

    [Fact(Timeout = 60000)]
    public async Task Search_BusinessRules_ReturnsWithNameAndIcon()
    {
        // Use the mesh hub from the client — same DI container in co-hosted setup
        var meshHub = Cluster.Client.ServiceProvider.GetRequiredService<IMessageHub>();
        var meshService = meshHub.ServiceProvider.GetRequiredService<IMeshService>();
        var ct = TestContext.Current.CancellationToken;

        // Exact same query pattern as SearchBar.ExecuteTextSearchAsync
        var query = "*business* scope:descendants context:search is:main limit:50";
        var results = new List<MeshNode>();
        await foreach (var obj in meshService.QueryAsync(new MeshQueryRequest { Query = query }, ct))
        {
            if (obj is MeshNode n)
                results.Add(n);
        }

        Output.WriteLine($"Search results: {results.Count}");
        foreach (var n in results)
            Output.WriteLine($"  {n.Path}: Name='{n.Name}', Icon='{n.Icon}'");

        results.Should().NotBeEmpty("Search for 'business' should find Doc nodes");

        var businessRules = results.FirstOrDefault(n => n.Path == "Doc/Architecture/BusinessRules");
        businessRules.Should().NotBeNull("BusinessRules node should appear in search");
        businessRules!.Name.Should().NotBeNullOrEmpty("Name must be set");
        businessRules.Icon.Should().NotBeNullOrEmpty("Icon must be set");
    }

    [Fact(Timeout = 60000)]
    public async Task DocNode_PathResolves()
    {
        var pathResolver = Cluster.Client.ServiceProvider.GetRequiredService<IPathResolver>();

        var resolution = await pathResolver.ResolvePathAsync("Doc/Architecture/BusinessRules");
        Output.WriteLine($"Resolution: Prefix={resolution?.Prefix}, Remainder={resolution?.Remainder}");
        resolution.Should().NotBeNull("Doc/Architecture/BusinessRules should resolve");
    }

    [Fact(Timeout = 120000)]
    public async Task BusinessRules_LayoutArea_Loads()
    {
        var portal = await CreatePortalHubAsync();
        var address = new Address("Doc/Architecture/BusinessRules");

        Output.WriteLine("Pinging Doc/Architecture/BusinessRules...");
        var response = await portal.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            new CancellationTokenSource(30.Seconds()).Token);
        Output.WriteLine($"Ping: {response.Message.GetType().Name}");

        var workspace = portal.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);

        Output.WriteLine("Waiting for Overview area...");
        var value = await stream.Timeout(30.Seconds()).FirstAsync();
        Output.WriteLine($"Received: ValueKind={value.Value.ValueKind}");

        value.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "BusinessRules Overview should return content");
    }

    public override async ValueTask DisposeAsync()
    {
        if (Cluster is not null)
            await Cluster.DisposeAsync();
        await base.DisposeAsync();
    }
}

/// <summary>
/// Client configurator that also registers Documentation (mirrors co-hosted portal).
/// </summary>
public class DocClientConfigurator : IHostConfigurator
{
    public void Configure(IHostBuilder hostBuilder)
    {
        var meshBuilder = hostBuilder.UseOrleansMeshClient();
        meshBuilder.AddDocumentation();
    }
}

/// <summary>
/// Silo configurator with in-memory persistence + Documentation static provider.
/// Mirrors the distributed portal setup without PostgreSQL.
/// </summary>
public class DocSiloConfigurator : ISiloConfigurator, IHostConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.ConfigureMeshWeaverServer()
            .AddMemoryGrainStorageAsDefault();
    }

    public void Configure(IHostBuilder hostBuilder)
    {
        hostBuilder.UseOrleansMeshServer()
            .ConfigurePortalMesh()
            .AddDocumentation()
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }
}
