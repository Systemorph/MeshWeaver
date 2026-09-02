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
/// verdicts; and that every ledger write is best-effort while the two scripts' self-tests run on every
/// run. A lane that quietly dropped one of those would still be green — a duplicate build costs money
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
        // Both outputs the downstream jobs key on.
        Assert.Contains("build-modules: ${{ steps.ledger.outputs.build }}", select, StringComparison.Ordinal);
        Assert.Contains("modules: ${{ steps.ledger.outputs.modules }}", select, StringComparison.Ordinal);
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

        var upload = At("name: module-bundle-${{ matrix.entry.module }}");
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
        // The verdict steps run on failure / cancellation only.
        Assert.Contains("if: failure() && inputs.ledger == 'required' && steps.plan.outputs.key != ''", pack, StringComparison.Ordinal);
        Assert.Contains("if: cancelled() && inputs.ledger == 'required' && steps.plan.outputs.key != ''", pack, StringComparison.Ordinal);
        // The reuse leg verifies the bytes against the record and never packs anyway.
        Assert.Contains("gh run download \"$ART_RUN\"", pack, StringComparison.Ordinal);
        Assert.Contains("is not the ledger's $EXPECTED_SHA", pack, StringComparison.Ordinal);
        // The suite writes the evidence the ledger records.
        Assert.Contains("--logger \"trx;LogFileName=ledger.trx\"", pack, StringComparison.Ordinal);
        // The reuse window is the artifact's retention.
        Assert.Contains("retention-days: 7", pack, StringComparison.Ordinal);
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
