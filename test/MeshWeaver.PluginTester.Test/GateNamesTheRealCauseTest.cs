#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using MeshWeaver.PluginCatalog;
using MeshWeaver.PluginTester;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// 🚨 THE GATE MUST NAME THE REAL CAUSE — the regression suite for Systemorph/MeshWeaver#2454 and
/// #2463, which are ONE defect with two triggers.
///
/// <para>Both begin at the same observation, in the same <c>Catch((TimeoutException _) =&gt; …)</c>:
/// the mesh wrote no terminal compile status inside the per-type budget. That observation was
/// scored <c>compile = Failed</c>, which is the single most expensive kind of wrong a report here
/// can be — it names a component that is not broken, so every reader spends their time in the one
/// place the cause provably is not:</para>
///
/// <list type="bullet">
///   <item><b>#2454</b> — <c>RolePlay/Story</c>: <c>no terminal compile status within 300s</c>
///     after the install raced its own hub disposal. The required check annotated it <i>"a public
///     API change here broke plugin node source"</i> on a PR whose entire diff was one markdown
///     line, a test-data ledger and one test-list row. Nothing in it was C#.</item>
///   <item><b>#2463</b> — <c>RolePlay/Scenery</c>: the SAME run's log had already recorded
///     <c>ok RolePlay/Scenery</c> and baked four assemblies with no mesh involved, then
///     <c>MergeGuard</c> refused the adoption stamp as a stale/reordered cross-hub write and
///     <c>CompileWatcher</c> logged <c>the write did not converge</c>. The gate read the absent
///     status as <c>compile=FAILED</c>, reddened main and held a production rollout ~11 hours.</item>
/// </list>
///
/// <para>Every assertion below is TWO-SIDED, in the tradition of <see cref="GateSummaryTest"/>: the
/// line must name the cause that applies AND must not name one that does not. A one-sided "it
/// failed" assertion passes just as happily against the defect.</para>
/// </summary>
public class GateNamesTheRealCauseTest
{
    private const string TypePath = "RolePlay/Scenery";

    private static NodeTypeResult Type(CheckOutcome compile, string? detail) =>
        new(TypePath, "RolePlay")
        {
            Compile = compile,
            CompileDetail = detail,
            Render = CheckOutcome.Skipped,
            Tests = CheckOutcome.Skipped,
        };

    private static GateReport Report(NodeTypeResult type) =>
        new([new PackageResult("RolePlay") { NodeCount = 42, NodeTypes = [type] }]);

    private static string Summarize(GateReport report, GateVerdict? verdict = null)
    {
        var output = new StringWriter();
        report.WriteSummary(output, verdict);
        return output.ToString();
    }

    private static string VerdictLine(string summary) =>
        summary.ReplaceLineEndings("\n").Split('\n')
            .Single(l => l.StartsWith(GateReport.FailedPrefix, StringComparison.Ordinal));

    /// <summary>A bake seed that declares assemblies for <paramref name="declared"/> — the
    /// evidence half of the classification, built with the seeder's own comparer.</summary>
    private static BakeSeed Seed(params string[] declared) =>
        new("/bake", "sdeadbeef", ImmutableArray.Create("/bake/RolePlay.zip"),
            declared.ToImmutableSortedSet(StringComparer.OrdinalIgnoreCase));

    // ── 1. the classification: which of the three outcomes the observation earns ──────────────

    /// <summary>
    /// #2463 exactly: the run CONSUMED a bake that carries this type's assembly, so the compile is
    /// proven by bytes the compiler stage emitted with no mesh involved. The only thing missing is
    /// the mesh's own status write, and the verdict must say so.
    /// </summary>
    [Fact]
    public void BakeCarriesTheAssembly_TheStatusWriteIsWhatWasLost_NotTheCompile()
    {
        var result = PluginGateRunner.NoTerminalCompileStatus(
            Type(CheckOutcome.Skipped, null), TypePath, Seed(TypePath), TimeSpan.FromSeconds(300));

        result.Compile.Should().Be(CheckOutcome.Unrecorded,
            "the bake proves the compile succeeded — what did not happen is the status write");
        result.Compile.Should().NotBe(CheckOutcome.Failed,
            "reporting a compile failure for a compile that produced bytes is #2463");
        result.CompileDetail.Should().Contain("COMPILE SUCCEEDED");
        result.CompileDetail.Should().Contain("STATUS WRITE");
        result.CompileDetail.Should().Contain("INFRASTRUCTURE, not the plugin source");
    }

    /// <summary>
    /// The comparer must be the SEEDER's (OrdinalIgnoreCase). A stricter one here would answer "no
    /// evidence" for an assembly <c>ShippedPrebuiltBundles.SeedForTypes</c> had happily adopted
    /// under a case difference — and the gate would then blame the compile after all.
    /// </summary>
    [Fact]
    public void TheBakeEvidenceIsMatchedWithTheSeedersOwnComparer()
    {
        var result = PluginGateRunner.NoTerminalCompileStatus(
            Type(CheckOutcome.Skipped, null), "roleplay/scenery", Seed(TypePath),
            TimeSpan.FromSeconds(300));

        result.Compile.Should().Be(CheckOutcome.Unrecorded);
    }

    /// <summary>
    /// #2454's honest floor: with no bake evidence the gate does NOT know that the compile
    /// succeeded, so it must not say so — but it equally must not say the compile FAILED. "I did
    /// not get an answer" is its own outcome, and the detail sends the reader to the mesh.
    /// </summary>
    [Fact]
    public void WithNoBakeEvidence_ItIsNoVerdict_NeverACompileFailure()
    {
        var result = PluginGateRunner.NoTerminalCompileStatus(
            Type(CheckOutcome.Skipped, null), TypePath, seed: null, TimeSpan.FromSeconds(300));

        result.Compile.Should().Be(CheckOutcome.Inconclusive);
        result.Compile.Should().NotBe(CheckOutcome.Failed);
        result.CompileDetail.Should().Contain("no terminal compile status within 300s");
        result.CompileDetail.Should().Contain("TIMEOUT, not a compiler diagnostic");
        result.CompileDetail.Should().NotContain("COMPILE SUCCEEDED",
            "with no bake there is no evidence the compile succeeded either");
    }

    /// <summary>A seed that covers OTHER types is not evidence about this one.</summary>
    [Fact]
    public void ABakeThatDoesNotCoverThisType_IsNotEvidenceAboutIt()
    {
        var result = PluginGateRunner.NoTerminalCompileStatus(
            Type(CheckOutcome.Skipped, null), TypePath, Seed("RolePlay/Story"),
            TimeSpan.FromSeconds(300));

        result.Compile.Should().Be(CheckOutcome.Inconclusive);
        result.CompileDetail.Should().Contain("declares no assembly for this type");
    }

    // ── 2. the verdict LINE — the one line CI lifts verbatim ──────────────────────────────────

    /// <summary>
    /// The #2463 headline. <c>compile: RolePlay/Scenery</c> is the sentence that cost 11 hours; the
    /// line must instead name the status write and say the work succeeded.
    /// </summary>
    [Fact]
    public void TheHeadline_ForALostStatusWrite_DoesNotSayTheCompileFailed()
    {
        var line = VerdictLine(Summarize(Report(Type(
            CheckOutcome.Unrecorded, "the COMPILE SUCCEEDED … MergeGuard refused the write"))));

        line.Should().Contain("compile-status-unrecorded: RolePlay/Scenery");
        line.Should().Contain("the work SUCCEEDED and the mesh did not record it");
        line.Should().NotContain("compile: RolePlay/Scenery",
            "that is the sentence that sends the reader to diff public API that is fine (#2463)");
    }

    /// <summary>
    /// The #2454 headline. A timeout is "I did not get an answer", and the line must say which of
    /// the two it is so nobody reads it as a diagnostic.
    /// </summary>
    [Fact]
    public void TheHeadline_ForATimeout_SaysNoVerdict_AndPointsAtTheMesh()
    {
        var line = VerdictLine(Summarize(Report(Type(
            CheckOutcome.Inconclusive, "no terminal compile status within 300s"))));

        line.Should().Contain("compile-no-verdict: RolePlay/Scenery");
        line.Should().Contain("NOT a compiler diagnostic");
        line.Should().Contain("investigate the mesh, not the source");
        line.Should().NotContain("compile: RolePlay/Scenery");
    }

    /// <summary>
    /// A REAL compile error must be unchanged — the fix must not blur the one case where "fix the
    /// source" IS the right instruction, and it must lead when both kinds are present.
    /// </summary>
    [Fact]
    public void ARealCompileError_StillReadsExactlyAsBefore_AndLeads()
    {
        var report = new GateReport(
        [
            new PackageResult("RolePlay")
            {
                NodeCount = 42,
                NodeTypes =
                [
                    Type(CheckOutcome.Inconclusive, "no terminal compile status within 300s"),
                    new NodeTypeResult("RolePlay/Story", "RolePlay")
                    {
                        Compile = CheckOutcome.Failed,
                        CompileDetail = "CS0246: type or namespace not found",
                        Render = CheckOutcome.Skipped,
                        Tests = CheckOutcome.Skipped,
                    },
                ],
            },
        ]);

        var line = VerdictLine(Summarize(report));

        line.Should().Contain("compile: RolePlay/Story");
        line.Should().Contain("compile-no-verdict: RolePlay/Scenery");
        line.IndexOf("compile: RolePlay/Story", StringComparison.Ordinal)
            .Should().BeLessThan(line.IndexOf("compile-no-verdict", StringComparison.Ordinal),
                "the actionable verdict leads; the non-verdict follows");
    }

    // ── 3. the two halves of the gate must agree about what failed ────────────────────────────

    /// <summary>
    /// 🚨 The failure ENUMERATION and the SUCCESS predicate are two independent readers of the same
    /// outcome, and a `== Failed` test in either makes them disagree: the run exits non-zero while
    /// the headline says "no failing check was recorded" and the ratchet calls every listed entry
    /// stale. A gate whose halves contradict each other is worse than one that names a wrong cause.
    /// </summary>
    [Theory]
    [InlineData(CheckOutcome.Inconclusive)]
    [InlineData(CheckOutcome.Unrecorded)]
    public void ANonVerdictFailsTheRun_AndIsEnumeratedAsAFailure(CheckOutcome outcome)
    {
        var report = Report(Type(outcome, "detail"));

        report.Success.Should().BeFalse("a check that produced no verdict has not passed");
        report.ExitCode.Should().Be(1);
        GateVerdict.Headline(report).Should().NotBe("no failing check was recorded");

        var verdict = GateVerdict.Evaluate(report, GateAllowlist.Empty);
        verdict.NewFailures.Should().ContainSingle()
            .Which.Outcome.Should().Be(outcome);
        verdict.Success.Should().BeFalse();
    }

    /// <summary>
    /// The known-debt ratchet keys on the CHECK NAME, not the kind — an entry that tolerates
    /// <c>RolePlay/Scenery compile</c> keeps tolerating it however the check failed to pass, and
    /// the entry is neither stale (the check did not pass) nor a new failure.
    /// </summary>
    [Fact]
    public void TheRatchetStillKeysOnTheCheckName()
    {
        var report = Report(Type(CheckOutcome.Unrecorded, "detail"));
        var verdict = GateVerdict.Evaluate(
            report, GateAllowlist.Parse(["RolePlay/Scenery compile"]));

        verdict.NewFailures.Should().BeEmpty();
        verdict.KnownDebt.Should().ContainSingle();
        verdict.Stale.Should().BeEmpty("the check did not pass, so the entry is not stale");
    }

    // ── 4. the wire contract — the collapse must not be reintroduced at the process boundary ──

    /// <summary>
    /// The combo verifier runs OUTSIDE the candidate image and reads this report as JSON. Both new
    /// outcomes must survive with their identity: mapping them to <c>Skipped</c> (the old
    /// catch-all) would tell the verifier "the check does not apply" about a check that failed.
    /// </summary>
    [Theory]
    [InlineData(CheckOutcome.Inconclusive, GateRunOutcome.Inconclusive)]
    [InlineData(CheckOutcome.Unrecorded, GateRunOutcome.Unrecorded)]
    public void TheNonVerdictsSurviveTheWireRoundTrip(CheckOutcome local, GateRunOutcome wire)
    {
        var json = JsonSerializer.Serialize(
            Report(Type(local, "detail")).ToRunReport(), InstanceComboAssembler.Json);
        var read = JsonSerializer.Deserialize<GateRunReport>(json, InstanceComboAssembler.Json)!;

        var type = read.Packages.Single().NodeTypes.Single();
        type.Compile.Should().Be(wire);
        type.Success.Should().BeFalse("a non-verdict is not a pass on the wire either");
        read.Packages.Single().Success.Should().BeFalse();
    }
}
