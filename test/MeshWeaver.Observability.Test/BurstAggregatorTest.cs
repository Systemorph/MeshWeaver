using System.Collections.Immutable;
using MeshWeaver.Observability;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// Aggregation is what turns "ten thousand red lines" into "one ticketable fact", so these tests
/// pin the collapsing behaviour and the burst boundaries it depends on.
/// </summary>
public class BurstAggregatorTest
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 7, 10, 0, 0, TimeSpan.Zero);

    private static LogLine Entry(string line, int second = 0, string pod = "memex-portal-abc") =>
        new(T0.AddSeconds(second), "memex", pod, line);

    /// <summary>The reports out of an aggregation — the counts have their own tests below.</summary>
    private static ImmutableList<LogIncidentReport> Reports(
        IReadOnlyList<LogLine> entries, int maxSamples, int maxSampleLength,
        IReadOnlyList<string>? ignoreCategories = null, int maxVariantsPerSite = 0) =>
        BurstAggregator.Aggregate(entries, maxSamples, maxSampleLength, ignoreCategories, maxVariantsPerSite)
            .Reports;

    private static IReadOnlyList<LogLine> OneFault(int second, string nodeId, string pod = "memex-portal-abc") =>
    [
        Entry("fail: MeshWeaver.Data.MeshDataSource[0]", second, pod),
        Entry($"      Update failed for node rbuergi/Foo/{nodeId}", second, pod),
        Entry("      System.InvalidOperationException: Sequence contains no elements", second, pod),
        Entry("         at MeshWeaver.Data.MeshDataSource.Apply(MeshNode node)", second, pod),
    ];

    [Fact]
    public void Aggregate_CollapsesRepeatsOfOneFaultIntoASingleReport()
    {
        var entries = OneFault(0, "7a2f1c4e-9b3d-4a21-8f65-0c1d2e3f4a5b")
            .Concat(OneFault(5, "91bcd7e0-1234-4f88-9a01-bbccddeeff00"))
            .Concat(OneFault(9, "c0ffee00-1111-2222-3333-444455556666"))
            .ToList();

        var reports = Reports(entries, maxSamples: 5, maxSampleLength: 2000);

        reports.Should().HaveCount(1);
        var report = reports[0];
        report.Occurrences.Should().Be(3);
        report.FirstSeen.Should().Be(T0);
        report.LastSeen.Should().Be(T0.AddSeconds(9));
        report.Category.Should().Be("MeshWeaver.Data.MeshDataSource");
        report.ExceptionType.Should().Be("System.InvalidOperationException");
        report.Severity.Should().Be(LogSeverity.Error);
        report.Namespace.Should().Be("memex");
    }

    [Fact]
    public void Aggregate_KeepsDifferentFaultsApart()
    {
        var entries = OneFault(0, "7a2f1c4e-9b3d-4a21-8f65-0c1d2e3f4a5b").ToList();
        entries.AddRange(
        [
            Entry("crit: Memex.Portal.Startup[0]", 3),
            Entry("      Could not connect to the database", 3),
        ]);

        var reports = Reports(entries, maxSamples: 5, maxSampleLength: 2000);

        reports.Should().HaveCount(2);
        reports.Select(r => r.Category).Should().Contain("Memex.Portal.Startup");
        reports.Single(r => r.Category == "Memex.Portal.Startup").Severity
            .Should().Be(LogSeverity.Critical);
    }

    [Fact]
    public void Aggregate_RecordsEveryPodOnOneIncident()
    {
        var entries = OneFault(0, "7a2f1c4e-9b3d-4a21-8f65-0c1d2e3f4a5b", "memex-portal-aaa")
            .Concat(OneFault(4, "91bcd7e0-1234-4f88-9a01-bbccddeeff00", "memex-portal-bbb"))
            .ToList();

        var reports = Reports(entries, maxSamples: 5, maxSampleLength: 2000);

        // Same defect on two pods is ONE ticket — the pods ride along as evidence.
        reports.Should().HaveCount(1);
        reports[0].Pods.Should().BeEquivalentTo(
            new[] { "memex-portal-aaa", "memex-portal-bbb" },
            System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact]
    public void Aggregate_AttachesTheStackTraceToItsHeader()
    {
        var reports = Reports(
            OneFault(0, "7a2f1c4e-9b3d-4a21-8f65-0c1d2e3f4a5b"),
            maxSamples: 5, maxSampleLength: 2000);

        // If the continuation lines were not re-attached, TopFrame would be null and every fault
        // in this category would share one fingerprint.
        reports[0].TopFrame.Should().Be("MeshWeaver.Data.MeshDataSource.Apply(MeshNode node)");
        reports[0].Samples.Should().ContainSingle();
        reports[0].Samples[0].Line.Should().Contain("System.InvalidOperationException");
    }

    [Fact]
    public void Aggregate_EndsABurstAtTheNextLevelHeader()
    {
        var entries = new List<LogLine>
        {
            Entry("fail: MeshWeaver.Data.MeshDataSource[0]", 0),
            Entry("      Update failed", 0),
            Entry("info: MeshWeaver.Data.MeshDataSource[0]", 1),
            Entry("      All good now", 1),
        };

        var reports = Reports(entries, maxSamples: 5, maxSampleLength: 2000);

        reports.Should().ContainSingle();
        // The info: lines must not be swallowed into the red burst's evidence.
        reports[0].Samples[0].Line.Should().NotContain("All good now");
    }

    [Fact]
    public void Aggregate_IgnoresConfiguredCategories()
    {
        var reports = Reports(
            OneFault(0, "7a2f1c4e-9b3d-4a21-8f65-0c1d2e3f4a5b"),
            maxSamples: 5, maxSampleLength: 2000,
            ignoreCategories: ["MeshWeaver.Data."]);

        reports.Should().BeEmpty();
    }

    [Fact]
    public void Aggregate_BoundsSamplesAndTheirLength()
    {
        var entries = Enumerable.Range(0, 20)
            .SelectMany(i => OneFault(i, $"{i:x8}-1111-2222-3333-444455556666"))
            .ToList();

        var reports = Reports(entries, maxSamples: 3, maxSampleLength: 40);

        reports[0].Occurrences.Should().Be(20);
        reports[0].Samples.Should().HaveCount(3);
        // Kept samples are the LATEST ones — those correlate with the rest of the logs.
        reports[0].Samples[^1].Timestamp.Should().Be(T0.AddSeconds(19));
        reports[0].Samples.Should().OnlyContain(s => s.Line.Length <= 40 + "…[truncated]".Length);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The per-site variant budget — the floor under "too fine" (#1787)
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One site reporting <paramref name="distinct"/> different unmaskable subjects. Modelled on the
    /// 2026-08-09 fan-out that produced ~50 incidents for ONE reconcile defect: a bare diagnostic, no
    /// exception and no frame, whose varying subject is a plain word no masking rule can anticipate.
    /// </summary>
    private static List<LogLine> ManyShapesAtOneSite(int distinct) =>
        Enumerable.Range(0, distinct)
            .SelectMany(i => new[]
            {
                Entry("fail: PluginGating[0]", i),
                // The subject is a bare WORD — no digits, no path, nothing Normalize masks. That is
                // exactly the shape that defeated four earlier fingerprint rules.
                Entry($"      reconcile is NOT CONVERGING — Widget{Word(i)} was rewritten again", i),
            })
            .ToList();

    /// <summary>A distinct all-letters token per index — a digit would be masked as <c>{n}</c>.</summary>
    private static string Word(int i) => $"{(char)('A' + i / 26)}{(char)('A' + i % 26)}";

    /// <summary>
    /// 13 different errors at one site is the #1787 case and MUST stay 13 tickets — every one of them
    /// needs its own fix. It is under the watcher's default budget of 20 for exactly that reason.
    /// </summary>
    [Fact]
    public void ThirteenShapesAtOneSite_StayThirteenIncidents()
    {
        var aggregation = BurstAggregator.Aggregate(
            ManyShapesAtOneSite(13), maxSamples: 5, maxSampleLength: 2000,
            maxVariantsPerSite: 20);

        aggregation.Reports.Should().HaveCount(13);
        aggregation.FoldedSites.Should().Be(0);
        aggregation.Reports.Should().OnlyContain(r => r.Variants == 1);
    }

    /// <summary>
    /// …and 50 is the fan-out that already burned us once. Past the budget the watcher stops
    /// believing its own split: ONE incident, carrying the shape count, so a human is told the
    /// masking rule needs a case instead of being handed fifty tickets nobody reads.
    /// </summary>
    [Fact]
    public void FiftyShapesAtOneSite_FoldOntoOneIncident()
    {
        var aggregation = BurstAggregator.Aggregate(
            ManyShapesAtOneSite(50), maxSamples: 5, maxSampleLength: 2000,
            maxVariantsPerSite: 20);

        aggregation.Reports.Should().ContainSingle();
        aggregation.FoldedSites.Should().Be(1);

        var folded = aggregation.Reports[0];
        folded.Variants.Should().Be(50, "the ticket has to say how much it is standing in for");
        folded.Occurrences.Should().Be(50);
        folded.Category.Should().Be("PluginGating");
    }

    /// <summary>
    /// A fold must not drag in an unrelated site. Only the site over budget collapses; the quiet one
    /// beside it keeps its own ticket — otherwise a noisy component would swallow its neighbours,
    /// which is the exact failure #1787 is about.
    /// </summary>
    [Fact]
    public void AFoldedSite_DoesNotSwallowAQuietOne()
    {
        var entries = ManyShapesAtOneSite(50);
        entries.AddRange(OneFault(60, "7a2f1c4e-9b3d-4a21-8f65-0c1d2e3f4a5b"));

        var aggregation = BurstAggregator.Aggregate(
            entries, maxSamples: 5, maxSampleLength: 2000, maxVariantsPerSite: 20);

        aggregation.Reports.Should().HaveCount(2);
        aggregation.Reports.Should().ContainSingle(r => r.Category == "MeshWeaver.Data.MeshDataSource")
            .Which.Variants.Should().Be(1);
    }

    /// <summary>The budget is opt-in: zero disables the fold entirely.</summary>
    [Fact]
    public void WithoutABudget_NothingFolds()
    {
        var aggregation = BurstAggregator.Aggregate(
            ManyShapesAtOneSite(50), maxSamples: 5, maxSampleLength: 2000, maxVariantsPerSite: 0);

        aggregation.Reports.Should().HaveCount(50);
        aggregation.FoldedSites.Should().Be(0);
    }
}
