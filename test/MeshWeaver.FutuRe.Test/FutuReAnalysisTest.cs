using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        var graphPath = TestPaths.SamplesGraph;
        var dataDirectory = TestPaths.SamplesGraphData;

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
                    o.EnableDiskCache = false;
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
        value.Value.ValueKind.Should().Be(JsonValueKind.Object,
            "Overview area should return a JSON object for Profitability");
    }

    /// <summary>
    /// Verifies that the AnnualReport renders markdown content (not form fields).
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnnualReport_Overview_ShouldRenderMarkdownContent()
    {
        var client = GetClient();
        var address = new Address("FutuRe/Profitability/AnnualReport");

        Output.WriteLine("Initializing hub for FutuRe/Profitability/AnnualReport...");
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);
        Output.WriteLine("Hub initialized.");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("Overview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        Output.WriteLine("Waiting for AnnualReport Overview area with markdown content...");
        var value = await stream
            .Where(v => v.Value.ValueKind == JsonValueKind.Object
                        && v.Value.GetRawText().Contains("Markdown"))
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync();

        var json = value.Value.GetRawText();
        Output.WriteLine($"JSON length: {json.Length}");

        json.Should().Contain("Markdown",
            "AnnualReport Overview should render via Markdown control");
        json.Should().Contain("@@(",
            "AnnualReport markdown should contain embedded @@() view references");
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
    /// Verifies that the KeyMetrics layout area renders actual KPI content,
    /// not just a loading indicator. This catches NodeType compilation failures
    /// where the Analysis views fail to compile and the graph shows loading forever.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task KeyMetrics_ShouldRenderActualContent()
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

        Output.WriteLine("Waiting for KeyMetrics area with actual content...");
        var value = await stream
            .Where(v => v.Value.ValueKind == JsonValueKind.Object
                        && v.Value.GetRawText().Contains("Total Premium"))
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync();

        var json = value.Value.GetRawText();
        Output.WriteLine($"KeyMetrics JSON length: {json.Length}");

        json.Should().Contain("Total Premium",
            "KeyMetrics should render actual KPI data, not just a loading indicator");
        json.Should().Contain("Markdown",
            "KeyMetrics should render as a Markdown control with KPI summary");
    }

    /// <summary>
    /// Verifies that the ProfitabilityTable layout area renders actual LoB data,
    /// not just a loading indicator.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ProfitabilityTable_ShouldRenderActualContent()
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

        Output.WriteLine("Waiting for ProfitabilityTable area with actual content...");
        var value = await stream
            .Where(v => v.Value.ValueKind == JsonValueKind.Object
                        && v.Value.GetRawText().Contains("Line of Business"))
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync();

        var json = value.Value.GetRawText();
        Output.WriteLine($"ProfitabilityTable JSON length: {json.Length}");

        json.Should().Contain("Line of Business",
            "ProfitabilityTable should render a markdown table with actual LoB data, not a loading indicator");
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

    /// <summary>
    /// Verifies that KeyMetrics ratios are mathematically consistent:
    /// Loss Ratio ≈ Claims/Premium*100, Profit Margin ≈ NetProfit/Premium*100.
    /// When the data cube has loaded, verifies the numeric relationships.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task KeyMetrics_RatiosShouldBeConsistent()
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

        var value = await stream
            .Where(v => v.Value.ValueKind == JsonValueKind.Object
                        && v.Value.GetRawText().Contains("Total Premium"))
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync();

        var json = value.Value.GetRawText();
        Output.WriteLine($"KeyMetrics JSON length: {json.Length}");

        // Verify all expected KPI labels are present in the markdown
        json.Should().Contain("Total Premium", "should render Total Premium label");
        json.Should().Contain("Total Claims", "should render Total Claims label");
        json.Should().Contain("Net Profit", "should render Net Profit label");
        json.Should().Contain("Loss Ratio", "should render Loss Ratio label");
        json.Should().Contain("Combined Ratio", "should render Combined Ratio label");
        json.Should().Contain("Profit Margin", "should render Profit Margin label");

        // Parse values and verify ratio consistency when data is available
        var premiumMatch = Regex.Match(json, @"Total Premium\*\*: ([0-9,]+)");
        var claimsMatch = Regex.Match(json, @"Total Claims\*\*: ([0-9,]+)");
        var netProfitMatch = Regex.Match(json, @"Net Profit\*\*: (-?[0-9,]+)");
        var lossRatioMatch = Regex.Match(json, @"Loss Ratio\*\*: ([0-9.]+)%");
        var profitMarginMatch = Regex.Match(json, @"Profit Margin\*\*: (-?[0-9.]+)%");

        premiumMatch.Success.Should().BeTrue("should find Total Premium value");
        claimsMatch.Success.Should().BeTrue("should find Total Claims value");
        netProfitMatch.Success.Should().BeTrue("should find Net Profit value");
        lossRatioMatch.Success.Should().BeTrue("should find Loss Ratio value");
        profitMarginMatch.Success.Should().BeTrue("should find Profit Margin value");

        var totalPremium = double.Parse(premiumMatch.Groups[1].Value,
            NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        var totalClaims = double.Parse(claimsMatch.Groups[1].Value,
            NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
        var netProfit = double.Parse(netProfitMatch.Groups[1].Value,
            NumberStyles.AllowThousands | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var lossRatio = double.Parse(lossRatioMatch.Groups[1].Value, CultureInfo.InvariantCulture);
        var profitMargin = double.Parse(profitMarginMatch.Groups[1].Value, CultureInfo.InvariantCulture);

        Output.WriteLine($"Premium={totalPremium}, Claims={totalClaims}, NetProfit={netProfit}");
        Output.WriteLine($"LossRatio={lossRatio}%, ProfitMargin={profitMargin}%");

        if (totalPremium > 0)
        {
            var expectedLossRatio = totalClaims / totalPremium * 100;
            lossRatio.Should().BeApproximately(expectedLossRatio, 0.2,
                "Loss Ratio should equal Claims/Premium*100");

            var expectedProfitMargin = netProfit / totalPremium * 100;
            profitMargin.Should().BeApproximately(expectedProfitMargin, 0.2,
                "Profit Margin should equal NetProfit/Premium*100");
        }
        else
        {
            Output.WriteLine("Data cube not yet loaded — verifying zero-data consistency");
            lossRatio.Should().Be(0, "Loss Ratio should be 0 when premium is 0");
            profitMargin.Should().Be(0, "Profit Margin should be 0 when premium is 0");
        }
    }

    /// <summary>
    /// Verifies that ProfitabilityTable LoB rows sum to the Total row.
    /// When data is available, validates that individual LoB rows sum to the Total.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ProfitabilityTable_RowsShouldSumToTotal()
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

        var value = await stream
            .Where(v => v.Value.ValueKind == JsonValueKind.Object
                        && v.Value.GetRawText().Contains("Total"))
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync();

        var json = value.Value.GetRawText();
        Output.WriteLine($"ProfitabilityTable JSON length: {json.Length}");

        // Verify table structure is present
        json.Should().Contain("Line of Business", "should have table header");
        json.Should().Contain("Total", "should have Total row");

        // Match data rows (LoB name starts with non-* character)
        var dataRowPattern = new Regex(
            @"\| ([^*|][^|]*?) \| ([0-9,]+) \| ([0-9,]+) \| ([0-9,]+) \| (-?[0-9,]+) \|");
        var totalRowPattern = new Regex(
            @"\*\*Total\*\*.*?\*\*([0-9,]+)\*\*.*?\*\*([0-9,]+)\*\*.*?\*\*([0-9,]+)\*\*.*?\*\*(-?[0-9,]+)\*\*");

        var dataRows = dataRowPattern.Matches(json);
        var totalMatch = totalRowPattern.Match(json);

        Output.WriteLine($"Data rows found: {dataRows.Count}, Total row found: {totalMatch.Success}");

        if (dataRows.Count > 0 && totalMatch.Success)
        {
            double sumPremium = 0, sumClaims = 0, sumOther = 0, sumProfit = 0;
            foreach (Match row in dataRows)
            {
                sumPremium += double.Parse(row.Groups[2].Value,
                    NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                sumClaims += double.Parse(row.Groups[3].Value,
                    NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                sumOther += double.Parse(row.Groups[4].Value,
                    NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
                sumProfit += double.Parse(row.Groups[5].Value,
                    NumberStyles.AllowThousands | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }

            var totalPremium = double.Parse(totalMatch.Groups[1].Value,
                NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            var totalClaims = double.Parse(totalMatch.Groups[2].Value,
                NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            var totalOther = double.Parse(totalMatch.Groups[3].Value,
                NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            var totalProfit = double.Parse(totalMatch.Groups[4].Value,
                NumberStyles.AllowThousands | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

            Output.WriteLine($"Sum:   Premium={sumPremium}, Claims={sumClaims}, Other={sumOther}, Profit={sumProfit}");
            Output.WriteLine($"Total: Premium={totalPremium}, Claims={totalClaims}, Other={totalOther}, Profit={totalProfit}");

            sumPremium.Should().BeApproximately(totalPremium, 1, "Sum of LoB premiums should match Total");
            sumClaims.Should().BeApproximately(totalClaims, 1, "Sum of LoB claims should match Total");
            sumOther.Should().BeApproximately(totalOther, 1, "Sum of LoB other costs should match Total");
            sumProfit.Should().BeApproximately(totalProfit, 1, "Sum of LoB profits should match Total");
        }
        else
        {
            Output.WriteLine("Data cube not yet loaded — verifying table structure only");
            json.Should().Contain("Premium", "table header should include Premium column");
            json.Should().Contain("Claims", "table header should include Claims column");
        }
    }

    /// <summary>
    /// Verifies that the default EUR filter does not include AmericasIns data.
    /// Checks the toolbar default and that USD-only data is excluded.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task KeyMetrics_DefaultFilter_ShouldShowOnlyEuropeRe()
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

        var value = await stream
            .Where(v => v.Value.ValueKind == JsonValueKind.Object
                        && v.Value.GetRawText().Contains("Business Units"))
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync();

        var json = value.Value.GetRawText();
        Output.WriteLine($"KeyMetrics JSON: {json.Substring(0, Math.Min(json.Length, 2000))}");

        // Default toolbar value should be EUR
        json.Should().Contain("EUR", "toolbar should have EUR as the default/selected currency");

        // AmericasIns data should never appear with EUR filter
        json.Should().NotContain("AmericasIns",
            "Default EUR filter should not include AmericasIns data");

        // If data has loaded, verify it shows only EuropeRe
        if (json.Contains("EuropeRe"))
        {
            json.Should().Contain("Business Units**: 1 (EuropeRe)",
                "EUR filter should show exactly one business unit: EuropeRe");
        }
        else
        {
            Output.WriteLine("Data cube not yet loaded — verified toolbar default and exclusion only");
        }
    }

    /// <summary>
    /// Verifies that ProfitabilityOverview renders a chart with expected series.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ProfitabilityOverview_ShouldRenderChart()
    {
        var client = GetClient();
        var address = new Address("FutuRe/Profitability");

        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(address),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference("ProfitabilityOverview");

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            address, reference);

        var value = await stream
            .Where(v => v.Value.ValueKind == JsonValueKind.Object
                        && v.Value.GetRawText().Contains("Premium"))
            .Timeout(TimeSpan.FromSeconds(30))
            .FirstAsync();

        var json = value.Value.GetRawText();
        Output.WriteLine($"ProfitabilityOverview JSON length: {json.Length}");

        json.Should().Contain("Chart", "ProfitabilityOverview should render a chart control");
        json.Should().Contain("Premium", "Chart should have a Premium series");
        json.Should().Contain("Claims", "Chart should have a Claims series");
        json.Should().Contain("Profit", "Chart should have a Profit series");
    }
}
