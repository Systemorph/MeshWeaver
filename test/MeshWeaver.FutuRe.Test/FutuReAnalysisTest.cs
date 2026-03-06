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
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Activity;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.ContentCollections;
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
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddFutuRe()
            .AddActivityLogs()
            .AddActivityTracking()
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
            .ConfigureHub(hub => hub.AddContentCollection(_ => new ContentCollectionConfig
            {
                SourceType = "FileSystem",
                Name = "storage",
                BasePath = graphPath,
                IsEditable = false
            }))
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                return config
                    .MapContentCollection("attachments", "storage", $"attachments/{nodePath}")
                    .AddDefaultLayoutAreas();
            });
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration)
            .AddLayoutClient();
    }

    [Fact(Timeout = 20000)]
    public async Task Profitability_Overview_ShouldRender()
    {
        var control = await GetControlAsync("FutuRe/Analysis", "Overview");
        control.Should().NotBeNull("Overview should render for group Analysis hub");
    }

    /// <summary>
    /// Verifies that the EuropeRe business unit renders its Overview area
    /// with actual content (not just an error control).
    /// </summary>
    [Fact(Timeout = 15000)]
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

        Output.WriteLine("Waiting for EuropeRe Overview control...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync(x => x is not null);

        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("Overview area should render for EuropeRe business unit");

        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(2,
            "BusinessUnit Overview should have H2 title + child areas (not just an error control)");
    }

    /// <summary>
    /// Verifies that the AmericasIns business unit renders its Overview area
    /// with actual content (not just an error control).
    /// </summary>
    [Fact(Timeout = 15000)]
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

        Output.WriteLine("Waiting for AmericasIns Overview control...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync(x => x is not null);

        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("Overview area should render for AmericasIns business unit");

        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(2,
            "BusinessUnit Overview should have H2 title + child areas (not just an error control)");
    }


    /// <summary>
    /// Verifies that TransactionMapping MeshNodes are loaded via IMeshQuery
    /// from both business unit namespaces.
    /// </summary>
    [Fact(Timeout = 15000)]
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
    [Fact(Timeout = 15000)]
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
    [Fact(Timeout = 15000)]
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
    [Fact(Timeout = 15000)]
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
    [Fact(Timeout = 15000)]
    public async Task LinesOfBusiness_ShouldLoadFromMeshNodes()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var query = "nodeType:FutuRe/LineOfBusiness namespace:FutuRe/LineOfBusiness scope:children state:Active";
        Output.WriteLine($"Querying: {query}");

        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        Output.WriteLine($"Found {results.Count} LineOfBusiness nodes");
        results.Count.Should().Be(10, "Should find all 10 group lines of business");
    }

    /// <summary>
    /// Verifies that the EuropeRe LineOfBusiness hub renders its Overview area.
    /// This tests runtime compilation of the LineOfBusiness data type.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task EuropeRe_LineOfBusiness_Overview_ShouldRender()
    {
        var client = GetClient();
        var address = new Address("FutuRe/EuropeRe/LineOfBusiness/HOUSEHOLD");

        Output.WriteLine("Initializing hub for FutuRe/EuropeRe/LineOfBusiness/HOUSEHOLD...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        Output.WriteLine("Waiting for Overview control...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync(x => x is not null);

        Output.WriteLine($"Received control: {control?.GetType().Name}");
        control.Should().NotBeNull("Overview area should render a control for EuropeRe LineOfBusiness HOUSEHOLD");

        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(2,
            "LineOfBusiness Overview should have H2 title + description (not just an error control)");
    }

    /// <summary>
    /// Verifies that the group-level LineOfBusiness Search area renders a MeshSearchControl
    /// and that executing its query returns the expected LoB instances.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task LineOfBusiness_Search_ShouldReturnGroupLoBs()
    {
        var client = GetClient();
        var address = new Address("FutuRe/LineOfBusiness");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Search");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync(x => x is not null);

        var searchControl = control.Should().BeOfType<MeshSearchControl>().Subject;
        searchControl.HiddenQuery.Should().NotBeNull("Search should have a hidden query");

        var hiddenQuery = searchControl.HiddenQuery!.ToString()!;
        Output.WriteLine($"Group-level hidden query: {hiddenQuery}");
        hiddenQuery.Should().Contain("namespace:FutuRe/LineOfBusiness",
            "Search query should scope to group LineOfBusiness namespace");

        // Execute the query and verify we get the 10 group-level LoBs
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(hiddenQuery)).ToListAsync();
        Output.WriteLine($"Group query returned {results.Count} results");
        results.Count.Should().Be(10, "Should find all 10 group lines of business");
    }

    /// <summary>
    /// Verifies that the EuropeRe LineOfBusiness Search area renders correctly,
    /// returns the 8 EuropeRe-specific LoB instances, and does NOT contain
    /// sibling nodes like Analysis or TransactionMapping.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task EuropeRe_LineOfBusiness_Search_ShouldReturn8LoBs()
    {
        var client = GetClient();
        var address = new Address("FutuRe/EuropeRe/LineOfBusiness");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Search");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync(x => x is not null);

        var searchControl = control.Should().BeOfType<MeshSearchControl>().Subject;
        searchControl.HiddenQuery.Should().NotBeNull("Search should have a hidden query");

        var hiddenQuery = searchControl.HiddenQuery!.ToString()!;
        Output.WriteLine($"EuropeRe hidden query: {hiddenQuery}");
        hiddenQuery.Should().Contain("namespace:FutuRe/EuropeRe/LineOfBusiness",
            "Search query should scope to EuropeRe LineOfBusiness namespace, not to FutuRe/EuropeRe (which would show siblings like Analysis and TransactionMapping)");

        // Must NOT contain a fallback query that scopes to the parent namespace
        hiddenQuery.Should().NotContain("path:FutuRe/EuropeRe scope:children",
            "Search should use NodeType mode (namespace:), not instance fallback (path: scope:children)");

        // Execute the query and verify we get the 8 EuropeRe LoBs
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(hiddenQuery)).ToListAsync();
        var names = results.Cast<MeshNode>().Select(n => n.Name).ToList();
        Output.WriteLine($"EuropeRe query returned {results.Count} results: {string.Join(", ", names)}");
        results.Count.Should().Be(8, "Should find all 8 EuropeRe lines of business");

        // Verify NO sibling nodes are returned (the bug that showed Analysis/TransactionMapping)
        var ids = results.Cast<MeshNode>().Select(n => n.Id).ToList();
        ids.Should().NotContain("Analysis", "Search should not return the Analysis sibling node");
        ids.Should().NotContain("TransactionMapping", "Search should not return the TransactionMapping sibling node");
    }

    // ── Layout Area Catalog ──

    [Fact(Timeout = 20000)]
    public async Task GroupAnalysis_LayoutAreas_ShouldRenderCatalog()
    {
        var control = await GetControlAsync("FutuRe/Analysis", "LayoutAreas");
        control.Should().NotBeNull("LayoutAreas catalog should render for group Analysis hub");
    }

    [Fact(Timeout = 20000)]
    public async Task LocalAnalysis_LayoutAreas_ShouldRenderCatalog()
    {
        var control = await GetControlAsync("FutuRe/EuropeRe/Analysis", "LayoutAreas");
        control.Should().NotBeNull("LayoutAreas catalog should render for local EuropeRe Analysis hub");

        // The LayoutAreaCatalog returns a StackControl with category headers (H2) and LayoutGridControls
        var stack = control.Should().BeOfType<StackControl>().Subject;
        Output.WriteLine($"LayoutAreas catalog has {stack.Areas?.Count ?? 0} areas (NamedAreaControls)");
        foreach (var area in stack.Areas ?? [])
            Output.WriteLine($"  area: {area.Area}");

        // The catalog creates: [H2("Profitability"), LayoutGrid(7 tiles)] = 2+ areas
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(2,
            "Catalog should have at least one category header (H2) + one grid of layout area tiles");

        // The control should NOT be a MeshSearchControl (which would indicate Overview/Search fallback)
        control.Should().NotBeOfType<MeshSearchControl>(
            "LayoutAreas should show a catalog of profitability views, not a search/overview");
    }

    /// <summary>
    /// Verifies that the default area (null area = browser navigation) for EuropeRe Analysis
    /// resolves to LayoutAreas, not Overview. This is what users see when navigating to the page.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task EuropeRe_Analysis_DefaultArea_ShouldResolveToLayoutAreas()
    {
        var client = GetClient();
        var address = new Address("FutuRe/EuropeRe/Analysis");

        Output.WriteLine("Initializing hub for FutuRe/EuropeRe/Analysis...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        // null area = mimics browser navigation to /FutuRe/EuropeRe/Analysis
        var reference = new LayoutAreaReference((string?)null);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        Output.WriteLine("Waiting for default area control...");
        // The default area stores a NamedAreaControl at the "" key
        var control = await stream
            .GetControlStream("")
            .Timeout(TimeSpan.FromSeconds(15))
            .FirstAsync(x => x is not null);

        Output.WriteLine($"Default area control type: {control?.GetType().Name}");

        // When the default area is resolved, a NamedAreaControl pointing to the resolved area
        // is stored at key "". Verify it points to "LayoutAreas", not "Overview".
        var namedArea = control.Should().BeOfType<NamedAreaControl>().Subject;
        Output.WriteLine($"Default area resolves to: {namedArea.Area}");
        namedArea.Area.Should().Be("LayoutAreas",
            "Default area for Analysis hub should be 'LayoutAreas' (profitability catalog), not 'Overview'");
    }

    // ── Local Analysis Hub (EuropeRe) ──

    [Fact(Timeout = 20000)]
    public async Task EuropeRe_KeyMetrics_ShouldHaveNonZeroData()
    {
        var control = await GetControlAsync("FutuRe/EuropeRe/Analysis", "KeyMetrics");
        var md = AssertMarkdownWithNonZeroNumbers(control, "EuropeRe KeyMetrics");
        md.Should().Contain("Total Premium", "KeyMetrics should show premium");
        md.Should().Contain("Loss Ratio", "KeyMetrics should show loss ratio");
    }

    [Fact(Timeout = 20000)]
    public async Task EuropeRe_ProfitabilityTable_ShouldHaveNonZeroData()
    {
        var control = await GetControlAsync("FutuRe/EuropeRe/Analysis", "ProfitabilityTable");
        var md = AssertMarkdownWithNonZeroNumbers(control, "EuropeRe ProfitabilityTable");
        md.Should().Contain("Line of Business", "table should have headers");
        md.Should().Contain("Total", "table should have totals row");
    }

    [Fact(Timeout = 20000)]
    public async Task EuropeRe_ProfitabilityOverview_ShouldRenderChart()
    {
        var control = await GetControlAsync("FutuRe/EuropeRe/Analysis", "ProfitabilityOverview");
        control.Should().BeOfType<ChartControl>("ProfitabilityOverview should be a chart");
    }

    [Fact(Timeout = 20000)]
    public async Task EuropeRe_EstimateVsActual_ShouldHaveData()
    {
        var control = await GetControlAsync("FutuRe/EuropeRe/Analysis", "EstimateVsActual");
        control.Should().NotBeNull("EstimateVsActual should render for EuropeRe");
    }

    [Fact(Timeout = 20000)]
    public async Task EuropeRe_ProfitByLoB_ShouldRenderChart()
    {
        var control = await GetControlAsync("FutuRe/EuropeRe/Analysis", "ProfitByLoB");
        control.Should().BeOfType<ChartControl>("ProfitByLoB should be a chart");
    }

    [Fact(Timeout = 20000)]
    public async Task EuropeRe_LossRatio_ShouldRenderChart()
    {
        var control = await GetControlAsync("FutuRe/EuropeRe/Analysis", "LossRatio");
        control.Should().BeOfType<ChartControl>("LossRatio should be a chart");
    }

    [Fact(Timeout = 20000)]
    public async Task EuropeRe_QuarterlyTrend_ShouldRenderChart()
    {
        var control = await GetControlAsync("FutuRe/EuropeRe/Analysis", "QuarterlyTrend");
        control.Should().BeOfType<ChartControl>("QuarterlyTrend should be a chart");
    }

    // ── Group Analysis Hub (FutuRe/Analysis) ──

    [Fact(Timeout = 60000)]
    public async Task Group_KeyMetrics_ShouldHaveNonZeroData()
    {
        var control = await GetControlAsync("FutuRe/Analysis", "KeyMetrics",
            waitForData: true, timeoutSeconds: 50);
        var md = AssertMarkdownWithNonZeroNumbers(control, "Group KeyMetrics");
        md.Should().Contain("Total Premium", "KeyMetrics should show premium");
        md.Should().Contain("Business Units", "Group KeyMetrics should show BU count");
    }

    [Fact(Timeout = 30000)]
    public async Task Group_ProfitabilityTable_ShouldHaveNonZeroData()
    {
        var control = await GetControlAsync("FutuRe/Analysis", "ProfitabilityTable",
            waitForData: true, timeoutSeconds: 25);
        var md = AssertMarkdownWithNonZeroNumbers(control, "Group ProfitabilityTable");
        md.Should().Contain("Line of Business", "table should have headers");
        md.Should().Contain("Total", "table should have totals row");
    }

    [Fact(Timeout = 30000)]
    public async Task Group_ProfitabilityOverview_ShouldRenderChart()
    {
        var control = await GetControlAsync("FutuRe/Analysis", "ProfitabilityOverview");
        control.Should().BeOfType<ChartControl>("ProfitabilityOverview should be a chart");
    }

    [Fact(Timeout = 30000)]
    public async Task Group_EstimateVsActual_ShouldHaveData()
    {
        var control = await GetControlAsync("FutuRe/Analysis", "EstimateVsActual");
        control.Should().NotBeNull("EstimateVsActual should render for group hub");
    }

    [Fact(Timeout = 30000)]
    public async Task Group_ProfitByLoB_ShouldRenderChart()
    {
        var control = await GetControlAsync("FutuRe/Analysis", "ProfitByLoB");
        control.Should().BeOfType<ChartControl>("ProfitByLoB should be a chart");
    }

    [Fact(Timeout = 30000)]
    public async Task Group_LossRatio_ShouldRenderChart()
    {
        var control = await GetControlAsync("FutuRe/Analysis", "LossRatio");
        control.Should().BeOfType<ChartControl>("LossRatio should be a chart");
    }

    [Fact(Timeout = 30000)]
    public async Task Group_QuarterlyTrend_ShouldRenderChart()
    {
        var control = await GetControlAsync("FutuRe/Analysis", "QuarterlyTrend");
        control.Should().BeOfType<ChartControl>("QuarterlyTrend should be a chart");
    }

    // ── Business Unit Layout Areas ──

    /// <summary>
    /// Verifies that the EuropeRe Search area renders with child nodes.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task EuropeRe_Search_ShouldRenderWithChildren()
    {
        var client = GetClient();
        var address = new Address("FutuRe/EuropeRe");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Search");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(TimeSpan.FromSeconds(10))
            .FirstAsync(x => x is not null);

        var searchControl = control.Should().BeOfType<MeshSearchControl>().Subject;
        var hiddenQuery = searchControl.HiddenQuery!.ToString()!;
        Output.WriteLine($"EuropeRe Search query: {hiddenQuery}");

        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(hiddenQuery)).ToListAsync();
        Output.WriteLine($"EuropeRe Search returned {results.Count} results");
        results.Count.Should().BeGreaterThanOrEqualTo(2,
            "EuropeRe should have at least LineOfBusiness and TransactionMapping child nodes");
    }

    // ── Node Existence Verification ──

    /// <summary>
    /// Verifies that all FutuRe NodeType definitions exist.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task AllNodeTypes_ShouldExist()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var query = "namespace:FutuRe nodeType:NodeType scope:children state:Active";
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();
        var ids = results.Cast<MeshNode>().Select(n => n.Id).ToList();

        Output.WriteLine($"Found {results.Count} NodeTypes: {string.Join(", ", ids)}");

        ids.Should().Contain("GroupAnalysis");
        ids.Should().Contain("LocalAnalysis");
        ids.Should().Contain("BusinessUnit");
        ids.Should().Contain("LineOfBusiness");
        ids.Should().Contain("TransactionMapping");
        ids.Should().Contain("AmountType");
        ids.Should().Contain("Currency");
        ids.Should().Contain("Country");
        ids.Should().Contain("Report");
    }

    /// <summary>
    /// Verifies that both BusinessUnit instances exist with correct properties.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task BusinessUnits_ShouldExistWithProperties()
    {
        var meshQuery = Mesh.ServiceProvider.GetRequiredService<IMeshQuery>();

        var query = "nodeType:FutuRe/BusinessUnit namespace:FutuRe scope:children state:Active";
        var results = await meshQuery.QueryAsync(MeshQueryRequest.FromQuery(query)).ToListAsync();

        Output.WriteLine($"Found {results.Count} BusinessUnits");
        results.Count.Should().Be(3, "Should have EuropeRe, AmericasIns, and AsiaRe");

        var ids = results.Cast<MeshNode>().Select(n => n.Id).ToList();
        ids.Should().Contain("EuropeRe");
        ids.Should().Contain("AmericasIns");
        ids.Should().Contain("AsiaRe");
    }

    // ── Report ──

    /// <summary>
    /// Verifies that the AnnualReport node exists and its Overview area renders.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task AnnualReport_Overview_ShouldRender()
    {
        var client = GetClient();
        var address = new Address("FutuRe/Analysis/AnnualReport");

        Output.WriteLine("Initializing hub for FutuRe/Analysis/AnnualReport...");
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
        var value = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();

        Output.WriteLine($"Received value: {value.Value.ValueKind}");
        value.Value.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "AnnualReport Overview should render the report content");
    }

    // ── Activity Logs ──

    /// <summary>
    /// Verifies that activity logs from the _activitylogs filesystem partition
    /// are loaded and returned by GetRecentActivityLogsAsync.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task ActivityLogs_ShouldLoadFromFileSystem()
    {
        var storageAdapter = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceServiceCore>();

        // Create PersistenceActivityLogStore directly to bypass DI ordering
        // (InMemoryActivityLogStore is registered first via TryAddSingleton in DataExtensions)
        var activityLogStore = new PersistenceActivityLogStore(persistence, adapter: storageAdapter);

        var logs = await activityLogStore.GetRecentActivityLogsAsync(limit: 30);

        Output.WriteLine($"Found {logs.Count} activity logs");
        foreach (var log in logs)
            Output.WriteLine($"  [{log.Category}] {log.User?.DisplayName} - {log.HubPath} ({log.Status})");

        logs.Count.Should().BeGreaterThanOrEqualTo(4,
            "Should find at least 4 activity logs (2 EuropeRe + 2 AmericasIns from _activitylogs/)");

        // Verify we have logs from both entities
        var hubPaths = logs.Select(l => l.HubPath).ToList();
        hubPaths.Should().Contain(p => p != null && p.Contains("EuropeRe"),
            "Should have activity logs from EuropeRe");
        hubPaths.Should().Contain(p => p != null && p.Contains("AmericasIns"),
            "Should have activity logs from AmericasIns");

        // Verify both categories are present
        var categories = logs.Select(l => l.Category).Distinct().ToList();
        categories.Should().Contain("Approval");
        categories.Should().Contain("DataUpdate");
    }

    // ── Helpers ──

    private async Task<UiControl?> GetControlAsync(
        string addressPath, string areaName,
        bool waitForData = false, int timeoutSeconds = 15)
    {
        var client = GetClient();
        var address = new Address(addressPath);

        Output.WriteLine($"Initializing hub for {addressPath}...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(areaName);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        Output.WriteLine($"Waiting for {areaName} at {addressPath}{(waitForData ? " (with data)" : "")}...");
        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(TimeSpan.FromSeconds(timeoutSeconds))
            .FirstAsync(x => x is not null && (!waitForData || HasNonTrivialData(x)));

        Output.WriteLine($"Received {areaName}: {control?.GetType().Name}");
        control.Should().NotBeNull($"{areaName} should render at {addressPath}");
        return control;
    }

    /// <summary>
    /// Checks if a control has meaningful data (not all zeros).
    /// Used to wait for PartitionedHubDataSource to deliver data before asserting.
    /// </summary>
    private static bool HasNonTrivialData(UiControl? control)
    {
        if (control is not MarkdownControl md)
            return true; // Charts, stacks, etc. are always considered ready

        var text = md.Markdown?.ToString() ?? "";
        return System.Text.RegularExpressions.Regex.Matches(text, @"\d[\d,]*")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Value.Replace(",", ""))
            .Where(s => double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture))
            .Any(v => v > 0);
    }

    /// <summary>
    /// Asserts that the control is a MarkdownControl and that it contains
    /// at least one number greater than zero (i.e., not all zeros).
    /// Returns the markdown text for further assertions.
    /// </summary>
    private string AssertMarkdownWithNonZeroNumbers(UiControl? control, string context)
    {
        var mdControl = control.Should().BeOfType<MarkdownControl>(
            $"{context} should render as MarkdownControl").Subject;

        var markdown = mdControl.Markdown?.ToString() ?? string.Empty;
        Output.WriteLine($"{context} markdown:\n{markdown}");

        markdown.Should().NotBeNullOrWhiteSpace($"{context} markdown should not be empty");

        // Extract numbers from the markdown — look for formatted numbers like "78,750,000"
        var numbers = System.Text.RegularExpressions.Regex.Matches(markdown, @"[\d,]+\.\d+|[\d,]+")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Value.Replace(",", ""))
            .Where(s => double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        Output.WriteLine($"{context} extracted numbers: {string.Join(", ", numbers.Take(10))}");

        numbers.Should().NotBeEmpty($"{context} should contain numeric values");
        numbers.Should().Contain(n => n > 0, $"{context} should have at least one non-zero value");

        return markdown;
    }

}
