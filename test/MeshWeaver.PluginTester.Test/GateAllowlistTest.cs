#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// The known-debt ratchet, pinned check by check: parsing (including the malformed-line throw —
/// a typo that silently allowed nothing would defeat the ratchet), the classification of every
/// failure into new-vs-known, the STALE rule (a passing check fails the run until its entry is
/// removed — the list only shrinks), and the unverifiable escape (a skipped check or absent
/// scope proves nothing, so it warns and never fails).
/// </summary>
public class GateAllowlistTest
{
    // ── parsing ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsEntries_SkipsCommentsAndBlanks()
    {
        var allow = GateAllowlist.Parse(
        [
            "# the debt list",
            "",
            "Claims idempotence   # tracked in #235",
            "Edu/Exercise tests",
        ]);
        Assert.Equal(2, allow.Entries.Count);
        Assert.True(allow.Allows("Claims", "idempotence"));
        Assert.True(allow.Allows("Edu/Exercise", "tests"));
        Assert.False(allow.Allows("Claims", "install"));
    }

    [Fact]
    public void Parse_MatchesCaseInsensitively()
    {
        var allow = GateAllowlist.Parse(["claims IDEMPOTENCE"]);
        Assert.True(allow.Allows("Claims", "idempotence"));
    }

    [Theory]
    [InlineData("Claims")]                      // missing check
    [InlineData("Claims idempotence extra")]    // too many tokens
    [InlineData("Claims flakiness")]            // unknown check name
    public void Parse_ThrowsOnMalformedLine(string line)
    {
        var ex = Assert.Throws<FormatException>(() => GateAllowlist.Parse([line]));
        Assert.Contains("line 1", ex.Message);
    }

    // ── classification ───────────────────────────────────────────────────────────────────────

    private static GateReport Report(params PackageResult[] packages) => new([.. packages]);

    private static PackageResult IdempotenceBroken(string id) =>
        new(id) { IdempotenceError = "re-install wrote 1 node(s)" };

    private static PackageResult WithType(string id, NodeTypeResult type) =>
        new(id) { NodeTypes = [type] };

    private static NodeTypeResult TestsRed(string path) =>
        new(path, path.Split('/')[0]) { Tests = CheckOutcome.Failed, TestsDetail = "1 red row" };

    [Fact]
    public void Evaluate_SplitsFailures_IntoKnownDebtAndNew()
    {
        var report = Report(IdempotenceBroken("Claims"), IdempotenceBroken("Video"));
        var verdict = GateVerdict.Evaluate(report, GateAllowlist.Parse(["Claims idempotence"]));

        Assert.Single(verdict.KnownDebt);
        Assert.True(verdict.IsKnownDebt("Claims", "idempotence"));
        Assert.Single(verdict.NewFailures);
        Assert.Equal("Video", verdict.NewFailures[0].Scope);
        Assert.False(verdict.Success);
    }

    [Fact]
    public void Evaluate_AllKnown_IsGreen()
    {
        var report = Report(
            IdempotenceBroken("Claims"),
            WithType("Edu", TestsRed("Edu/Exercise")));
        var verdict = GateVerdict.Evaluate(
            report, GateAllowlist.Parse(["Claims idempotence", "Edu/Exercise tests"]));

        Assert.Empty(verdict.NewFailures);
        Assert.Empty(verdict.Stale);
        Assert.Equal(2, verdict.KnownDebt.Count);
        Assert.True(verdict.Success);
    }

    [Fact]
    public void Evaluate_TypeChecks_MatchOnTheTypePath()
    {
        var report = Report(WithType("Edu", TestsRed("Edu/Exercise")));
        var verdict = GateVerdict.Evaluate(report, GateAllowlist.Parse(["Edu tests"]));

        // The package-scoped entry does NOT cover a type-level failure: exact scope only,
        // or the ratchet stops ratcheting.
        Assert.Single(verdict.NewFailures);
        Assert.False(verdict.Success);
    }

    // ── the stale rule ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_EntryWhoseCheckPasses_IsStale_AndFailsTheRun()
    {
        var healthy = new PackageResult("Claims");
        var verdict = GateVerdict.Evaluate(
            Report(healthy), GateAllowlist.Parse(["Claims idempotence"]));

        Assert.Single(verdict.Stale);
        Assert.Empty(verdict.NewFailures);
        Assert.False(verdict.Success);
    }

    [Fact]
    public void Evaluate_TypeEntryWhoseTestsPass_IsStale()
    {
        var green = new NodeTypeResult("Edu/Exercise", "Edu") { Tests = CheckOutcome.Passed };
        var verdict = GateVerdict.Evaluate(
            Report(WithType("Edu", green)), GateAllowlist.Parse(["Edu/Exercise tests"]));

        Assert.Single(verdict.Stale);
        Assert.False(verdict.Success);
    }

    // ── the unverifiable escape ──────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_AbsentScope_IsUnverifiable_NotStale()
    {
        var verdict = GateVerdict.Evaluate(
            Report(new PackageResult("Claims")), GateAllowlist.Parse(["Gone/Type tests"]));

        Assert.Single(verdict.Unverifiable);
        Assert.Empty(verdict.Stale);
        Assert.True(verdict.Success);
    }

    [Fact]
    public void Evaluate_SkippedTests_AreUnverifiable_NotStale()
    {
        var skipped = new NodeTypeResult("Edu/Exercise", "Edu") { Tests = CheckOutcome.Skipped };
        var verdict = GateVerdict.Evaluate(
            Report(WithType("Edu", skipped)), GateAllowlist.Parse(["Edu/Exercise tests"]));

        Assert.Single(verdict.Unverifiable);
        Assert.Empty(verdict.Stale);
        Assert.True(verdict.Success);
    }

    [Fact]
    public void Evaluate_IdempotenceEntry_BehindAFailedInstall_IsUnverifiable()
    {
        // The idempotence pin never ran if the install itself failed — its entry must not be
        // stale (that would demand removing an entry for debt that may well still exist).
        var broken = new PackageResult("Claims") { InstallError = "boom" };
        var verdict = GateVerdict.Evaluate(
            Report(broken), GateAllowlist.Parse(["Claims idempotence"]));

        Assert.Single(verdict.Unverifiable);
        Assert.Empty(verdict.Stale);
        // The install failure itself is unlisted → a new failure → the run still fails.
        Assert.Single(verdict.NewFailures);
        Assert.False(verdict.Success);
    }

    // ── the summary states ONE verdict ───────────────────────────────────────────────────────

    [Fact]
    public void Summary_LabelsKnownDebt_AndStatesTheVerdictLine()
    {
        var report = Report(IdempotenceBroken("Claims"));
        var verdict = GateVerdict.Evaluate(report, GateAllowlist.Parse(["Claims idempotence"]));
        var output = new StringWriter();
        report.WriteSummary(output, verdict);
        var text = output.ToString();

        Assert.Contains("[DEBT] Claims", text);
        Assert.Contains("[known-debt]", text);
        Assert.Contains("GREEN — 1 known-debt failure(s) allowed", text);
        Assert.DoesNotContain("GATE FAILED", text);
    }

    [Fact]
    public void Summary_NamesStaleEntries_AndFails()
    {
        var report = Report(new PackageResult("Claims"));
        var verdict = GateVerdict.Evaluate(report, GateAllowlist.Parse(["Claims idempotence"]));
        var output = new StringWriter();
        report.WriteSummary(output, verdict);
        var text = output.ToString();

        Assert.Contains("STALE allow entry", text);
        Assert.Contains("Claims idempotence", text);
        Assert.Contains("GATE FAILED", text);
    }
}
