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

        var reports = BurstAggregator.Aggregate(entries, maxSamples: 5, maxSampleLength: 2000);

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

        var reports = BurstAggregator.Aggregate(entries, maxSamples: 5, maxSampleLength: 2000);

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

        var reports = BurstAggregator.Aggregate(entries, maxSamples: 5, maxSampleLength: 2000);

        // Same defect on two pods is ONE ticket — the pods ride along as evidence.
        reports.Should().HaveCount(1);
        reports[0].Pods.Should().BeEquivalentTo(
            new[] { "memex-portal-aaa", "memex-portal-bbb" },
            System.Text.Json.JsonSerializerOptions.Default);
    }

    [Fact]
    public void Aggregate_AttachesTheStackTraceToItsHeader()
    {
        var reports = BurstAggregator.Aggregate(
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

        var reports = BurstAggregator.Aggregate(entries, maxSamples: 5, maxSampleLength: 2000);

        reports.Should().ContainSingle();
        // The info: lines must not be swallowed into the red burst's evidence.
        reports[0].Samples[0].Line.Should().NotContain("All good now");
    }

    [Fact]
    public void Aggregate_IgnoresConfiguredCategories()
    {
        var reports = BurstAggregator.Aggregate(
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

        var reports = BurstAggregator.Aggregate(entries, maxSamples: 3, maxSampleLength: 40);

        reports[0].Occurrences.Should().Be(20);
        reports[0].Samples.Should().HaveCount(3);
        // Kept samples are the LATEST ones — those correlate with the rest of the logs.
        reports[0].Samples[^1].Timestamp.Should().Be(T0.AddSeconds(19));
        reports[0].Samples.Should().OnlyContain(s => s.Line.Length <= 40 + "…[truncated]".Length);
    }
}
