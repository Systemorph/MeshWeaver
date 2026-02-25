using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration test for User node's Activity layout area.
/// Uses the same samples/Graph/Data setup to verify that
/// AddUserActivityViews() is properly invoked when a User hub is created.
/// Regression test for: built-in HubConfiguration was overwritten by compiled config.
/// </summary>
[Collection("SamplesGraphData")]
public class UserActivityAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverUserActivityTests",
        ".mesh-cache");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
        Directory.CreateDirectory(SharedCacheDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(dataDirectory)
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = SharedCacheDirectory;
                    o.EnableDiskCache = true;
                });
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .AddGraph();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    /// <summary>
    /// Verify that the User node type is registered and has HubConfiguration.
    /// </summary>
    [Fact(Timeout = 15000)]
    public void UserNodeType_IsRegistered()
    {
        var meshCatalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var config = meshCatalog.Configuration;

        config.Nodes.Should().ContainKey("User",
            "User node type should be registered via AddGraph() → AddUserType()");

        var userNode = config.Nodes["User"];
        userNode.HubConfiguration.Should().NotBeNull(
            "User MeshNode should have a HubConfiguration lambda that calls AddUserActivityViews()");
    }

    /// <summary>
    /// Verify that NodeTypeService returns cached HubConfiguration for the "User" node type.
    /// </summary>
    [Fact(Timeout = 15000)]
    public void NodeTypeService_HasCachedConfig_ForUserType()
    {
        var nodeTypeService = Mesh.ServiceProvider.GetService<INodeTypeService>();
        nodeTypeService.Should().NotBeNull("INodeTypeService should be registered by AddGraph()");

        var cachedConfig = nodeTypeService!.GetCachedHubConfiguration("User");
        cachedConfig.Should().NotBeNull(
            "NodeTypeService should have cached HubConfiguration for 'User' node type");
    }

    /// <summary>
    /// Verify that the Roland user node can be loaded and enriched with HubConfiguration.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task UserNode_Roland_CanBeLoaded()
    {
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();
        var nodeTypeService = Mesh.ServiceProvider.GetService<INodeTypeService>();

        var node = await persistence.GetNodeAsync("User/Roland");
        node.Should().NotBeNull("Roland user node should exist in samples/Graph/Data/User/Roland.json");
        node!.NodeType.Should().Be("User");

        // Enrich with node type — this should attach HubConfiguration
        if (nodeTypeService != null)
        {
            var enriched = await nodeTypeService.EnrichWithNodeTypeAsync(node);
            enriched.HubConfiguration.Should().NotBeNull(
                "After enrichment, User node should have HubConfiguration from UserNodeType");
        }
    }

    /// <summary>
    /// Verify that a hub can be created for the User/Roland address
    /// and that it responds to PingRequest (hub is alive).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task UserHub_Roland_CanBeCreated()
    {
        var client = GetClient();
        var rolandAddress = new Address("User/Roland");

        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(rolandAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull("User/Roland hub should be created and respond to ping");
    }

    /// <summary>
    /// The main test: resolve the Activity layout area on User/Roland.
    /// This is the area registered by AddUserActivityViews().
    /// Regression test: compilation of User/Code/Person.cs was overwriting
    /// the built-in HubConfiguration, losing layout areas.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ActivityArea_CanBeResolved_ForUserRoland()
    {
        var client = GetClient();
        var rolandAddress = new Address("User/Roland");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(rolandAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(UserActivityLayoutAreas.ActivityArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            rolandAddress,
            reference);

        var value = await stream.Timeout(TimeSpan.FromSeconds(15)).FirstAsync();

        value.Should().NotBe(default(JsonElement),
            "Activity area should render for User/Roland — AddUserActivityViews() must be invoked");
    }

    /// <summary>
    /// Also verify the Overview area works (baseline — this uses AddDefaultLayoutAreas).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task OverviewArea_CanBeResolved_ForUserRoland()
    {
        var client = GetClient();
        var rolandAddress = new Address("User/Roland");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(rolandAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            rolandAddress,
            reference);

        var value = await stream.Timeout(TimeSpan.FromSeconds(15)).FirstAsync();

        value.Should().NotBe(default(JsonElement),
            "Overview area should render for User/Roland — AddDefaultLayoutAreas() must be invoked");
    }
}
