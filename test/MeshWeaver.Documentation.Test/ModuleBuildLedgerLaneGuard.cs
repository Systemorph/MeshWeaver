#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for the module build LEDGER in the reusable module-pack lane (Plugins#889,
/// #931; <c>Doc/Architecture/ModuleBuildArchitecture</c> → "Content-addressed outputs").
///
/// <para>The ledger is a protocol between a CI lane and the registry portal, and its properties live
/// in YAML, not in the product: that the lane KEYS every selected module and CONSULTS the ledger before
/// the one-workspace build; that the build compiles the ledger's <c>build-modules</c> subset and feeds
/// the SAME list to its postcondition; that the pack job records the three transitions (Built after the
/// artifact upload, Tested after the suite, Published after the hand-over) plus the Failed and cancelled
/// verdicts — and, since the suite moved to a lane of its own on non-publishing runs, that the
/// <c>tests</c> job records the same Tested / test-phase-Failed verdicts there; and that every ledger
/// write is best-effort while the two scripts' self-tests run on every run. A lane that quietly dropped one of those would still be green — a duplicate build costs money
/// silently, a missing <c>Built</c> record makes every later run rebuild, and a <c>Tested</c> recorded
/// before the suite ran would hand followers a verdict nobody reached. Nothing in a green run
/// distinguishes those shapes; this does.</para>
/// </summary>
public class ModuleBuildLedgerLaneGuard
{
    private const string Lane = ".github/workflows/node-repo-module-pack.yml";
    private const string KeyScript = ".github/scripts/module-build-key.py";
    private const string LedgerScript = ".github/scripts/module-build-ledger.py";

    [Fact]
    public void TheLane_DeclaresTheLedgerFlagAndItsToken_DefaultingToOff()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Lane));
        // The folded (`>`) description carries blank lines, so the body is "8-space-indented or empty".
        var input = Regex.Match(text, @"\n      ledger:\n(?<body>(?:(?:        .*)?\n)+)");
        Assert.True(input.Success, $"{Lane} must declare a `ledger` input");
        Assert.Contains("default: off", input.Groups["body"].Value, StringComparison.Ordinal);
        Assert.True(Regex.IsMatch(text, @"\n      ledger-token:\n"), $"{Lane} must declare the `ledger-token` secret");
    }

    [Fact]
    public void Select_SelfTestsBothScripts_KeysTheSelection_AndConsultsTheLedger()
    {
        var select = JobBody("select");
        Assert.Contains("module-build-key.py --self-test", select, StringComparison.Ordinal);
        Assert.Contains("module-build-ledger.py --self-test", select, StringComparison.Ordinal);
        Assert.Contains("module-build-key.py --root repo", select, StringComparison.Ordinal);
        Assert.Contains("module-build-ledger.py decide", select, StringComparison.Ordinal);
        // The flag is a three-way case with a RED default arm — an unreadable value never means "off".
        Assert.Contains("inputs.ledger is '$LEDGER'. The only values are 'off' (the default) and 'required'", select, StringComparison.Ordinal);
        // The token is asserted, never tested-and-skipped.
        Assert.Contains("[ -n \"${MW_LEDGER_TOKEN:-}\" ] ||", select, StringComparison.Ordinal);
        // Publication reuse is downstream of the ledger: it must preserve the ledger selection
        // and its build subset when disabled, then expose the final selection to both consumers.
        Assert.Contains("MATRIX: ${{ steps.ledger.outputs.modules }}", select, StringComparison.Ordinal);
        Assert.Contains("BUILD: ${{ steps.ledger.outputs.build }}", select, StringComparison.Ordinal);
        Assert.Contains("echo \"modules=$MATRIX\" >> \"$GITHUB_OUTPUT\"", select, StringComparison.Ordinal);
        Assert.Contains("echo \"build=$BUILD\" >> \"$GITHUB_OUTPUT\"", select, StringComparison.Ordinal);
        Assert.Contains("node-repo-publication-reuse.py", select, StringComparison.Ordinal);
        Assert.Contains("build-modules: ${{ steps.reuse.outputs.build }}", select, StringComparison.Ordinal);
        Assert.Contains("modules: ${{ steps.reuse.outputs.modules }}", select, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildWorkspace_CompilesTheLedgersBuildSubset_AndFeedsThePostconditionTheSameList()
    {
        var build = JobBody("build-workspace");
        var uses = Regex.Matches(build, @"MODULES: \$\{\{ needs\.select\.outputs\.(?<which>[a-z-]+) \}\}")
            .Select(m => m.Groups["which"].Value).Distinct().ToArray();
        Assert.True(uses.Length == 1 && uses[0] == "build-modules",
            $"build-workspace must read ONLY `build-modules` (the entries the ledger did not hand a reusable bundle for) — "
            + $"the compile and its postcondition must share one enumerator. Found: {string.Join(", ", uses)}");
        Assert.Contains("module-build-ledger.py heartbeat", build, StringComparison.Ordinal);
        Assert.Contains("--status Failed --phase \"$phase\"", build, StringComparison.Ordinal);
    }

    [Fact]
    public void Pack_RecordsTheThreeTransitions_InOrder_AndTheTwoVerdicts()
    {
        var pack = JobBody("pack");
        int At(string needle)
        {
            var i = pack.IndexOf(needle, StringComparison.Ordinal);
            Assert.True(i >= 0, $"the pack job must contain: {needle}");
            return i;
        }

        // The first of the unrolled per-module upload slots (batched legs, 2026-09-12).
        var upload = At("name: module-bundle-${{ steps.bundles.outputs.s1 }}");
        var built = At("--status Built");
        var tests = At("dotnet test \"$tests\"");
        var tested = At("--status Tested --trx");
        var publish = At("-X POST \"$REGISTRY/api/plugins/bundles/$PACKAGE");
        var published = At("--status Published");
        var failed = At("--status Failed --phase \"$phase\"");
        var released = At("module-build-ledger.py release --key");
        var finished = At("module-build-ledger.py finish --key");

        // Built AFTER the artifact upload it names; Tested AFTER the suite; Published AFTER the hand-over.
        Assert.True(upload < built, "`Built` must be recorded AFTER the bundle artifact upload — the record names that artifact");
        Assert.True(tests < tested, "`Tested` must be recorded AFTER the suite ran");
        Assert.True(publish < published, "`Published` must be recorded AFTER the registry accepted the bundle");
        Assert.True(published < failed && failed < released && released < finished, "the Failed / released / finished steps come last");

        // Tested and Published are gated on the OUTCOME of the step they attest, not on the job's mood.
        Assert.Contains("steps.tests.outcome == 'success'", pack, StringComparison.Ordinal);
        Assert.Contains("steps.publish.outcome == 'success'", pack, StringComparison.Ordinal);
        // The Failed verdict is PER MODULE and the failures it records did not fail the step that
        // found them (a batched leg isolates each module), so it runs `always()` and reads the
        // batch state: the modules marked failed, plus — when the LEG itself stopped in a shared
        // step — the ones still in play. Released runs on cancellation only, for every key.
        Assert.Contains("if: always() && inputs.ledger == 'required'", pack, StringComparison.Ordinal);
        Assert.Contains("for MODULE in $(bk list --failed); do", pack, StringComparison.Ordinal);
        Assert.Contains("if [ \"$JOB_STATUS\" = failure ]; then", pack, StringComparison.Ordinal);
        Assert.Contains("if: cancelled() && inputs.ledger == 'required'", pack, StringComparison.Ordinal);
        // The reuse leg verifies the bytes against the record and never packs anyway.
        Assert.Contains("gh run download \"$ART_RUN\"", pack, StringComparison.Ordinal);
        Assert.Contains("is not the ledger's $EXPECTED_SHA", pack, StringComparison.Ordinal);
        // The suite writes the evidence the ledger records — under EITHER runner. The flags are
        // chosen from the CALLER's global.json (#4378), so asserting one literal would have gone
        // on passing while the other branch wrote nothing.
        AssertWritesTheLedgerTrxUnderBothRunners(pack, "pack");
        // The reuse window is the artifact's retention.
        Assert.Contains("retention-days: 7", pack, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 The suite does not always run in the pack job any more: on a call with
    /// <c>publish: false</c> it runs in the <c>tests</c> lane beside the pack matrix, so the
    /// verdict a follower reads must be recorded THERE with the same rules — <c>Tested</c> only
    /// after the suite it attests, the <c>test</c>-phase <c>Failed</c> verdict on the way out, and
    /// both best-effort. A lane that moved the suite and left its bookkeeping behind would make
    /// every later run of the same key re-run a suite that had already passed, silently.
    /// </summary>
    [Fact]
    public void TestsLane_RecordsTestedAfterItsSuite_AndTheTestPhaseVerdict()
    {
        var tests = JobBody("tests");
        var suite = tests.IndexOf("dotnet test \"$tests\"", StringComparison.Ordinal);
        var tested = tests.IndexOf("--status Tested --trx", StringComparison.Ordinal);
        Assert.True(suite >= 0, "the tests lane must run the module's suite");
        Assert.True(tested > suite, "`Tested` must be recorded AFTER the suite it attests");
        // Gated on the OUTCOME of the step it attests — never on the job's mood — and, inside,
        // on each module's own `tested` fact and ledger key (batched legs, 2026-09-12).
        Assert.Contains("if: inputs.ledger == 'required' && steps.tests.outcome == 'success'",
            tests, StringComparison.Ordinal);
        Assert.Contains("bk list --ok --where tested=true --where 'ledger.key!='", tests, StringComparison.Ordinal);
        // The suite writes the evidence the ledger records, exactly as the inline one does.
        AssertWritesTheLedgerTrxUnderBothRunners(tests, "tests");
        // The way out: this lane can only fail in one phase, so it names it rather than deriving it.
        Assert.Contains("--status Failed --phase test", tests, StringComparison.Ordinal);
        Assert.Contains("if: always() && inputs.ledger == 'required'", tests, StringComparison.Ordinal);
        Assert.Contains("for MODULE in $(bk list --failed); do", tests, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ledger's evidence is a <c>ledger.trx</c>, and which flags produce one depends on the
    /// runner: <c>dotnet test</c> selects it from the first <c>global.json</c> found walking up from
    /// the CURRENT DIRECTORY, and this lane runs with the caller's checkout as the cwd (the
    /// platform's own sits in a SIBLING checkout and never applies by itself — measured on the .NET
    /// 10.0.400 SDK, 2026-09-15). So the body must carry BOTH branches and hand the chosen one to
    /// the invocation.
    ///
    /// <para>🚨 <b>And the PLATFORM's <c>global.json</c> is read too, with the command run from its
    /// checkout when only it selects Microsoft.Testing.Platform (#4414).</b> The suite compiles
    /// against the platform's Directory.Packages.props, so the xunit.v3 version is the platform's;
    /// xunit.v3 4.x refuses VSTest on .NET 10. Reading the caller's file alone ran
    /// MeshWeaver.Plugins, which has none, under VSTest against a framework that refuses it, and
    /// main-cd failed every module suite with nothing executed. A body that reads only the caller
    /// passes every other assertion here — so this one names the platform read and the cd.</para>
    ///
    /// <para>🚨 Asserting only the VSTest literal is what this guard used to do, and it would have
    /// stayed green while the MTP branch wrote no trx at all — under Microsoft.Testing.Platform
    /// <c>--logger</c> ends the run as <c>Zero tests ran</c>, exit 5, which this lane records as
    /// "the module's own suite failed".</para>
    /// </summary>
    private static void AssertWritesTheLedgerTrxUnderBothRunners(string body, string job)
    {
        Assert.Contains("test_flags() {", body, StringComparison.Ordinal);
        Assert.Contains("--logger \"trx;LogFileName=ledger.trx\"", body, StringComparison.Ordinal);
        Assert.Contains("--report-xunit-trx --report-xunit-trx-filename ledger.trx", body, StringComparison.Ordinal);
        Assert.Contains("--results-directory \"$1\"", body, StringComparison.Ordinal);
        Assert.True(
            body.Contains("elif selects_mtp \"$GITHUB_WORKSPACE/meshweaver\"; then", StringComparison.Ordinal)
            && body.Contains("TEST_CWD=\"$GITHUB_WORKSPACE/meshweaver\"", StringComparison.Ordinal)
            && body.Contains("cd \"$TEST_CWD\"", StringComparison.Ordinal),
            $"the `{job}` job must follow the PLATFORM's test runner when the caller selects none, and "
            + "run `dotnet test` from the platform checkout so its global.json applies (#4414) — the "
            + "caller's global.json alone ran xunit.v3 4.x under VSTest, which it refuses");
        Assert.True(
            body.Contains("mapfile -t flags < <(test_flags \"$RUNNER_TEMP/trx/$MODULE\")", StringComparison.Ordinal)
            && body.Contains("\"${flags[@]}\"", StringComparison.Ordinal),
            $"the `{job}` job must hand the runner-selected flags to `dotnet test`; a body that "
            + "computes them and then invokes with a hard-coded set writes the trx of whichever "
            + "runner it guessed, and the ledger reads an absent file as a failed suite");
    }

    [Fact]
    public void TheScripts_NameThemselvesOnTheirFirstLine_ForTheFetchCheck()
    {
        foreach (var script in new[] { KeyScript, LedgerScript })
        {
            var head = File.ReadAllText(Path.Combine(FindRepoRoot(), script));
            head = head[..Math.Min(400, head.Length)];
            Assert.Contains(Path.GetFileName(script), head, StringComparison.Ordinal);
        }
    }

    private static string JobBody(string job)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Lane));
        var match = Regex.Match(text, @"\n  " + Regex.Escape(job) + @":\n(?<body>(?:(?:    .*|  #.*)\n|\n)+?)(?=  [a-z][a-z-]*:\n|\z)");
        Assert.True(match.Success, $"{Lane} must have a `{job}` job");
        return string.Join('\n', match.Groups["body"].Value.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root (MeshWeaver.slnx) above the test bin");
        return dir!.FullName;
    }
}
