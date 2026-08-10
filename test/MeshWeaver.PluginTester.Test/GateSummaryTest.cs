#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The gate's verdict LINE, pinned against drift — because CI lifts it verbatim into the failure
/// annotation on the PR (<c>.github/workflows/dotnet-test.yml</c>, the plugin-gate step greps
/// <see cref="GateReport.FailedPrefix"/>).
///
/// <para>🚨 What these tests exist to prevent: a message that names a cause the report does not
/// support. The gate judges three checks per node — compile, render, tests — and CI used to
/// annotate ALL of them as <i>"MeshWeaver.Plugins does not compile against this PR … A public API
/// change here breaks plugin node source"</i>. On 2026-08-10 that fired for a verdict of
/// <c>Store/Catalog: compile=Ok render=ok tests=FAILED</c>: nothing failed to compile, and the
/// annotation sent investigators to diff public signatures on a PR whose whole diff was
/// <c>MessageService.cs</c> plus two test files (issue #1077). The wrong lead is expensive
/// precisely because a public-API break here IS a real way to break that repo — so the claim reads
/// as authoritative. Every assertion below is therefore two-sided: the line must name the check
/// that failed AND must not name one that did not.</para>
/// </summary>
public class GateSummaryTest
{
    private static NodeTypeResult Type(string path, CheckOutcome compile, CheckOutcome render,
        CheckOutcome tests) =>
        new(path, path.Split('/')[0])
        {
            Compile = compile,
            CompileDetail = compile == CheckOutcome.Failed ? "CS0103: name not found" : null,
            Render = render,
            RenderDetail = render == CheckOutcome.Failed ? "This area failed to render" : null,
            Tests = tests,
            TestsDetail = tests == CheckOutcome.Failed ? "**Area not found**" : null,
        };

    private static GateReport Report(params NodeTypeResult[] types) =>
        new([new PackageResult(types.FirstOrDefault()?.Package ?? "Store")
        {
            NodeCount = 97,
            NodeTypes = [.. types],
        }]);

    private static string Summarize(GateReport report, GateVerdict? verdict = null)
    {
        var output = new StringWriter();
        report.WriteSummary(output, verdict);
        return output.ToString();
    }

    private static string VerdictLine(string summary) =>
        summary.ReplaceLineEndings("\n").Split('\n')
            .Single(l => l.StartsWith(GateReport.FailedPrefix, StringComparison.Ordinal));

    // ── the exact shape that misled: tests failed, compile was fine ───────────────────────────

    [Fact]
    public void TestsFailure_NamesTests_AndNeverCompile()
    {
        var report = Report(Type("Store/Catalog",
            CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Failed));

        var line = VerdictLine(Summarize(report));

        Assert.Contains("tests: Store/Catalog", line, StringComparison.Ordinal);
        Assert.DoesNotContain("compile", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TestsFailure_WithAllowlist_NamesTests_AndNeverCompile()
    {
        // The armed path CI actually runs: --allow is always passed, so the verdict — not the raw
        // report — decides. This is the line that was annotated as a compile break.
        var report = Report(Type("Store/Catalog",
            CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Failed));
        var verdict = GateVerdict.Evaluate(report, GateAllowlist.Empty);

        var line = VerdictLine(Summarize(report, verdict));

        Assert.StartsWith(GateReport.FailedPrefix, line, StringComparison.Ordinal);
        Assert.Contains("tests: Store/Catalog", line, StringComparison.Ordinal);
        Assert.DoesNotContain("compile", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 new failure(s)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileFailure_NamesCompile()
    {
        var report = Report(Type("Store/Plugin",
            CheckOutcome.Failed, CheckOutcome.Skipped, CheckOutcome.Skipped));

        var line = VerdictLine(Summarize(report));

        Assert.Contains("compile: Store/Plugin", line, StringComparison.Ordinal);
        Assert.DoesNotContain("tests:", line, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryFailingCheck_IsNamed_InPipelineOrder()
    {
        var report = Report(
            Type("Store/Order", CheckOutcome.Failed, CheckOutcome.Skipped, CheckOutcome.Skipped),
            Type("Store/Coupon", CheckOutcome.Passed, CheckOutcome.Failed, CheckOutcome.Skipped),
            Type("Store/Catalog", CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Failed));

        var line = VerdictLine(Summarize(report));

        Assert.Contains("compile: Store/Order", line, StringComparison.Ordinal);
        Assert.Contains("render: Store/Coupon", line, StringComparison.Ordinal);
        Assert.Contains("tests: Store/Catalog", line, StringComparison.Ordinal);
        Assert.True(line.IndexOf("compile:", StringComparison.Ordinal)
                    < line.IndexOf("render:", StringComparison.Ordinal));
        Assert.True(line.IndexOf("render:", StringComparison.Ordinal)
                    < line.IndexOf("tests:", StringComparison.Ordinal));
    }

    [Fact]
    public void SameCheck_OnSeveralNodes_ListsThemAll()
    {
        var report = Report(
            Type("Store/Catalog", CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Failed),
            Type("Store/Plugin", CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Failed));

        var line = VerdictLine(Summarize(report));

        Assert.Contains("tests: Store/Catalog, Store/Plugin", line, StringComparison.Ordinal);
    }

    // ── the failures that are NOT a per-node check ────────────────────────────────────────────

    [Fact]
    public void FatalError_SaysFatal_NotAFailingCheck()
    {
        // A fatal error used to render as "GATE FAILED — 0 new failure(s), 0 stale allow
        // entr(ies)" — a failing gate reporting nothing failing.
        var report = new GateReport([]) { FatalError = "IOException: repo root not found\n at …" };
        var verdict = GateVerdict.Evaluate(report, GateAllowlist.Empty);

        var line = VerdictLine(Summarize(report, verdict));

        Assert.Contains("fatal: IOException: repo root not found", line, StringComparison.Ordinal);
        // Only the first line of the error — a stack trace would swallow the annotation.
        Assert.DoesNotContain(" at …", line, StringComparison.Ordinal);
    }

    [Fact]
    public void StaleAllowEntry_IsNamedAsSuch_NotAsAFailingCheck()
    {
        var green = Report(Type("Store/Catalog",
            CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Passed));
        var verdict = GateVerdict.Evaluate(green, GateAllowlist.Parse(["Store/Catalog tests"]));

        var line = VerdictLine(Summarize(green, verdict));

        Assert.Contains("stale allow entr(ies): Store/Catalog tests", line, StringComparison.Ordinal);
        Assert.Contains("0 new failure(s)", line, StringComparison.Ordinal);
    }

    [Fact]
    public void KnownDebt_IsNotNamed_ItDidNotFailTheRun()
    {
        // Debt is tolerated, so it is not what failed — naming it would send the investigator at
        // an entry that is deliberately on the list.
        var report = Report(
            Type("Store/Catalog", CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Failed),
            Type("Store/Plugin", CheckOutcome.Failed, CheckOutcome.Skipped, CheckOutcome.Skipped));
        var verdict = GateVerdict.Evaluate(report, GateAllowlist.Parse(["Store/Catalog tests"]));

        var line = VerdictLine(Summarize(report, verdict));

        Assert.Contains("compile: Store/Plugin", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Store/Catalog", line, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRecordedFailure_SaysSo_RatherThanGuessing()
    {
        // Defensive: if the run fails with nothing recorded, the line must admit that rather than
        // pick a phase. An unexplained failure is cheaper than a confidently wrong explanation.
        var report = new GateReport([]) { FatalError = null };
        var verdict = new GateVerdict([], [], [], []);

        Assert.Equal("no failing check was recorded", GateVerdict.Headline(report, verdict));
    }

    // ── the Tests host: which node actually ran the area ──────────────────────────────────────

    [Fact]
    public void TestsHost_IsPrinted_SoAreaNotFoundIsDiagnosable()
    {
        // "No renderer is registered for area `Tests` on hub `Store`" is unreadable without
        // knowing WHY the gate probed `Store`: a type's Tests area is served by instance hubs, so
        // the host is either an instance already in the mesh or a probe the gate created — and
        // only the first can carry a hub activated before the install finished.
        var report = Report(Type("Store/Catalog",
            CheckOutcome.Passed, CheckOutcome.Passed, CheckOutcome.Failed) with
        {
            TestsHost = "Store — an instance of Store/Catalog already in the mesh",
        });

        var summary = Summarize(report);

        Assert.Contains("Tests host: Store — an instance of Store/Catalog already in the mesh",
            summary, StringComparison.Ordinal);
    }
}
