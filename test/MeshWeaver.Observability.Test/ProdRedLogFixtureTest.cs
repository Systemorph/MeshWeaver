using System.Collections.Immutable;
using MeshWeaver.Observability;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// 🚨 THE #1787 REGRESSION TEST, run on VERBATIM production lines.
///
/// <para><c>Fixtures/memex-cloud-2026-08-17-red-bursts.txt</c> is copied out of the <c>memex-cloud</c> portal
/// pod on the day the defect was measured. That day the watcher reported
/// <c>"memex-cloud: 1 distinct fingerprint(s) from 5000 red line(s)"</c> window after window, and
/// **13 NodeTypes parked at <c>CompileError</c> were never ticketed** (#1786 had to be filed by
/// hand). Every earlier revision of the identity function passed hand-written input and failed
/// production, so these assertions run on the real thing.</para>
///
/// <para>The two cases the fix has to answer, both present in this fixture:</para>
/// <list type="bullet">
/// <item><b>Many lines of the SAME error ⇒ ONE incident.</b> The three health-check bursts differ
/// only in an elapsed time and fold into one report with three occurrences.</item>
/// <item><b>Few lines of DIFFERENT errors ⇒ one incident each.</b> The three compile failures share
/// their category, their event id, their exception type AND their top application frame — the whole
/// of the old identity — and are three separate defects.</item>
/// </list>
/// </summary>
public class ProdRedLogFixtureTest
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 17, 19, 27, 30, TimeSpan.Zero);
    private const string Pod = "memex-portal-deployment-559ddb8f8-dbpx6";

    /// <summary>The fixture as the collector would have handed it over: one entry per line.</summary>
    private static ImmutableList<LogLine> Fixture()
    {
        // 🚨 `.txt`, not `.log` — the repo's .gitignore excludes `*.log`, so a fixture with that
        // extension is silently untracked: green locally, MISSING on CI's clean checkout.
        var path = Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "memex-cloud-2026-08-17-red-bursts.txt");
        return File.ReadAllLines(path)
            .Select((line, i) => new LogLine(T0.AddMilliseconds(i * 10), "memex-cloud", Pod, line))
            .ToImmutableList();
    }

    private static ImmutableList<LogIncidentReport> Reports() =>
        BurstAggregator.Aggregate(Fixture(), maxSamples: 5, maxSampleLength: 4000).Reports;

    /// <summary>
    /// The headline claim: NodeTypes that failed to compile are separate incidents, even though the
    /// identity's old inputs are identical across all of them. The fixture carries three of the
    /// thirteen bursts memex-cloud produced — enough to pin the split, small enough to read.
    /// </summary>
    [Fact]
    public void ParkedNodeTypes_WithDifferentCompilerErrors_AreSeparateIncidents()
    {
        var compile = Reports()
            .Where(r => r.Category == "MeshWeaver.Graph.Configuration.MeshNodeCompilationService")
            .ToImmutableList();

        // Everything the identity used to consist of is the same for all of them…
        compile.Select(r => r.ExceptionType).Distinct().Should().ContainSingle()
            .Which.Should().Be("MeshWeaver.Compiler.CompilationException");
        compile.Select(r => r.TopFrame).Distinct().Should().ContainSingle()
            .Which.Should().Be(
                "MeshWeaver.Compiler.EmitPipeline.EmitCompilationToDirectory(CSharpCompilation compilation, "
                + "String nodeName, String nodePath, String releaseDir, CancellationToken ct)");

        // …so under the old rule this was ONE fingerprint. The compiler diagnostics are what differ,
        // and they now reach the identity.
        compile.Should().HaveCount(3,
            "Cession, Northwind/Product and SocialMedia/Post fail with different compiler errors and "
            + "need different fixes — one ticket for all of them is why #1786 was filed by hand");
        compile.Select(r => r.Fingerprint).Distinct().Should().HaveCount(3);
        compile.Should().OnlyContain(r => r.Occurrences == 1);
    }

    /// <summary>
    /// The other direction, on the same fixture: a repeated diagnostic is ONE incident whose
    /// occurrence count says how loud it was. This is the property a finer fingerprint must not lose,
    /// and 3,894 lines of one error was the other measured case in #1787.
    /// </summary>
    [Fact]
    public void RepeatsOfOneDiagnostic_AreOneTicket()
    {
        var health = Reports()
            .Where(r => r.Category.EndsWith("DefaultHealthCheckService", StringComparison.Ordinal))
            .ToImmutableList();

        health.Should().ContainSingle(
            "the three bursts differ only in an elapsed time, which is masked before hashing");
        health[0].Occurrences.Should().Be(3);
        health[0].NormalizedDetail.Should().NotContain("0.7493");
    }

    /// <summary>
    /// The independently-actionable failures #1787 lists must each be their own incident: a compile
    /// error, a Polly timeout and a failing health check are three different problems in one window.
    /// </summary>
    [Fact]
    public void DifferentSubsystems_AreDifferentIncidents()
    {
        var reports = Reports();

        reports.Select(r => r.Category).Distinct().Should().BeEquivalentTo(
            new[]
            {
                "MeshWeaver.Graph.Configuration.MeshNodeCompilationService",
                "Polly",
                "Microsoft.Extensions.Diagnostics.HealthChecks.DefaultHealthCheckService",
            },
            System.Text.Json.JsonSerializerOptions.Default);

        // 3 compile + 1 Polly + 1 health check. The watcher reported ONE for this shape of window.
        reports.Should().HaveCount(5);
        reports.Select(r => r.Fingerprint).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The verdict line has to be readable. "1 distinct fingerprint(s) from 5000 red line(s)" bound
    /// <c>Lines</c> to the TOTAL line count — the query is unfiltered — so it read as 5000 collapsing
    /// errors when it was 5000 lines of every severity. Red bursts and total lines are separate now.
    /// </summary>
    [Fact]
    public void TheAggregation_ReportsRedBurstsSeparatelyFromTotalLines()
    {
        var entries = Fixture();
        var aggregation = BurstAggregator.Aggregate(entries, maxSamples: 5, maxSampleLength: 4000);

        aggregation.TotalLines.Should().Be(entries.Count);
        aggregation.RedBursts.Should().Be(7, "3 compile + 1 Polly + 3 health-check bursts are red");
        aggregation.RedBursts.Should().BeLessThan(aggregation.TotalLines,
            "the unfiltered query returns info: and warn: lines too — conflating the two is what made "
            + "the production log unreadable");
        aggregation.FoldedSites.Should().Be(0);
    }

    /// <summary>
    /// The per-node identity has to survive into the ticket, or splitting the incidents buys nothing:
    /// a responder still has to know WHICH node is parked. It rides in the samples, verbatim.
    /// </summary>
    [Fact]
    public void EachIncident_CarriesItsOwnNodePathInTheEvidence()
    {
        var compile = Reports()
            .Where(r => r.Category == "MeshWeaver.Graph.Configuration.MeshNodeCompilationService")
            .ToImmutableList();

        var nodes = compile
            .Select(r => string.Join('\n', r.Samples.Select(s => s.Line)))
            .ToImmutableList();

        nodes.Should().ContainSingle(s => s.Contains("BusinessRules/Cession", StringComparison.Ordinal));
        nodes.Should().ContainSingle(s => s.Contains("Northwind/Product", StringComparison.Ordinal));
        nodes.Should().ContainSingle(s => s.Contains("SocialMedia/Post", StringComparison.Ordinal));

        // …while the fingerprint itself is free of it, so the same failure on another node folds in
        // rather than opening a second ticket.
        compile.Should().OnlyContain(r => !r.NormalizedDetail.Contains("Northwind"));
    }
}
