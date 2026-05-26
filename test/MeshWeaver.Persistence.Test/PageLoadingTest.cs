using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using System.Reactive.Threading.Tasks;
namespace MeshWeaver.Persistence.Test;

/// <summary>
/// Tests for page loading to prevent infinite spinner regression.
/// These tests verify that pages load within reasonable timeouts and don't hang.
/// The tests cover various node types to ensure reactive streams emit properly.
/// </summary>
[Collection("PageLoadingTests")]
public class PageLoadingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    private const int DefaultTimeoutSeconds = 20;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;
        // Stable cache directory (no Guid.NewGuid()) so the compiled NodeType
        // assemblies survive across test runs. The source files at
        // TestPaths.SamplesGraph have stable LastModified — combined with
        // CompilationCacheService.TryGetLatestCachedDllPath (timestamped
        // subdir lookup), the second-and-onwards test invocations skip the
        // 9 s Roslyn cold compile of Cornerstone/Insured / Northwind / etc.
        // and complete inside the 10 s per-test timeout.
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "MeshWeaverPageLoadingTests", ".mesh-cache");
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
            // NOTE: storage stays ExposeInChildren=false here. The test
            // doesn't query GetAllCollectionConfigs from descendant hubs;
            // setting ExposeInChildren=true cascades visibility of the
            // per-node mapped wrappers ("attachments", "content") through
            // the hierarchy, which slowed down LoadsWithoutHanging assertions
            // past their 10 s budget. If a future test needs cross-hub
            // visibility, prefer a per-test fixture override.
        };

        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddSpaceType()
            .AddAcme()
            .AddSystemorph()
            .AddCornerstone()
            .AddUserData()
            .AddNorthwind()
            .AddMeshWeaverDocs()
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var address = new Address(nodePath);
        Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] new Address({nodePath}) done");
        var client = GetClient(c => c
            .AddLayoutClient(cc => cc)
            .AddContentCollections());
        Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] GetClient done");

        Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] Sending PingRequest to {nodePath}");

        await client.Observe(new PingRequest(), o => o.WithTarget(address)).FirstAsync().ToTask(TestContext.Current.CancellationToken);
        Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] Ping returned for {nodePath}");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(string.Empty);
        Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] About to GetRemoteStream for {nodePath}");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);
        Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] GetRemoteStream returned (subscribed below)");

        var changeItem = await stream.Timeout(TimeSpan.FromSeconds(timeoutSeconds)).FirstAsync();
        Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] stream.FirstAsync returned");
        var value = changeItem.Value;

        var rawText = value.GetRawText();
        Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] {nodePath} text len={rawText.Length}");

        value.Should().NotBe(default(JsonElement), $"{nodePath} should load and emit a value");
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

        await client.Observe(new PingRequest(), o => o.WithTarget(address)).FirstAsync().ToTask(TestContext.Current.CancellationToken);
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

    #region Space Node Tests

    /// <summary>
    /// Tests that Space nodes load without hanging.
    /// 60 s timeout (vs 10 s default) because ACME's first activation triggers
    /// cold compile of its three child NodeTypes (Article, Project, Todo) +
    /// loads access assignments. Subsequent runs hit the timestamped-subdir
    /// cache via CompilationCacheService.TryGetLatestCachedDllPath.
    /// </summary>
    [Theory(Timeout = 60000)]
    [InlineData("ACME")]
    [InlineData("Systemorph")]
    public async Task Space_LoadsWithoutHanging(string nodePath)
    {
        await AssertNodeLoadsWithoutHanging(nodePath);
    }

    #endregion

    #region Person Node Tests

    /// <summary>
    /// Tests that Person nodes load without hanging.
    /// </summary>
    [Theory(Timeout = 60000)]
    [InlineData("Cornerstone/Microsoft")]
    [InlineData("Cornerstone/Tesla")]
    public async Task Person_LoadsWithoutHanging(string nodePath)
    {
        await AssertNodeLoadsWithoutHanging(nodePath);
    }

    /// <summary>
    /// Tests that the Activity area loads for User nodes.
    /// This verifies UserActivityLayoutAreas is properly registered via UserNodeType.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task User_Activity_LoadsWithoutHanging()
    {
        await AssertAreaLoadsWithoutHanging("Cornerstone/Microsoft", "Activity");
    }

    #endregion

    #region Cornerstone Node Tests

    /// <summary>
    /// Tests that Cornerstone Insured nodes load without hanging.
    /// This specifically tests the CombineLatest fix with StartWith.
    /// </summary>
    [Theory(Timeout = 60000)]
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
    [Fact(Timeout = 60000)]
    public async Task CornerstoneInsured_PricingCatalog_LoadsWithoutHanging()
    {
        await AssertAreaLoadsWithoutHanging("Cornerstone/Microsoft", "PricingCatalog");
    }

    #endregion

    #region Markdown Node Tests

    /// <summary>
    /// Tests that Markdown documentation nodes load without hanging.
    /// </summary>
    [Theory(Timeout = 60000)]
    [InlineData("MeshWeaver/Welcome")]
    public async Task MarkdownNode_LoadsWithoutHanging(string nodePath)
    {
        await AssertNodeLoadsWithoutHanging(nodePath);
    }

    #endregion

    #region Northwind Node Tests

    /// <summary>
    /// Tests that Northwind nodes load without hanging.
    /// </summary>
    [Theory(Timeout = 60000)]
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
    [Fact(Timeout = 60000)]
    public async Task ConcurrentRequests_MultipleNodeTypes_AllLoadWithoutHanging()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var nodePaths = new[]
        {
            "Cornerstone/Microsoft",
            "ACME",
            "Cornerstone/Microsoft",
            "Northwind"
        };

        var client = GetClient(c => c
            .AddLayoutClient(cc => cc)
            .AddContentCollections());
        Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] GetClient done; starting concurrent loads");

        var tasks = new List<Task<(string Path, bool Success)>>();

        foreach (var nodePath in nodePaths)
        {
            var path = nodePath; // Capture for closure
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var address = new Address(path);
                    Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] {path}: Ping starting");
                    await client.Observe(new PingRequest(), o => o.WithTarget(address)).FirstAsync().ToTask(TestContext.Current.CancellationToken);
                    Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] {path}: Ping returned");

                    var workspace = client.GetWorkspace();
                    var reference = new LayoutAreaReference(string.Empty);
                    var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(address, reference);
                    Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] {path}: GetRemoteStream returned, awaiting FirstAsync");

                    var changeItem = await stream.Timeout(TimeSpan.FromSeconds(DefaultTimeoutSeconds)).FirstAsync();
                    Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] {path}: FirstAsync returned");
                    return (path, changeItem.Value.ValueKind != JsonValueKind.Undefined);
                }
                catch (Exception ex)
                {
                    Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] {path}: Error: {ex.GetType().Name}: {ex.Message}");
                    return (path, false);
                }
            }));
        }

        var results = await Task.WhenAll(tasks);

        foreach (var (path, success) in results)
        {
            success.Should().BeTrue($"{path} should load successfully");
            Output.WriteLine($"[{sw.ElapsedMilliseconds}ms] {path} loaded: {success}");
        }
    }

    #endregion
}
