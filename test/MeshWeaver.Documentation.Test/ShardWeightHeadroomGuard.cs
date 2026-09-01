using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for MeshWeaver#2747: no test project may be scheduled as a single unit whose
/// measured weight sits close to the per-project wall-clock cap it runs under.
///
/// <para><b>Why the balance rule was not enough.</b> <c>shard-assign.sh</c> already had a split
/// rule — split when a project's solo weight exceeds the ideal shard load — and it is about
/// BALANCE, i.e. the long pole. <c>MeshWeaver.PluginCatalog.Test</c> passed that rule comfortably
/// (348 s against a ~421 s ideal load) and was nonetheless one slow runner away from being killed:
/// measured 315 / 320 / 315 s on three consecutive runs, then <b>480 s exit=124 TIMEOUT</b> on
/// <c>e12697ebd</c> (run 33302269352, shard 0) with the SAME tree passing at 320 s on the very next
/// run. No change was responsible.</para>
///
/// <para><b>Why that costs more than a slow shard.</b> A <c>timeout</c> kill produces a red that
/// names tests which did not really fail — five of them, that time — so every occurrence costs a
/// full investigation before it can be dismissed, and this repo's rules (correctly) forbid simply
/// re-running it to see. Raising the cap is not the alternative: the cap is what turns a wedge into
/// a bounded, attributable failure instead of a 20-minute shard.</para>
///
/// <para><b>Why this is a guard and not a one-time split.</b> The weight table is hand-maintained
/// and its drift is INVISIBLE — the LPT loop keeps reporting six perfectly balanced shards because
/// it balances the numbers, not the clock. The file's own header records two occasions on which the
/// table drifted for weeks unnoticed, and PluginCatalog's own entry moved 30 → 348 s. A comment
/// asking the next person to check headroom is exactly the kind of instruction this repo has
/// watched go unread; the arithmetic is mechanical, so a test can do it.</para>
///
/// <para>The cap is READ from the workflow rather than restated here, so the guard cannot quietly
/// disagree with the budget it is guarding.</para>
/// </summary>
public class ShardWeightHeadroomGuard
{
    private const string WeightsPath = ".github/scripts/shard-assign.sh";
    private const string WorkflowPath = ".github/workflows/dotnet-test.yml";

    /// <summary>
    /// The fraction of the cap a single scheduled unit may occupy. 0.6 is not a taste: PluginCatalog
    /// was killed from 0.66–0.72, and the CI/local ratio this repo has measured (~1.7×) means the
    /// spread between a healthy and a saturated runner is comfortably larger than the remaining 40%.
    /// </summary>
    private const double MaxCapFraction = 0.6;

    [Fact(Timeout = 60000)]
    public void NoScheduledUnitRunsCloseToThePerProjectCap()
    {
        var root = SourceScan.FindRepoRoot();
        var capSeconds = ReadPerProjectCapSeconds(File.ReadAllText(Path.Combine(root, WorkflowPath)));
        var budget = capSeconds * MaxCapFraction;

        var units = ReadWeights(File.ReadAllText(Path.Combine(root, WeightsPath))).ToArray();

        // 🚨 A NON-VACUITY floor, not a policy one: it catches a parse that returned nothing, and
        // must therefore track the table's real size. 41 entries -> 24 when 17 mesh suites moved to
        // MeshWeaver.Plugins (#2847), -> 14 when tranche 2 took nine more plus two dead support
        // libraries. Lower it as the table shrinks; a floor left above the real count fails every
        // run and says "the parse broke" about a parse that worked.
        units.Length.Should().BeGreaterThanOrEqualTo(10,
            "the weight table must actually have been parsed — a short list means the parse broke, "
            + "not that the table is healthy, and a guard that measures nothing must fail loudly");

        var overBudget = units
            .Where(u => u.PerUnitSeconds > budget)
            .OrderByDescending(u => u.PerUnitSeconds)
            .ToArray();

        overBudget.Should().BeEmpty(
            $"a scheduled unit must leave headroom against the {capSeconds:0} s per-project cap in "
            + $"{WorkflowPath}, or one slow runner turns it into exit=124 — a red naming tests that "
            + "did not fail (MeshWeaver#2747). Split the project by adding a parts column in "
            + $"{WeightsPath}; do NOT raise the cap, which is what bounds a wedge. Over "
            + $"{budget:0} s per unit: "
            + string.Join(", ", overBudget.Select(u =>
                $"{u.Name} ({u.TotalSeconds} s ÷ {u.Parts} part(s) = {u.PerUnitSeconds:0} s)")));
    }

    /// <summary>
    /// 🚨 The guard's own failure modes, asserted rather than assumed — both halves of the parse are
    /// places it could silently stop measuring: a cap it fails to find, and a parts column it
    /// ignores (which would make every split project look over budget, or every unsplit one look
    /// fine, depending on which way the bug went).
    /// </summary>
    [Fact(Timeout = 30000)]
    public void TheGuardParsesTheCapAndThePartsColumn()
    {
        ReadPerProjectCapSeconds("( cd \"$dir\" && timeout --signal=TERM --kill-after=30s 8m \\")
            .Should().Be(480);
        ReadPerProjectCapSeconds("timeout --signal=TERM --kill-after=30s 90s \\").Should().Be(90);

        var parsed = ReadWeights("WEIGHTS=$(cat <<'EOF'\n600 Big.Test 2\n100 Small.Test\nEOF\n)").ToArray();
        parsed.Should().HaveCount(2);
        parsed.Single(u => u.Name == "Big.Test").PerUnitSeconds.Should().Be(300,
            "a split project's scheduled unit is 1/N of its weight — ignoring the column would "
            + "report a correctly-split project as over budget");
        parsed.Single(u => u.Name == "Small.Test").PerUnitSeconds.Should().Be(100);
    }

    private readonly record struct Unit(string Name, int TotalSeconds, int Parts)
    {
        public double PerUnitSeconds => (double)TotalSeconds / Parts;
    }

    /// <summary>The <c>timeout … 8m</c> that wraps each project's test host, in seconds.</summary>
    private static double ReadPerProjectCapSeconds(string workflow)
    {
        var m = Regex.Match(workflow, @"timeout\s+--signal=TERM\s+--kill-after=30s\s+(\d+)([smh])");
        Assert.True(m.Success,
            $"could not find the per-project `timeout` in {WorkflowPath}. The guard reads the cap "
            + "from the workflow on purpose, so restore the parse rather than hard-coding a number "
            + "here — a guard holding its own copy of the budget is how the two drift apart.");
        var value = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch { "s" => value, "m" => value * 60, _ => value * 3600 };
    }

    /// <summary>The <c>&lt;seconds&gt; &lt;project&gt; [parts]</c> table, as scheduled units.</summary>
    private static IEnumerable<Unit> ReadWeights(string script)
    {
        var body = Regex.Match(script, @"WEIGHTS=\$\(cat <<'EOF'\n(.*?)\nEOF", RegexOptions.Singleline);
        Assert.True(body.Success,
            $"could not find the WEIGHTS table in {WeightsPath} — the parse broke, and an empty "
            + "table would let this guard pass having measured nothing.");

        foreach (var line in body.Groups[1].Value.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w))
                continue;
            var n = parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 1;
            yield return new Unit(parts[1], w, Math.Max(1, n));
        }
    }
}
