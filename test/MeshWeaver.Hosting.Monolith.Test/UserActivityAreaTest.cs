using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
using MeshWeaver.Mesh.Security;
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
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddUserData()
            .AddCornerstone()
            .AddMeshWeaverDocs()
            .AddMeshNodes(MeshNode.FromPath("User/TestUser") with
            {
                Name = "Test User",
                NodeType = "User",
                State = MeshNodeState.Active,
            })
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
    [Fact(Timeout = 10000)]
    public void UserNodeType_IsRegistered()
    {
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();

        var cachedConfig = nodeTypeService.GetCachedHubConfiguration("User");
        cachedConfig.Should().NotBeNull(
            "User node type should be registered via AddGraph() → AddUserType() with HubConfiguration");
    }

    /// <summary>
    /// Verify that NodeTypeService returns cached HubConfiguration for the "User" node type.
    /// </summary>
    [Fact(Timeout = 10000)]
    public void NodeTypeService_HasCachedConfig_ForUserType()
    {
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();

        var cachedConfig = nodeTypeService.GetCachedHubConfiguration("User");
        cachedConfig.Should().NotBeNull(
            "NodeTypeService should have cached HubConfiguration for 'User' node type");
    }

    /// <summary>
    /// Verify that the Roland user node can be loaded and enriched with HubConfiguration.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task UserNode_Roland_CanBeLoaded()
    {
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();

        var node = await MeshQuery.QueryAsync<MeshNode>("path:User/TestUser scope:exact").FirstOrDefaultAsync();
        node.Should().NotBeNull("Oliver user node should exist in samples/Graph/Data/User/TestUser.json");
        node!.NodeType.Should().Be("User");

        // Enrich with node type — this should attach HubConfiguration
        var enriched = await nodeTypeService.EnrichWithNodeTypeAsync(node);
        enriched.HubConfiguration.Should().NotBeNull(
            "After enrichment, User node should have HubConfiguration from UserNodeType");
    }

    /// <summary>
    /// Verify that a hub can be created for the User/TestUser address
    /// and that it responds to PingRequest (hub is alive).
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task UserHub_Roland_CanBeCreated()
    {
        var client = GetClient();
        var rolandAddress = new Address("User/TestUser");

        var response = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(rolandAddress),
            TestContext.Current.CancellationToken);

        response.Should().NotBeNull("User/TestUser hub should be created and respond to ping");
    }

    /// <summary>
    /// The main test: resolve the Activity layout area on User/TestUser.
    /// This is the area registered by AddUserActivityViews().
    /// Regression test: compilation of User/_Source/Person.cs was overwriting
    /// the built-in HubConfiguration, losing layout areas.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ActivityArea_CanBeResolved_ForUserRoland()
    {
        var client = GetClient();
        var rolandAddress = new Address("User/TestUser");

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
            "Activity area should render for User/TestUser — AddUserActivityViews() must be invoked");
    }

    /// <summary>
    /// Simulates the production onboarding flow: creates a User node at runtime
    /// (not pre-registered via AddMeshNodes), then verifies the Activity area resolves.
    /// This tests the path: persistence → MeshCatalog.ResolvePathAsync → routing → hub creation.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task ActivityArea_WorksForRuntimeCreatedUser()
    {
        // Arrange — create a user node at runtime (simulating onboarding)
        var username = $"RuntimeUser_{Guid.NewGuid():N}"[..20];
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using (accessService.ImpersonateAsHub(Mesh))
        {
            await meshService.CreateNodeAsync(new MeshNode(username, "User")
            {
                Name = "Runtime Test User",
                NodeType = "User",
                State = MeshNodeState.Active,
                Content = new User { Email = "runtime@test.com", Bio = "Created at runtime" }
            });
        }

        // Act — request the Activity area (same as Index.razor does)
        var client = GetClient();
        var userAddress = new Address("User", username);

        // First, verify the hub can be created
        var pingResponse = await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(userAddress),
            TestContext.Current.CancellationToken);
        pingResponse.Should().NotBeNull($"User/{username} hub should be created and respond to ping");

        // Now request the Activity layout area
        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(UserActivityLayoutAreas.ActivityArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            userAddress, reference);

        var value = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();

        // Assert
        value.Should().NotBe(default(JsonElement),
            "Activity area should render for a runtime-created User node — " +
            "this simulates the production onboarding → Index.razor flow");
    }

    /// <summary>
    /// Also verify the Overview area works (baseline — this uses AddDefaultLayoutAreas).
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task OverviewArea_CanBeResolved_ForUserRoland()
    {
        var client = GetClient();
        var rolandAddress = new Address("User/TestUser");

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
            "Overview area should render for User/TestUser — AddDefaultLayoutAreas() must be invoked");
    }
}
