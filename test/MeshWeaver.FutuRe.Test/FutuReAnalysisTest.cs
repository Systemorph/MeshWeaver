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
using MeshWeaver.Hosting.Monolith;
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

namespace MeshWeaver.FutuRe.Test;

/// <summary>
/// Tests for the FutuRe insurance sample.
/// Verifies that Analysis views render at group level (Profitability)
/// and that BusinessUnit views render at local level (EuropeRe, AmericasIns).
/// </summary>
public class FutuReAnalysisTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverFutuReTests",
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
            .AddGraph()
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    /// <summary>
    /// Verifies that the Profitability Analysis hub renders its Overview area.
    /// This is the group-level view showing profitability charts.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task Profitability_Overview_ShouldRender()
    {
        var client = GetClient();
        var address = new Address("FutuRe/Profitability");

        Output.WriteLine("Initializing hub for FutuRe/Profitability...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        Output.WriteLine("Waiting for Overview area...");
        var value = await stream.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "Overview area should return a response for Profitability");
    }

    /// <summary>
    /// Verifies that the EuropeRe business unit renders its Overview area.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task EuropeRe_Overview_ShouldRender()
    {
        var client = GetClient();
        var address = new Address("FutuRe/EuropeRe");

        Output.WriteLine("Initializing hub for FutuRe/EuropeRe...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        Output.WriteLine("Waiting for EuropeRe Overview...");
        var value = await stream.Timeout(TimeSpan.FromSeconds(15)).FirstAsync();

        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "Overview area should render for EuropeRe business unit");
    }

    /// <summary>
    /// Verifies that the AmericasIns business unit renders its Overview area.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AmericasIns_Overview_ShouldRender()
    {
        var client = GetClient();
        var address = new Address("FutuRe/AmericasIns");

        Output.WriteLine("Initializing hub for FutuRe/AmericasIns...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        Output.WriteLine("Waiting for AmericasIns Overview...");
        var value = await stream.Timeout(TimeSpan.FromSeconds(15)).FirstAsync();

        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "Overview area should render for AmericasIns business unit");
    }

    /// <summary>
    /// Verifies that the KeyMetrics layout area renders KPI summary.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task KeyMetrics_ShouldRender()
    {
        var client = GetClient();
        var address = new Address("FutuRe/Profitability");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("KeyMetrics");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        Output.WriteLine("Waiting for KeyMetrics area...");
        var value = await stream.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "KeyMetrics area should render KPI summary");
    }

    /// <summary>
    /// Verifies that the ProfitabilityTable layout area renders LoB breakdown.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ProfitabilityTable_ShouldRender()
    {
        var client = GetClient();
        var address = new Address("FutuRe/Profitability");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("ProfitabilityTable");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        Output.WriteLine("Waiting for ProfitabilityTable area...");
        var value = await stream.Timeout(TimeSpan.FromSeconds(30)).FirstAsync();

        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "ProfitabilityTable area should render LoB breakdown table");
    }

    /// <summary>
    /// Verifies that TransactionMapping MeshNodes are loaded via IMeshQuery
    /// from both business unit namespaces.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TransactionMappings_ShouldLoadFromBothBusinessUnits()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var query = "nodeType:FutuRe/TransactionMapping namespace:FutuRe scope:descendants state:Active";
        Output.WriteLine($"Querying: {query}");

        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        Output.WriteLine($"Found {results.Count} TransactionMapping nodes");
        results.Count.Should().BeGreaterThanOrEqualTo(20,
            "Should find TransactionMapping nodes from both EuropeRe and AmericasIns");

        // Verify we have nodes from both namespaces
        var namespaces = results.Cast<MeshNode>().Select(n => n.Namespace).Distinct().ToList();
        Output.WriteLine($"Namespaces: {string.Join(", ", namespaces)}");
        namespaces.Should().Contain("FutuRe/EuropeRe/TransactionMapping");
        namespaces.Should().Contain("FutuRe/AmericasIns/TransactionMapping");
    }

    /// <summary>
    /// Verifies that AmountType MeshNodes are loaded via IMeshQuery.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AmountTypes_ShouldLoadFromMeshNodes()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var query = "nodeType:FutuRe/AmountType namespace:FutuRe/AmountType state:Active";
        Output.WriteLine($"Querying: {query}");

        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        Output.WriteLine($"Found {results.Count} AmountType nodes");
        results.Count.Should().Be(6, "Should find all 6 amount types");

        var names = results.Cast<MeshNode>().Select(n => n.Name).ToList();
        Output.WriteLine($"Amount types: {string.Join(", ", names)}");
        names.Should().Contain("Premium");
        names.Should().Contain("Claims");
    }

    /// <summary>
    /// Verifies that Currency MeshNodes are loaded via IMeshQuery.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Currencies_ShouldLoadFromMeshNodes()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var query = "nodeType:FutuRe/Currency namespace:FutuRe/Currency state:Active";
        Output.WriteLine($"Querying: {query}");

        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        Output.WriteLine($"Found {results.Count} Currency nodes");
        results.Count.Should().Be(3, "Should find USD, EUR, CHF");

        var ids = results.Cast<MeshNode>().Select(n => n.Id).ToList();
        ids.Should().Contain("USD");
        ids.Should().Contain("EUR");
        ids.Should().Contain("CHF");
    }

    /// <summary>
    /// Verifies that Country MeshNodes are loaded via IMeshQuery.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task Countries_ShouldLoadFromMeshNodes()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var query = "nodeType:FutuRe/Country namespace:FutuRe/Country state:Active";
        Output.WriteLine($"Querying: {query}");

        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        Output.WriteLine($"Found {results.Count} Country nodes");
        results.Count.Should().Be(3, "Should find US, CH, DE");

        var ids = results.Cast<MeshNode>().Select(n => n.Id).ToList();
        ids.Should().Contain("US");
        ids.Should().Contain("CH");
        ids.Should().Contain("DE");
    }

    /// <summary>
    /// Verifies that LineOfBusiness MeshNodes are loaded via IMeshQuery.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task LinesOfBusiness_ShouldLoadFromMeshNodes()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var query = "nodeType:FutuRe/LineOfBusiness namespace:FutuRe/LineOfBusiness scope:children state:Active";
        Output.WriteLine($"Querying: {query}");

        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        Output.WriteLine($"Found {results.Count} LineOfBusiness nodes");
        results.Count.Should().Be(10, "Should find all 10 group lines of business");
    }
}
