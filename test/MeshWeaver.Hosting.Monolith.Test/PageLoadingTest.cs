using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for page loading to prevent infinite spinner regression.
/// These tests verify that pages load within reasonable timeouts and don't hang.
/// The tests cover various node types to ensure reactive streams emit properly.
/// </summary>
[Collection("PageLoadingTests")]
public class PageLoadingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const int DefaultTimeoutSeconds = 20;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverPageLoadingTests", Guid.NewGuid().ToString(), ".mesh-cache");
        Directory.CreateDirectory(cacheDirectory);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Graph:Storage:SourceType"] = "FileSystem",
                ["Graph:Storage:BasePath"] = graphPath
            })
            .Build();

        var storageConfig = new ContentCollectionConfig
        {
            Name = "storage",
            SourceType = "FileSystem",
            BasePath = graphPath
        };

        return builder
            .UseMonolithMesh()
            .AddFileSystemPersistence(dataDirectory)
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o => o.CacheDirectory = cacheDirectory);
                services.AddSingleton<IConfiguration>(configuration);
                return services;
            })
            .ConfigureHub(hub => hub.AddContentCollections([storageConfig]))
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                config = config
                    .AddContentCollections()
                    .MapContentCollection("attachments", "storage", $"attachments/{nodePath}")
                    .MapContentCollection("content", "storage", $"content/{nodePath}");

                return config.AddDefaultLayoutAreas();
            })
            .AddGraph();
    }

    /// <summary>
    /// Helper method to test that a node's default area loads without hanging.
    /// </summary>
    private async Task AssertNodeLoadsWithoutHanging(string nodePath, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        var address = new Address(nodePath);
        var client = GetClient(c => c
            .AddLayoutClient(cc => cc)
            .AddContentCollections());

        Output.WriteLine($"Testing default area loading for {nodePath}");

        // Initialize the hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);
        Output.WriteLine($"Hub initialized for {nodePath}");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(string.Empty); // Default area

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);

        // Must emit within timeout - catches infinite spinner
        var changeItem = await stream.Timeout(TimeSpan.FromSeconds(timeoutSeconds)).FirstAsync();
        var value = changeItem.Value;

        var rawText = value.GetRawText();
        Output.WriteLine($"Received value: {rawText.Substring(0, Math.Min(200, rawText.Length))}...");

        value.Should().NotBe(default(JsonElement), $"{nodePath} should load and emit a value");
        Output.WriteLine($"{nodePath} loaded successfully");
    }

    /// <summary>
    /// Helper method to test that a specific layout area loads without hanging.
    /// </summary>
    private async Task AssertAreaLoadsWithoutHanging(string nodePath, string areaName, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        var address = new Address(nodePath);
        var client = GetClient(c => c
            .AddLayoutClient(cc => cc)
            .AddContentCollections());

        Output.WriteLine($"Testing {areaName} area loading for {nodePath}");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);
        Output.WriteLine($"Hub initialized for {nodePath}");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(areaName);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);

        var changeItem = await stream.Timeout(TimeSpan.FromSeconds(timeoutSeconds)).FirstAsync();
        var value = changeItem.Value;

        var rawText = value.GetRawText();
        Output.WriteLine($"Received value: {rawText.Substring(0, Math.Min(300, rawText.Length))}...");

        value.Should().NotBe(default(JsonElement), $"{nodePath}/{areaName} should emit a value");
        Output.WriteLine($"{nodePath}/{areaName} loaded successfully");
    }

    #region Organization Node Tests

    /// <summary>
    /// Tests that Organization nodes load without hanging.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData("ACME")]
    [InlineData("Systemorph")]
    public async Task Organization_LoadsWithoutHanging(string nodePath)
    {
        await AssertNodeLoadsWithoutHanging(nodePath);
    }

    #endregion

    #region Person Node Tests

    /// <summary>
    /// Tests that Person nodes load without hanging.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData("User/Alice")]
    [InlineData("User/Bob")]
    public async Task Person_LoadsWithoutHanging(string nodePath)
    {
        await AssertNodeLoadsWithoutHanging(nodePath);
    }

    #endregion

    #region Cornerstone Node Tests

    /// <summary>
    /// Tests that Cornerstone Insured nodes load without hanging.
    /// This specifically tests the CombineLatest fix with StartWith.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData("Cornerstone/Microsoft")]
    [InlineData("Cornerstone/EuropeanLogistics")]
    [InlineData("Cornerstone/GlobalManufacturing")]
    public async Task CornerstoneInsured_LoadsWithoutHanging(string nodePath)
    {
        await AssertNodeLoadsWithoutHanging(nodePath);
    }

    /// <summary>
    /// Tests that the PricingCatalog area loads for Cornerstone nodes.
    /// This directly tests the CombineLatest fix with StartWith.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CornerstoneInsured_PricingCatalog_LoadsWithoutHanging()
    {
        await AssertAreaLoadsWithoutHanging("Cornerstone/Microsoft", "PricingCatalog");
    }

    #endregion

    #region Markdown Node Tests

    /// <summary>
    /// Tests that Markdown documentation nodes load without hanging.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData("MeshWeaver/Documentation/DataMesh/UnifiedPath")]
    public async Task MarkdownNode_LoadsWithoutHanging(string nodePath)
    {
        await AssertNodeLoadsWithoutHanging(nodePath);
    }

    #endregion

    #region Northwind Node Tests

    /// <summary>
    /// Tests that Northwind nodes load without hanging.
    /// </summary>
    [Theory(Timeout = 30000)]
    [InlineData("Northwind")]
    public async Task NorthwindNode_LoadsWithoutHanging(string nodePath)
    {
        await AssertNodeLoadsWithoutHanging(nodePath);
    }

    #endregion

    #region Concurrent Loading Tests

    /// <summary>
    /// Tests that multiple concurrent requests to different node types don't cause hanging.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ConcurrentRequests_MultipleNodeTypes_AllLoadWithoutHanging()
    {
        var nodePaths = new[]
        {
            "Cornerstone/Microsoft",
            "ACME",
            "User/Alice",
            "Northwind"
        };

        var client = GetClient(c => c
            .AddLayoutClient(cc => cc)
            .AddContentCollections());
        Output.WriteLine("Testing concurrent page loading across node types");

        var tasks = new List<Task<(string Path, bool Success)>>();

        foreach (var nodePath in nodePaths)
        {
            var path = nodePath; // Capture for closure
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var address = new Address(path);
                    await client.AwaitResponse(
                        new PingRequest(),
                        o => o.WithTarget(address),
                        TestContext.Current.CancellationToken);

                    var workspace = client.GetWorkspace();
                    var reference = new LayoutAreaReference(string.Empty);
                    var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);

                    var changeItem = await stream.Timeout(TimeSpan.FromSeconds(DefaultTimeoutSeconds)).FirstAsync();
                    return (path, changeItem.Value.ValueKind != JsonValueKind.Undefined);
                }
                catch (Exception ex)
                {
                    Output.WriteLine($"Error loading {path}: {ex.Message}");
                    return (path, false);
                }
            }));
        }

        var results = await Task.WhenAll(tasks);

        foreach (var (path, success) in results)
        {
            success.Should().BeTrue($"{path} should load successfully");
            Output.WriteLine($"{path} loaded: {success}");
        }
    }

    #endregion
}
