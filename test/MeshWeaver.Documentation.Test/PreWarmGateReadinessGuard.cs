using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The chart stated its own startup budget in prose, and the prose was wrong</b> (#2787).
///
/// <para><c>PreWarm__GateReadiness</c> holds <c>/health</c> red until the NodeType bake is green,
/// which is only safe with three paired settings — the chart's own comment lists them, numbered,
/// and warns that missing one "means Kubernetes kills the pod mid-bake, every time, forever".
/// Prerequisite 2 is a budget: <c>probes.startup.periodSeconds × failureThreshold</c> must cover a
/// full cold bake.</para>
///
/// <para><b>What was actually there.</b> The deployment template asserted "values.yaml
/// (probes.startup) — 10 × 180 = 30 min" and, forty lines down, "which managed envs set to
/// 10 × 180 = 30 MINUTES". `values.yaml` shipped <c>5 × 60</c>, and memex-cloud was measured live
/// on 2026-08-30 running exactly that. So an operator arming the gate would read prerequisite 2,
/// believe it already satisfied, and walk straight into the failure the same comment warns about.
/// That is the <i>prose asserts a guard that does not exist</i> shape, in the one file where being
/// wrong costs a rollout.</para>
///
/// <para>So the coupling stops being a paragraph. This guard reads the numbers the chart actually
/// ships and fails when the chart TALKS about a budget it does not have, or when the gate is armed
/// without every prerequisite moving in the same change.</para>
/// </summary>
public class PreWarmGateReadinessGuard
{
    private const string Deployment = "deploy/helm/templates/memex-portal/deployment.yaml";
    private const string Values = "deploy/helm/values.yaml";

    /// <summary>
    /// The cold-bake ceiling the chart itself derives, from production Loki on 2026-08-10:
    /// ~2.4 s per NodeType, strictly sequential, ~240 types on the largest mesh we run ⇒ ~570 s.
    /// Plus the plain cold boot (schema provisioning + static import) the ungated budget covers.
    /// A gated environment needs BOTH, which is why arming the gate is never a one-key change.
    /// </summary>
    private const int ColdBakeSeconds = 570;

    private const int PlainColdBootSeconds = 300;

    [Fact]
    public void TheChart_NeverStatesAStartupBudgetItDoesNotShip()
    {
        var root = FindRepoRoot();
        var (period, threshold) = StartupBudget(File.ReadAllText(Path.Combine(root, Values)));
        var shipped = period * threshold;

        // Every "N × M" the chart writes about the startup probe must be the one it ships. The
        // multiplication sign is the ASCII 'x' or the '×' the chart uses; both are matched.
        foreach (var file in new[] { Deployment, Values })
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            foreach (Match m in Regex.Matches(text, @"(?<a>\d{1,4})\s*[x×]\s*(?<b>\d{1,4})"))
            {
                var a = int.Parse(m.Groups["a"].Value, CultureInfo.InvariantCulture);
                var b = int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture);
                var line = LineOf(text, m.Index);

                // Only lines that are TALKING about the startup probe are in scope; the chart does
                // arithmetic about other things, and this guard must not police those.
                if (!MentionsStartupBudget(line)) continue;

                // A line may legitimately quote a SUGGESTED paired setting for a gated
                // environment — values.yaml does exactly that — as long as it is marked as the
                // thing to move TO, not as the thing that is there.
                if (IsSuggestion(line)) continue;

                Assert.True(a * b == shipped,
                    $"{file} states a startup budget of {a} × {b} = {a * b}s on this line:\n"
                    + $"    {line.Trim()}\n"
                    + $"but {Values} ships probes.startup {period} × {threshold} = {shipped}s. "
                    + "A chart that describes a budget it does not have is how PreWarm__GateReadiness "
                    + "gets armed on a five-minute probe: the operator reads prerequisite 2, believes "
                    + "it satisfied, and every pod is killed mid-bake. State what ships, or mark the "
                    + "number as a suggested paired setting.");
            }
        }
    }

    [Fact]
    public void ArmingTheReadinessGate_MovesEveryPrerequisiteInTheSameChange()
    {
        var root = FindRepoRoot();
        var values = File.ReadAllText(Path.Combine(root, Values));

        if (!BoolKey(values, "PreWarm__GateReadiness"))
            // 🚦 The gate is OFF in this chart, which is the fleet's setting and the safe default.
            // This is a CONDITIONAL invariant, and its unconditional half — the one that fails
            // today if the chart's prose drifts — is the test above. Both must exist: a guard that
            // only fires once someone arms the gate would have caught nothing on the day the prose
            // went wrong.
            return;

        var (period, threshold) = StartupBudget(values);
        var required = ColdBakeSeconds + PlainColdBootSeconds;
        Assert.True(period * threshold >= required,
            $"PreWarm__GateReadiness is armed, but probes.startup is {period} × {threshold} = "
            + $"{period * threshold}s and a gated boot needs at least {required}s "
            + $"({ColdBakeSeconds}s cold bake + {PlainColdBootSeconds}s plain boot). Kubernetes "
            + "kills the pod mid-bake, every time, forever — prerequisite 2 of the chart's own list.");

        Assert.True(BoolKey(values, "PreWarm__DynamicTypes"),
            "PreWarm__GateReadiness is armed without PreWarm__DynamicTypes: the gate reads state "
            + "only the sweep writes, so gate-without-sweep is permanently GREEN — a gate that "
            + "certifies nothing. Prerequisite 1 of the chart's own list.");

        var deployment = File.ReadAllText(Path.Combine(root, Deployment));
        Assert.True(Regex.IsMatch(deployment, @"maxUnavailable:\s*0\b"),
            "PreWarm__GateReadiness is armed without strategy.maxUnavailable: 0. The gate works by "
            + "making the NEW pod refuse readiness, which protects nothing if the serving pod was "
            + "already deleted. Prerequisite 3 of the chart's own list.");

        Assert.True(Regex.IsMatch(deployment, @"startupProbe:(?s).{0,400}?path:\s*/health"),
            "PreWarm__GateReadiness is armed but the startupProbe does not target /health — the "
            + "only endpoint that reads the gate. A namespace probing /alive or /healthz ignores it "
            + "entirely, so the gate is silently useless.");
    }

    private static bool MentionsStartupBudget(string line) =>
        line.Contains("startup", StringComparison.OrdinalIgnoreCase)
        || line.Contains("failureThreshold", StringComparison.Ordinal)
        || line.Contains("periodSeconds", StringComparison.Ordinal)
        || line.Contains("min", StringComparison.OrdinalIgnoreCase)
           && line.Contains("bake", StringComparison.OrdinalIgnoreCase);

    /// <summary>A number offered as the setting to move TO, not claimed as the setting in force.</summary>
    private static bool IsSuggestion(string line) =>
        line.Contains("suggested", StringComparison.OrdinalIgnoreCase)
        || line.Contains("raise", StringComparison.OrdinalIgnoreCase)
        || line.Contains("→", StringComparison.Ordinal);

    private static (int Period, int Threshold) StartupBudget(string values)
    {
        var block = Regex.Match(values, @"^probes:\s*$(?<body>(?:\n(?:[ \t].*)?)+)",
            RegexOptions.Multiline);
        Assert.True(block.Success,
            $"{Values} no longer has a top-level 'probes:' block — this guard reads the budget from "
            + "it, and a guard that cannot find its subject passes having checked nothing.");
        var body = block.Groups["body"].Value;
        var period = Regex.Match(body, @"periodSeconds:\s*(\d+)");
        var threshold = Regex.Match(body, @"failureThreshold:\s*(\d+)");
        Assert.True(period.Success && threshold.Success,
            $"{Values} probes.startup no longer declares both periodSeconds and failureThreshold.");
        return (int.Parse(period.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(threshold.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static bool BoolKey(string values, string key) =>
        Regex.Match(values, Regex.Escape(key) + @":\s*""?(?<v>true|false)""?", RegexOptions.IgnoreCase)
            is { Success: true } m
        && string.Equals(m.Groups["v"].Value, "true", StringComparison.OrdinalIgnoreCase);

    private static string LineOf(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Min(index, text.Length - 1)) + 1;
        var end = text.IndexOf('\n', index);
        return end < 0 ? text[start..] : text[start..end];
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
