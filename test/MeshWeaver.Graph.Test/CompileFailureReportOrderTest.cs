using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using MeshWeaver.Compiler;
using MeshWeaver.Observability;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A compile failure must be ACTIONABLE from the incident, not only from a complete pod log —
/// issue #1840.
///
/// <para><b>What the issue reported, and what was actually true.</b> The ticket said
/// <c>MeshNodeCompilationService</c> "logs the fact of failure and the matched node list
/// <i>without</i> attaching the compiler's diagnostic messages", and asked whether
/// <see cref="CompilationException"/> "carries diagnostic details that are being discarded before
/// logging". Neither is so. The exception carries the complete Roslyn diagnostics (pinned by
/// <see cref="CompileFailureReportedOnceTest.Failed_emit_throws_the_full_diagnostics_and_logs_nothing"/>),
/// the funnel passes it to <c>LogError(ex, …)</c>, and the console formatter prints it. The
/// incident's own evidence table even names the exception type, which the parser can only read off
/// a printed exception line.</para>
///
/// <para><b>The real defect is ORDER against a fixed evidence budget.</b> The funnel's message put
/// the source-discovery report first — every executed query and <i>every</i> matched Code path.
/// For <c>MeshWeaver/samples/Graph/Data/Northwind/AnalyticsCatalog</c> that is 26 paths, ~2.4 kB,
/// and the red-log watcher keeps <see cref="MaxSampleLength"/> characters of a burst
/// (<c>BurstAggregator</c> → <c>Truncate</c>, <c>LogWatcherOptions.MaxSampleLength</c> = 2000). The
/// capture therefore ended <c>…[truncated]</c> partway down the listing — 17 of 26 paths in the
/// filed issue — and the exception, the only part naming a <c>CS####</c>, was never in the ticket.
/// Remote diagnosis was impossible for a reason no reader could see.</para>
///
/// <para>These tests pin the invariant that fixes it: <b>the compiler's verdict appears inside the
/// evidence budget, whatever the size of the source set</b>, and the part that scales with the
/// input is bounded in the log and kept in full where nothing truncates it.</para>
/// </summary>
public class CompileFailureReportOrderTest : IDisposable
{
    /// <summary>
    /// The watcher's per-burst evidence budget — <c>LogWatcherOptions.MaxSampleLength</c>. Mirrored
    /// as a literal because the watcher is a tool, not a referenced library; the value is asserted
    /// against the SHAPE of the real production burst below, so it is a scenario input rather than
    /// a magic number the fix is tuned to.
    /// </summary>
    private const int MaxSampleLength = 2000;

    /// <summary>The log category the incident was filed under.</summary>
    private const string Category = "MeshWeaver.Graph.Configuration.MeshNodeCompilationService";

    /// <summary>The node from the incident.</summary>
    private const string NodePath = "MeshWeaver/samples/Graph/Data/Northwind/AnalyticsCatalog";

    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), $"mesh-compile-report-{Guid.NewGuid():N}");

    public CompileFailureReportOrderTest() => Directory.CreateDirectory(_cacheDir);

    public void Dispose()
    {
        try { Directory.Delete(_cacheDir, recursive: true); }
        catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    // Instance, never static (AGENTS.md "no static collections"): the runtime's reference set, so
    // Roslyn produces the diagnostics it really produces rather than a hand-written string.
    private readonly IReadOnlyList<MetadataReference> _references =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

    /// <summary>
    /// A REAL failed compile of the incident's node, so the message under test is the message
    /// production produces — <c>CS0246</c> from a source file referencing a type nothing supplies,
    /// which is the shape the issue guessed at ("a missing reference, or an API change").
    /// </summary>
    private CompilationException RealCompilationFailure()
    {
        var compilation = CSharpCompilation.Create(
            "AnalyticsCatalog",
            syntaxTrees:
            [
                CSharpSyntaxTree.ParseText(
                    """
                    public static class NorthwindDataCube
                    {
                        public static NorthwindRow Build() => new NorthwindRow();
                    }
                    """)
            ],
            references: _references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return Assert.Throws<CompilationException>(() =>
            EmitPipeline.EmitCompilationToDirectory(
                compilation, "AnalyticsCatalog", NodePath, _cacheDir, CancellationToken.None));
    }

    /// <summary>The two source queries the incident logged, verbatim.</summary>
    private static IReadOnlyList<string> IncidentQueries() =>
    [
        $"namespace:{NodePath}/Source scope:subtree nodeType:Code",
        $"namespace:{NodePath}/Test scope:subtree nodeType:Code",
    ];

    /// <summary>The 26 Code nodes the incident matched — the volume that pushed the answer out.</summary>
    private static IReadOnlyList<string> IncidentMatchedPaths() =>
    [
        .. new[]
        {
            "CatalogContent", "Category", "Customer", "CustomerLayoutAreas", "DashboardLayoutAreas",
            "Employee", "EmployeeLayoutAreas", "FinancialLayoutAreas", "InventoryLayoutAreas",
            "MeshNodeDataLoader", "NorthwindDataCube", "NorthwindDataCubeExtensions",
            "NorthwindDataLoader", "NorthwindHelpers", "NorthwindYearToolbar", "Order",
            "OrderDetails", "Product", "ProductLayoutAreas", "Region", "Shipper", "Supplier",
            "SupplierLayoutAreas", "Territory", "TerritoryLayoutAreas", "OrderLayoutAreas",
        }.Select(n => $"{NodePath}/Source/{n}"),
    ];

    /// <summary>
    /// 🚨 THE regression test, and it reproduces the incident end to end: the console burst the
    /// portal writes → the watcher's burst aggregation → the evidence sample the ticket carries.
    /// The sample must name a <c>CS####</c>.
    ///
    /// <para>Against <c>origin/main</c> this fails with the sample ending <c>…[truncated]</c> inside
    /// the matched-node listing — byte-for-byte the evidence pasted into #1840.</para>
    /// </summary>
    [Fact]
    public void The_incidents_evidence_sample_carries_the_compiler_diagnostics()
    {
        var failure = RealCompilationFailure();
        var sample = EvidenceSampleFor(failure);

        sample.Should().Contain("CS0246",
            "the compiler's verdict is the only part of a compile failure an operator can act on, "
            + "so it must survive the watcher's 2000-character evidence budget — the whole point of "
            + "#1840, where 26 matched node paths pushed it out and the ticket said remote "
            + "diagnosis was impossible");
        sample.Should().Contain("NorthwindRow",
            "naming the symbol that failed to resolve is what turns the ticket into a fix");
        sample.Should().Contain(NodePath,
            "the report must name the node whose compile failed");
        sample.Should().Contain("scope:subtree nodeType:Code",
            "the source queries explain an empty or surprising source set and are small enough to "
            + "keep in full — they belong inside the budget too");
    }

    /// <summary>
    /// The ordering rule stated directly, independent of any particular budget: the compiler's
    /// verdict precedes the source-discovery context. Everything downstream of a logger truncates
    /// from the END, so ordering by actionability is what makes a budget survivable at all.
    /// </summary>
    [Fact]
    public void The_report_puts_the_compiler_verdict_before_the_source_discovery_context()
    {
        var report = CompileDiagnostics.FormatCompileFailureReport(
            NodePath, RealCompilationFailure().Message, IncidentQueries(), IncidentMatchedPaths());

        var diagnosticsAt = report.IndexOf("CS0246", StringComparison.Ordinal);
        var discoveryAt = report.IndexOf("Executed source queries", StringComparison.Ordinal);

        diagnosticsAt.Should().BeGreaterThan(-1, "the report carries the diagnostics");
        discoveryAt.Should().BeGreaterThan(-1, "the report still carries the source-discovery context");
        diagnosticsAt.Should().BeLessThan(discoveryAt,
            "a failure report is ordered by ACTIONABILITY — the verdict first, the context after, "
            + "because the context is what scales with the input and the tail is what gets cut");
    }

    /// <summary>
    /// The half of the fix that keeps the invariant true for ANY source set: the matched-node
    /// listing — the only part whose length is unbounded in the input — is capped in the log, and
    /// says where the complete list lives (the compile's ActivityLog, which is not size-capped).
    /// Without this, a 500-node package would put the diagnostics back outside the budget.
    /// </summary>
    [Fact]
    public void The_matched_node_listing_is_bounded_and_says_where_the_full_list_is()
    {
        var matched = Enumerable.Range(0, 500).Select(i => $"{NodePath}/Source/Type{i:000}").ToList();
        var report = CompileDiagnostics.FormatCompileFailureReport(
            NodePath, RealCompilationFailure().Message, IncidentQueries(), matched);

        report.Should().Contain("Matched Code nodes (500, first 8 listed):",
            "the count is the fact worth having in full; the listing is a sample");
        report.Should().Contain("… and 492 more",
            "a bounded listing must say how much it elided, or the reader cannot tell it was bounded");
        report.Should().Contain("ActivityLog",
            "…and where the complete list is, since it is kept somewhere nothing truncates");

        report.IndexOf("CS0246", StringComparison.Ordinal)
            .Should().BeLessThan(MaxSampleLength,
                "500 matched nodes must not push the diagnostics out of the budget — the bound is "
                + "what makes the ordering rule hold for any package size, not just for 26");
    }

    /// <summary>
    /// The empty-source-set case keeps its explanatory line. That message is the whole diagnosis
    /// for "the configuration lambda cannot reference types", and a bounded listing must not
    /// quietly drop the zero-length branch.
    /// </summary>
    [Fact]
    public void A_source_set_that_matched_nothing_still_explains_itself()
    {
        var report = CompileDiagnostics.FormatCompileFailureReport(
            NodePath, RealCompilationFailure().Message, IncidentQueries(), []);

        report.Should().Contain("Matched Code nodes (0):");
        report.Should().Contain("`sources` list points at them",
            "zero matches has its own actionable explanation and must keep it");
    }

    /// <summary>
    /// A compile failure that carries NO Roslyn diagnostics — the publish-lost-write
    /// <see cref="CompilationException"/> — must still lead with its own message rather than an
    /// empty section. The report is a projection of whatever verdict arrived, never a claim that
    /// diagnostics exist.
    /// </summary>
    [Fact]
    public void A_failure_without_roslyn_diagnostics_still_leads_with_its_own_verdict()
    {
        const string verdict =
            "Compilation succeeded but the emitted assembly for 'AnalyticsCatalog' could not be "
            + "published to '/data/cache' after 3 attempts";

        var report = CompileDiagnostics.FormatCompileFailureReport(
            NodePath, verdict, IncidentQueries(), IncidentMatchedPaths());

        var verdictAt = report.IndexOf(verdict, StringComparison.Ordinal);
        verdictAt.Should().BeGreaterThan(-1,
            "a failure with no Roslyn diagnostics still has a verdict, and the report carries it");
        verdictAt.Should().BeLessThan(report.IndexOf("Executed source queries", StringComparison.Ordinal),
            "the verdict leads whatever kind of failure it is");
    }

    /// <summary>
    /// Renders the burst exactly as <c>SimpleConsoleFormatter</c> writes it to pod stdout — the
    /// header, the six-space-indented message, then the exception — and runs it through the
    /// watcher's own aggregation, returning the evidence line the incident would carry.
    /// </summary>
    private static string EvidenceSampleFor(CompilationException failure)
    {
        // The message the funnel logs. `LogError(ex, "{CompileFailure}", report)` renders to
        // exactly `report`, so this IS the production message — not a re-spelling of it.
        var message = CompileDiagnostics.FormatCompileFailureReport(
            NodePath, failure.Message, IncidentQueries(), IncidentMatchedPaths());

        var at = DateTimeOffset.Parse("2026-08-18T13:37:59Z", System.Globalization.CultureInfo.InvariantCulture);
        var lines = ImmutableList.CreateBuilder<LogLine>();
        lines.Add(new LogLine(at, "memex-cloud", "memex-portal-676f69bc68-4c9gs", $"fail: {Category}[0]"));
        foreach (var line in message.Replace("\r\n", "\n").Split('\n'))
            lines.Add(new LogLine(at, "memex-cloud", "memex-portal-676f69bc68-4c9gs", "      " + line));
        foreach (var line in failure.ToString().Replace("\r\n", "\n").Split('\n'))
            lines.Add(new LogLine(at, "memex-cloud", "memex-portal-676f69bc68-4c9gs", "      " + line));

        var aggregation = BurstAggregator.Aggregate(
            lines.ToImmutable(), maxSamples: 10, maxSampleLength: MaxSampleLength);

        var report = aggregation.Reports.Should().ContainSingle(
            "the burst is one fault and must file as one incident").Which;
        report.ExceptionType.Should().Contain("CompilationException",
            "the exception IS printed — the issue's premise that it was discarded before logging "
            + "is wrong, and the evidence table on #1840 names the type for exactly this reason");
        return report.Samples[0].Line;
    }
}
