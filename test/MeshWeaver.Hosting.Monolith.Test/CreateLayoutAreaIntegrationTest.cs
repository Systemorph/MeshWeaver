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
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for CreateLayoutArea - the node creation workflow.
/// Tests the two-phase create flow:
/// 1. Parent node: Show type selection or Name+Description form, create transient node
/// 2. Transient node: Show ContentType editor with Confirm button
/// </summary>
[Collection("SamplesGraphData")]
public class CreateLayoutAreaIntegrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverCreateLayoutTests",
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
    /// Test that the Create area renders on a parent node (ProductLaunch) with type parameter.
    /// Should show the Name+Description form when ?type=ACME/Project/Todo is specified.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateArea_WithTypeParam_ShowsCreateForm()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        Output.WriteLine("Initializing hub...");
        // Initialize the hub first - this triggers dynamic compilation which can take time
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(parentAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        // Query parameters are passed via the Id property
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea)
        {
            Id = "?type=ACME%2FProject%2FTodo"
        };

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            reference);

        Output.WriteLine("Waiting for Create form to render...");
        // Use simpler pattern: just FirstAsync() which waits for any value
        var value = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();

        Output.WriteLine($"Received value");
        value.Should().NotBe(default(JsonElement), "Create form should render when type parameter is specified");
    }

    /// <summary>
    /// Test that the Create area renders on a parent node without type parameter.
    /// Should show type selection grid or a message.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CreateArea_WithoutTypeParam_ShowsTypeSelection()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        Output.WriteLine("Initializing hub...");
        // Initialize the hub first - this triggers dynamic compilation which can take time
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(parentAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.CreateNodeArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            reference);

        Output.WriteLine("Waiting for type selection to render...");
        // Use simpler pattern: just FirstAsync() which waits for any value
        var value = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();

        Output.WriteLine($"Received value");
        value.Should().NotBe(default(JsonElement), "Type selection should render when no type parameter is specified");
    }

    /// <summary>
    /// Test that the Overview area works for ProductLaunch (baseline test).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task OverviewArea_WorksForProductLaunch()
    {
        var client = GetClient();
        var parentAddress = new Address("ACME/ProductLaunch");

        Output.WriteLine("Initializing hub...");
        // Initialize the hub first - this triggers dynamic compilation which can take time
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(parentAddress),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            parentAddress,
            reference);

        Output.WriteLine("Waiting for Overview to render...");
        // Use simpler pattern: just FirstAsync() which waits for any value
        var value = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();

        Output.WriteLine($"Received value");
        value.Should().NotBe(default(JsonElement), "Overview should render for ProductLaunch");
    }

    /// <summary>
    /// Test that IMeshNodeFactory service is available for CreateLayoutArea.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task MeshNodeFactory_IsRegistered()
    {
        var nodeFactory = Mesh.ServiceProvider.GetRequiredService<IMeshNodeFactory>();
        nodeFactory.Should().NotBeNull("IMeshNodeFactory should be registered for CreateLayoutArea to work");

        await Task.CompletedTask;
    }

    /// <summary>
    /// Test that INodeTypeService service is available for CreateLayoutArea.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task NodeTypeService_IsRegistered()
    {
        var nodeTypeService = Mesh.ServiceProvider.GetService<INodeTypeService>();
        nodeTypeService.Should().NotBeNull("INodeTypeService should be registered for type selection");

        await Task.CompletedTask;
    }
}
