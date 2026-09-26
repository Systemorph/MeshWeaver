using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>The chart stated its own startup budget in prose, and the prose was wrong</b> (#2787) — and
/// <b>the NodeType bake gate is read by READINESS, never by the startup probe</b> (policy
/// <c>bake-gate-readiness-only</c>, #5544).
///
/// <para><b>#2787.</b> The deployment template asserted "values.yaml (probes.startup) — 10 × 180 =
/// 30 min" while <c>values.yaml</c> shipped <c>5 × 60</c>, and memex-cloud was measured live on
/// 2026-08-30 running exactly that. A chart that describes a budget it does not have is how an
/// operator believes a prerequisite satisfied when it is not. So the first test reads the numbers
/// the chart ships and fails when the chart TALKS about a startup budget it does not have.</para>
///
/// <para><b>#5544.</b> <c>PreWarm__GateReadiness</c> used to hold <c>/health</c> red, the startup
/// probe read it, and "arming the gate" meant raising the startup budget to cover a cold bake. A
/// refusal then killed every container at the end of that budget (three hours on memex), restarted
/// pods of the serving image included. The gate's verdict now reaches <c>/ready</c> only, so the
/// prerequisites the second test holds are: the sweep it reads, <c>maxUnavailable: 0</c>, a
/// readiness probe on <c>/ready</c>, and a rollout deadline that carries the bake's time.</para>
/// </summary>
public class PreWarmGateReadinessGuard
{
    private const string Deployment = "deploy/helm/templates/memex-portal/deployment.yaml";
    private const string Values = "deploy/helm/values.yaml";

    /// <summary>
    /// The cold-bake ceiling the chart itself derives, from production Loki on 2026-08-10:
    /// ~2.4 s per NodeType, strictly sequential, ~240 types on the largest mesh we run ⇒ ~570 s.
    /// It is spent holding READINESS after startup, so it belongs on the rollout deadline.
    /// </summary>
    private const int ColdBakeSeconds = 570;

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
        var deployment = File.ReadAllText(Path.Combine(root, Deployment));

        // 🚦 Unconditional half (policy bake-gate-readiness-only, #5544): whether or not THIS chart
        // arms the gate, the rollout deadline must carry the bake term, because the gate's time is
        // spent AFTER startup, holding readiness. A template that derived the deadline from the
        // startup budget alone would report every armed cold bake as a failed roll.
        Assert.True(Regex.IsMatch(deployment,
                @"progressDeadlineSeconds:[^\n]*\.Values\.probes\.startup\.periodSeconds[^\n]*\.Values\.probes\.startup\.failureThreshold[^\n]*probes\.rollGate\)\.bakeSeconds"),
            $"{Deployment} no longer derives progressDeadlineSeconds from the startup budget PLUS "
            + "probes.rollGate.bakeSeconds (in every render — the gate can be armed by an env "
            + "source the render cannot see). The gate holds "
            + "READINESS for the whole cold bake after the pod has started, so a deadline without the "
            + "bake term reports a legitimately baking pod as a failed roll — the signal an operator "
            + "acts on by rolling back the good image.");
        Assert.True(RollGateBakeSeconds(values) >= ColdBakeSeconds,
            $"{Values} probes.rollGate.bakeSeconds is {RollGateBakeSeconds(values)}s, below the "
            + $"{ColdBakeSeconds}s a cold bake of the largest mesh we run takes.");

        if (!BoolKey(values, "PreWarm__GateReadiness"))
            // 🚦 The gate is OFF in this chart, which is the fleet's setting and the safe default.
            // The rest is a CONDITIONAL invariant; the unconditional halves are the assertions above
            // and the prose test before this one.
            return;

        Assert.True(BoolKey(values, "PreWarm__DynamicTypes"),
            "PreWarm__GateReadiness is armed without PreWarm__DynamicTypes: the gate reads state "
            + "only the sweep writes, so gate-without-sweep is permanently GREEN — a gate that "
            + "certifies nothing. Prerequisite 1 of the chart's own list.");

        Assert.True(Regex.IsMatch(deployment, @"maxUnavailable:\s*0\b"),
            "PreWarm__GateReadiness is armed without strategy.maxUnavailable: 0. The gate works by "
            + "making the NEW pod refuse readiness, which protects nothing if the serving pod was "
            + "already deleted. Prerequisite 3 of the chart's own list.");

        // 🚨 The gate's ONE reader is the readiness probe on /ready (policy
        // bake-gate-readiness-only). It is NOT the startup probe any more: a startup probe that
        // never records a success KILLS the container, and on 2026-09-25/26 that killed every
        // container of both images, restarted pods of the serving image included.
        Assert.True(Regex.IsMatch(deployment,
                @"readinessProbe:(?s)(?:(?!livenessProbe:).)*?httpGet:\s*\{\s*path:\s*/ready\b"),
            "PreWarm__GateReadiness is armed but the readinessProbe does not read /ready — the only "
            + "endpoint that reads the gate (checks tagged `ready` plus the roll gates). The gate is "
            + "then registered, never read, and every rollout completes as if it had passed.");
    }

    /// <summary>The bake budget <c>probes.rollGate.bakeSeconds</c> the chart ships.</summary>
    private static int RollGateBakeSeconds(string values)
    {
        var m = Regex.Match(values, @"^  rollGate:\s*$(?:\n(?:\s*#.*|\s*))*?\n\s+bakeSeconds:\s*(?<n>\d+)",
            RegexOptions.Multiline);
        Assert.True(m.Success,
            $"{Values} declares no probes.rollGate.bakeSeconds — the template adds it to the rollout "
            + "deadline when the gate is armed, and a guard that cannot find it checks nothing.");
        return int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
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
