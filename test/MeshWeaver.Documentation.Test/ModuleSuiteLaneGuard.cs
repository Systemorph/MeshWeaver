#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for WHERE a module's own test suite runs in the reusable module-pack lane.
///
/// <para>A <c>needs:</c> on a <c>uses:</c> job waits for the WHOLE called workflow, so anything
/// inside the last job of that lane sits on the critical path of every gate the caller hangs off
/// it. Measured on MeshWeaver.Plugins run 33656010754 (a 39-minute pull request):
/// <c>Module bundle (MeshWeaver.AI)</c> took 13.4 minutes, of which 12.7 were the module's own
/// suite and 0.2 the build — so the bundle artifact the two required gates COMPOSE was ready 0.2
/// minutes in, and those gates started at 32.8. So on a run that does not publish the suite moves
/// to a lane of its own beside the pack matrix; on a run that DOES publish it stays inline, before
/// the hand-over, because there the ordering buys the one property worth its cost: a failing suite
/// never publishes.</para>
///
/// <para>🚨 The whole construction is only safe while three things hold, and none of them is
/// visible in a green run — which is why they are asserted here. The two conditions must be
/// COMPLEMENTS of one input (two `if:`s can both be false, and a suite that ran nowhere renders
/// exactly like one that passed); the tests lane must not depend on the pack chain it is meant to
/// run beside; and <c>verify</c> — the lane's one stable context, <c>All selected bundles built</c>,
/// the context a repo's branch protection requires — must still go RED when a suite does, and must
/// pair every DELEGATED suite against positive evidence that it ran.</para>
/// </summary>
public class ModuleSuiteLaneGuard
{
    private const string Lane = ".github/workflows/node-repo-module-pack.yml";
    private const string Verifier = ".github/scripts/node-repo-pack-verify.py";

    [Fact]
    public void TheInlineSuite_RunsOnlyWhenThisCallPublishes()
    {
        var pack = JobBody("pack");
        Assert.Contains(
            "if: ${{ inputs.publish && matrix.entry.test != false && steps.plan.outputs.need_test == 'true' }}",
            pack, StringComparison.Ordinal);
        // Still AFTER the bundle upload and BEFORE the hand-over when it does run: that ordering is
        // the entire reason a publishing run keeps paying for it.
        var upload = pack.IndexOf("name: module-bundle-${{ matrix.entry.module }}", StringComparison.Ordinal);
        var suite = pack.IndexOf("dotnet test \"$tests\"", StringComparison.Ordinal);
        var publish = pack.IndexOf("-X POST \"$REGISTRY/api/plugins/bundles/$PACKAGE", StringComparison.Ordinal);
        Assert.True(upload >= 0 && suite >= 0 && publish >= 0, "the pack job must still upload, test and publish");
        Assert.True(upload < suite && suite < publish,
            "on a publishing run the inline suite must sit AFTER the bundle upload and BEFORE the hand-over — "
            + "a failing suite may not publish, and may not un-build either");
    }

    [Fact]
    public void TheTestsLane_IsTheExactComplement_AndDependsOnNothingItShouldRunBeside()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Lane));
        Assert.True(Regex.IsMatch(text, @"\n  tests:\n"), $"{Lane} must have a `tests` job");

        var tests = JobBody("tests");
        // The complement of the inline step's `inputs.publish &&`, on the SAME input — never on
        // github.event_name, which would diverge from what `publish` already means here (core's own
        // main-cd calls this lane with publish:false and is not a pull request).
        Assert.Contains("!inputs.publish", tests, StringComparison.Ordinal);
        Assert.DoesNotContain("github.event_name", tests, StringComparison.Ordinal);
        // A matrix with zero vectors does not evaluate, so the count is asserted the way `pack`
        // asserts its own.
        Assert.Contains("needs.select.outputs.test-count != '0'", tests, StringComparison.Ordinal);
        Assert.Contains("entry: ${{ fromJson(needs.select.outputs.test-modules) }}", tests, StringComparison.Ordinal);

        // 🚨 `select` ONLY. Depending on prepare / build-workspace / pack would put the suite back
        // on the very chain this job exists to run beside, and the change would measure as nothing.
        var needs = Regex.Match(tests, @"\n    needs: (?<needs>.+)\n");
        Assert.True(needs.Success, "the tests job must declare its needs");
        Assert.Equal("select", needs.Groups["needs"].Value.Trim());

        // The suite itself is a MOVE, not a rewrite: same command, same evidence trail.
        Assert.Contains("dotnet test \"$tests\"", tests, StringComparison.Ordinal);
        Assert.Contains("-p:MeshWeaverRoot=$GITHUB_WORKSPACE/meshweaver", tests, StringComparison.Ordinal);
        Assert.Contains("MESHWEAVER_TEST_FILE_LOGS", tests, StringComparison.Ordinal);
        Assert.Contains("DOTNET_DbgEnableMiniDump", tests, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryLegRecords_WhichLaneOwnsItsSuite_AndTheTestsLaneProvesItRanOne()
    {
        var pack = JobBody("pack");
        // `none` | `inline` | `lane`, written into the receipt rather than inferred afterwards from
        // an `if:` — the pairing below is what makes "it ran nowhere" impossible to do quietly.
        Assert.Contains(
            "TESTS: ${{ steps.plan.outputs.need_test != 'true' && 'none' || (inputs.publish && 'inline' || 'lane') }}",
            pack, StringComparison.Ordinal);
        Assert.Contains("tests:$t", pack, StringComparison.Ordinal);

        var tests = JobBody("tests");
        Assert.Contains("tests:\"lane\"", tests, StringComparison.Ordinal);
        // Lane-stamped and lane-named, like every other artifact here: artifacts are RUN-WIDE and a
        // repo may call this lane twice in one run.
        Assert.Contains("name: module-tests-receipt-${{ needs.select.outputs.lane }}-${{ matrix.entry.module }}",
            tests, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", tests, StringComparison.Ordinal);
        // The receipt means the suite got to the end green, so it is the LAST evidence step.
        var suite = tests.IndexOf("dotnet test \"$tests\"", StringComparison.Ordinal);
        var receipt = tests.IndexOf("name: Drop the test receipt", StringComparison.Ordinal);
        Assert.True(suite >= 0 && receipt > suite,
            "the test receipt must be dropped AFTER the suite it attests");
    }

    [Fact]
    public void Verify_StaysTheOneStableContext_AndStillRedsOnARedSuite()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Lane));
        // 🚨 The context NAME is unchanged on purpose: a renamed required context waits forever,
        // and six repos call this lane.
        Assert.Contains("    name: All selected bundles built\n", text, StringComparison.Ordinal);

        var verify = JobBody("verify");
        Assert.Contains("needs: [select, pack, tests]", verify, StringComparison.Ordinal);
        Assert.Contains("if: always()", verify, StringComparison.Ordinal);
        Assert.Contains("TESTS_RESULT: ${{ needs.tests.result }}", verify, StringComparison.Ordinal);
        Assert.Contains("--tests-result \"$TESTS_RESULT\"", verify, StringComparison.Ordinal);
        Assert.Contains("--test-receipts \"$RUNNER_TEMP/test-receipts\"", verify, StringComparison.Ordinal);
        Assert.Contains("pattern: module-tests-receipt-${{ needs.select.outputs.lane }}-*", verify, StringComparison.Ordinal);

        // 🚨 …and `bundles-built` must NOT move with it. It is what a gate that only COMPOSES the
        // bundle depends on, so a red suite may not read as "bundle missing" there (#2710,
        // Plugins#937). The verifier answers it from the built markers alone.
        var verifier = File.ReadAllText(Path.Combine(FindRepoRoot(), Verifier));
        var body = Regex.Match(verifier, @"\ndef bundles_built\((?<b>.*?)\n\ndef ", RegexOptions.Singleline);
        Assert.True(body.Success, "the verifier must define bundles_built");
        Assert.DoesNotContain("tests", body.Groups["b"].Value, StringComparison.Ordinal);
        // The accounting the workflow calls, and its own self-test, both exist.
        Assert.Contains("def test_lane(", verifier, StringComparison.Ordinal);
        Assert.Contains("--tests-result", verifier, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_PublishesTheSuiteSubset_UsingThePackPlansOwnPredicate()
    {
        var select = JobBody("select");
        Assert.Contains("test-modules: ${{ steps.suites.outputs.modules }}", select, StringComparison.Ordinal);
        Assert.Contains("test-count: ${{ steps.suites.outputs.count }}", select, StringComparison.Ordinal);
        // The same predicate the pack job's plan step answers as `need_test`. The two are
        // cross-checked by the verifier rather than trusted to agree, but they must at least be
        // written as one rule.
        Assert.Contains("if (.ledger.decision // \"build\") == \"reuse\"", select, StringComparison.Ordinal);
        Assert.Contains("then (.ledger.needTest == true)", select, StringComparison.Ordinal);
        Assert.Contains("else (.test != false) end", select, StringComparison.Ordinal);
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
