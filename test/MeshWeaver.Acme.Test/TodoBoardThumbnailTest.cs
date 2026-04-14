using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using Memex.Portal.Shared;
using Memex.Portal.Shared.Settings;
using MeshWeaver.AI;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Activity;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Catalog;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Acme.Test;

/// <summary>
/// Tests that Todo board views (TodosByCategory, AllTasks) can render child hub thumbnails
/// without pre-initializing each hub via PingRequest. This reproduces the portal's behavior
/// where LayoutAreaControl items trigger hub creation on render.
/// </summary>
public class TodoBoardThumbnailTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverTodoBoardTests",
        ".mesh-cache");

    private static readonly string ContentBasePath = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverTodoBoardTests",
        "content");

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
        Directory.CreateDirectory(SharedCacheDirectory);
        Directory.CreateDirectory(ContentBasePath);

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
            .AddAcme()
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
            // Match portal's ConfigureMemexMesh order exactly:
            .AddRowLevelSecurity()
            .AddGraph()
            .AddOrganizationType()
            .AddAI()
            // Reproduce the portal's ConfigureDefaultNodeHub from MemexConfiguration.ConfigureMemexMesh
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                var basePath = Path.Combine(ContentBasePath, nodePath);
                var nodeContentConfig = new ContentCollectionConfig
                {
                    Name = "content",
                    SourceType = "FileSystem",
                    IsEditable = true,
                    BasePath = basePath,
                    Settings = new Dictionary<string, string>
                    {
                        ["BasePath"] = basePath
                    }
                };
                return config
                    .AddContentCollection(_ => nodeContentConfig)
                    .AddDefaultLayoutAreas()
                    .AddThreadsLayoutArea()
                    .AddApiTokensSettingsTab();
            })
            .AddActivityTracking();
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    /// <summary>
    /// TodosByCategory returns LayoutAreaControl items for each Todo.
    /// When rendered, each LayoutAreaControl triggers child hub creation.
    /// This test verifies those child hubs initialize successfully and render thumbnails.
    /// In the portal, this fails with "Hub 'ACME/ProductLaunch/Todo/X' initialization failed".
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task TodosByCategory_Thumbnails_ShouldInitializeHubs()
    {
        var client = GetClient();
        var workspace = client.GetWorkspace();
        var projectAddress = new Address("ACME/ProductLaunch");

        // Step 1: Get TodosByCategory catalog (this works — the Project hub renders fine)
        var reference = new LayoutAreaReference("TodosByCategory");
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            projectAddress,
            reference);

        Output.WriteLine("Waiting for TodosByCategory CatalogControl...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Where(c => c is CatalogControl { Groups.Count: > 0 })
            .Timeout(15.Seconds())
            .FirstAsync();

        var catalog = control.Should().BeOfType<CatalogControl>().Subject;
        Output.WriteLine($"Got CatalogControl with {catalog.Groups.Count} groups");

        // Step 2: Extract LayoutAreaControl items from catalog groups
        var layoutAreaControls = catalog.Groups
            .SelectMany(g => g.Items)
            .OfType<LayoutAreaControl>()
            .ToList();

        Output.WriteLine($"Found {layoutAreaControls.Count} LayoutAreaControl items:");
        foreach (var lac in layoutAreaControls.Take(5))
            Output.WriteLine($"  - Address={lac.Address}, Area={lac.Reference.Area}");

        layoutAreaControls.Should().NotBeEmpty("TodosByCategory should contain LayoutAreaControl thumbnail items");

        // Step 3: For each LayoutAreaControl, create a remote stream (simulating what Blazor does)
        // This triggers child hub creation — the exact path that fails in the portal
        var firstFew = layoutAreaControls.Take(3).ToList();
        foreach (var lac in firstFew)
        {
            var todoAddress = new Address(lac.Address.ToString()!);
            Output.WriteLine($"Rendering thumbnail for {todoAddress}...");

            var thumbnailStream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
                todoAddress,
                lac.Reference);

            // This is where the portal fails — the child hub initialization faults
            var thumbnailValue = await thumbnailStream
                .Timeout(15.Seconds())
                .FirstAsync();

            Output.WriteLine($"  Got value for {todoAddress}: {thumbnailValue.Value.ValueKind}");
            thumbnailValue.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
                $"Thumbnail for {todoAddress} should render without hub initialization failure");
        }

        Output.WriteLine("All thumbnails rendered successfully");
    }
}
