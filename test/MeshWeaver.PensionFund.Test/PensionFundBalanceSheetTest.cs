using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Activity;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Chart;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Fixture;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.PensionFund.Test;

/// <summary>
/// End-to-end tests for the PensionFund balance-sheet data-cube sample
/// (<c>samples/Graph/Data/PensionFund</c>, documented at
/// <c>Doc/DataMesh/DataCubes</c>). The BalanceSheet NodeType's Source — the
/// business-rules scopes included — is compiled DYNAMICALLY by the NodeType
/// compiler (which runs the scope generator), the dimension / fact nodes are
/// imported from the sample JSONs, and the views below evaluate the computed
/// positions through the real ScopeRegistry. A wrong formula, a generator
/// regression, or a broken dimension reference fails here, not in the portal.
/// </summary>
public class PensionFundBalanceSheetTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // All [Fact]s are read-only view renders over the shared sample graph —
    // share the mesh so the dynamic NodeType compile happens once.
    protected override bool ShareMeshAcrossTests => true;

    // Stable cache directory so compiled dynamic NodeType DLLs survive across
    // test runs (timestamped-subdir cache, same shape as FutuRe.Test).
    private static readonly string SharedCacheDirectory = Path.Combine(
        Path.GetTempPath(),
        "MeshWeaverPensionFundTests",
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

        // Same fixture shape as FutuReAnalysisTest — the canonical sample-render
        // test setup (activity tracking, row-level security, the 'storage'
        // content collection, per-node attachments mapping).
        return builder
            .UseMonolithMesh()
            .AddPartitionedFileSystemPersistence(dataDirectory)
            .AddPensionFund()
            .AddActivityTracking()
            .AddRowLevelSecurity()
            .ConfigureServices(services =>
            {
                services.Configure<CompilationCacheOptions>(o =>
                {
                    o.CacheDirectory = SharedCacheDirectory;
                    o.EnableDiskCache = true;
                });
                services.AddSingleton<IConfiguration>(configuration);
                services.AddLogging(b => b
                    .SetMinimumLevel(LogLevel.Information)
                    .AddFilter("MeshWeaver", LogLevel.Information)
                    .AddXUnitLogger(new TestOutputHelperAccessor { OutputHelper = Output }));
                return services;
            })
            .AddGraph()
            .ConfigureHub(hub => hub.AddContentCollection(_ => new ContentCollectionConfig
            {
                SourceType = "FileSystem",
                Name = "storage",
                BasePath = graphPath,
                ExposeInChildren = true,
            }))
            .ConfigureDefaultNodeHub(config =>
            {
                var nodePath = config.Address.ToString();
                return config
                    .MapContentCollection("attachments", "storage", $"attachments/{nodePath}")
                    .AddDefaultLayoutAreas();
            });
    }

    /// <summary>
    /// The cold dynamic compile of the BalanceSheet NodeType (5 Source files +
    /// scope generation) can exceed the default 60 s Ping timeout on slow CI
    /// runners — same extension as FutuRe.Test.
    /// </summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .WithRequestTimeout(TimeSpan.FromSeconds(120))
            .AddLayoutClient();

    // The report INSTANCE node (nodeType PensionFund/BalanceSheet) — layout
    // areas attach to instances of a NodeType, not to the NodeType definition
    // node (same shape as FutuRe/Analysis for FutuRe/GroupAnalysis).
    private const string BalanceSheetPath = "PensionFund/Statement";

    /// <summary>Watchdog headroom for the cold dynamic compile (sources + scope generator).</summary>
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(150);

    private UiControl RenderArea(string area, Func<UiControl, bool>? predicate = null)
    {
        var reference = new LayoutAreaReference(area);
        var stream = GetClient().GetWorkspace()
            .GetRemoteStream<JsonElement, LayoutAreaReference>(
                new Address(BalanceSheetPath), reference);

        var controls = stream.GetControlStream(area).Where(c => c is not null);

        // Surface what the area ACTUALLY emitted before asserting the predicate —
        // a "no data" markdown or an error control fails with its content, not
        // with an opaque timeout. 100 s budget covers the first-activation
        // dynamic compile (sources + scope generator).
        var first = controls
            .Should().Within(100.Seconds()).Emit($"area '{area}' must render a control");
        Output.WriteLine($"--- {area} first control: {first!.GetType().Name}: " +
            (first is MarkdownControl md ? md.Markdown?.ToString() : first.ToString()));

        if (predicate is null || predicate((UiControl)first))
            return (UiControl)first;

        return (UiControl)controls
            .Should().Within(30.Seconds())
            .Match(c => predicate((UiControl)c!),
                $"area '{area}' first emitted {first.GetType().Name} — waiting for the full render")!;
    }

    private static string MarkdownOf(UiControl control)
        => control.Should().BeOfType<MarkdownControl>().Subject.Markdown?.ToString() ?? string.Empty;

    /// <summary>
    /// The statement renders all three sections and the scope-computed rows:
    /// both years balance at 1,060.0 / 1,142.0 and the Funding Ratio — a
    /// Ratio position over Sum positions with negative weights — evaluates
    /// to ≈109.8% / ≈112.4%. The formulas live on the Position NODES; this
    /// asserts the dynamically compiled scopes fold them correctly.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void BalanceSheetStatement_RendersScopeComputedNumbers()
    {
        var control = RenderArea("BalanceSheetStatement",
            c => c is MarkdownControl m && (m.Markdown?.ToString() ?? "").Contains("FundingRatio"));

        var markdown = MarkdownOf(control);
        Output.WriteLine(markdown);

        // Dimension members render as rows (atomic) …
        markdown.Should().Contain("Cash").And.Contain("PensionersCapital");
        // … computed positions evaluate through the PositionValue scopes.
        Regex.IsMatch(markdown, @"1[',]060[.,]0").Should().BeTrue("2024 must total 1,060.0:\n" + markdown);
        Regex.IsMatch(markdown, @"1[',]142[.,]0").Should().BeTrue("2025 must total 1,142.0:\n" + markdown);
        Regex.IsMatch(markdown, @"109[.,]8").Should().BeTrue("2024 funding ratio must be ≈109.8%:\n" + markdown);
        Regex.IsMatch(markdown, @"112[.,]4").Should().BeTrue("2025 funding ratio must be ≈112.4%:\n" + markdown);
    }

    /// <summary>
    /// KeyFigures binds the BalanceSheetSummary scope: headline figures per
    /// year plus the balance check (assets = liabilities for both years).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public void KeyFigures_BalanceCheckPasses()
    {
        var control = RenderArea("KeyFigures",
            c => c is MarkdownControl m && (m.Markdown?.ToString() ?? "").Contains("Funding Ratio"));

        var markdown = MarkdownOf(control);
        Output.WriteLine(markdown);

        markdown.Should().Contain("✅", "the sample balance sheet balances by construction:\n" + markdown);
        Regex.IsMatch(markdown, @"920[.,]0").Should().BeTrue("2024 pension capital must be 920.0:\n" + markdown);
        Regex.IsMatch(markdown, @"964[.,]0").Should().BeTrue("2025 pension capital must be 964.0:\n" + markdown);
    }

    /// <summary>The asset-allocation pie chart renders from the atomic asset scopes.</summary>
    [Fact(Timeout = 120_000)]
    public void AssetAllocation_RendersPieChart()
    {
        var control = RenderArea("AssetAllocation", c => c is ChartControl);
        control.Should().BeOfType<ChartControl>();
    }

    /// <summary>The new-entry dialog opener (MeshNodePicker form) renders.</summary>
    [Fact(Timeout = 120_000)]
    public void NewEntryDialog_RendersButton()
    {
        var control = RenderArea("NewEntryDialog", c => c is ButtonControl);
        control.Should().BeOfType<ButtonControl>()
            .Subject.Data?.ToString().Should().Contain("New balance sheet entry");
    }
}
