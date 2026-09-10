using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// #3963: deletion in the prebuilt-bundle store is NEVER armed by omission.
///
/// <para><b>The defect this pins.</b> <see cref="PrebuiltBundleRetention.Delete"/> defaults to
/// <c>true</c>, so an instance that mounts <c>PreWarm:PrebuiltBundleRoot</c> and says nothing else
/// sweeps ARMED. Worse, the knob that would disarm it was unreachable: the portal ConfigMap
/// template enumerates every key it renders EXPLICITLY — there is no catch-all range over
/// <c>.Values.config</c> — so a values file, a deployment overlay or a Deployment record's
/// <c>extraPortalConfig</c> setting <c>PreWarm__PrebuiltBundleRetention__Delete</c> reached no
/// container, with nothing erroring anywhere. The same silent-drop shape cost the CI-bake lane
/// months of inertness (#1660 WS3) and twelve OpenRouter model keys (#2203); here the omission does
/// not merely disable a feature, it DELETES.</para>
///
/// <para><b>The invariant, stated so it survives either fix.</b> Deletion must be off unless
/// somebody wrote it down. That holds if the CODE default is already <c>false</c>, or if the chart
/// binds the key with an explicit <c>default "false"</c>. It does NOT hold when the code default is
/// <c>true</c> and the chart is silent — which is exactly the state this guard was written for. So
/// the guard reads the live default off the type rather than assuming it, and only then demands the
/// chart line.</para>
///
/// <para>🚨 The guard follows <see cref="PrebuiltBundleRetentionExtensions.DeleteConfigKey"/>, not a
/// copied string: renaming the config key moves the guard's subject with it instead of leaving a
/// check that passes having verified nothing.</para>
///
/// <para>See Doc/Architecture/PrebuiltBundleRetention.</para>
/// </summary>
public class PrebuiltBundleRetentionArmingGuard
{
    /// <summary>The config key as it lands in the rendered ConfigMap (<c>:</c> ⇒ <c>__</c>).</summary>
    private static string EnvKey =>
        PrebuiltBundleRetentionExtensions.DeleteConfigKey.Replace(":", "__", StringComparison.Ordinal);

    [Fact]
    public void DeletionIsNeverArmedByOmission()
    {
        var root = FindRepoRoot();
        var configMap = File.ReadAllText(
            Path.Combine(root, "deploy", "helm", "templates", "memex-portal", "config.yaml"));

        // Read the shipped default rather than assuming it — if the code default is ever flipped to
        // false, the invariant is satisfied at the source and this guard says so instead of
        // demanding a chart line that would then be redundant.
        if (!PrebuiltBundleRetention.Default.Delete)
            return;

        // 🚨 BOUND, not merely mentioned. The template's explanatory `{{- /* ... */}}` comment
        // blocks open with the very key they describe, and no '#'-stripping removes them — so a
        // check satisfied by prose is not a check. Requiring the value binding is a shape a comment
        // cannot accidentally have.
        var binding = new Regex(
            @"^\s*" + Regex.Escape(EnvKey) + @"\s*:\s*""\{\{(?<expr>.*?)\}\}""\s*$",
            RegexOptions.Multiline);
        var match = binding.Match(configMap);

        Assert.True(match.Success,
            $"PrebuiltBundleRetention.Delete defaults to TRUE in code, so {EnvKey} MUST be bound in "
            + "deploy/helm/templates/memex-portal/config.yaml — otherwise every instance that mounts "
            + "PreWarm__PrebuiltBundleRoot sweeps with deletion ARMED and the knob that would disarm "
            + "it reaches no container (the configmap enumerates keys explicitly; a values file or a "
            + "Deployment record's extraPortalConfig setting it is silently dropped). Add:\n"
            + $"  {EnvKey}: \"{{{{ .Values.config.memex_portal.{EnvKey} | default \"false\" }}}}\"");

        Assert.Contains("default \"false\"", match.Groups["expr"].Value, StringComparison.Ordinal);

        // The chart's own default is the fleet's report-only setting; values.yaml states it so the
        // deployed surface stays reviewable, and so EveryPreWarmKeyInValues_IsTemplatedInTheConfigMap
        // keeps the two halves tied together.
        var values = File.ReadAllLines(Path.Combine(root, "deploy", "helm", "values.yaml"))
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith('#'))
            .FirstOrDefault(l => l.StartsWith(EnvKey + ":", StringComparison.Ordinal));

        Assert.True(values is not null,
            $"deploy/helm/values.yaml must declare {EnvKey} under config.memex_portal — the chart "
            + "default is what makes the fleet report-only, and an undeclared key makes that "
            + "invisible to anyone reading the values file.");
        Assert.Contains("\"false\"", values!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The report NAMES ITS DENOMINATOR, so a sweep that examined nothing cannot read as a sweep
    /// that found nothing. This is the same distinction the rest of the fleet gets wrong: "0
    /// collectable over 482 identities" and "0 collectable because the root was empty" are the same
    /// number and opposite facts. Asserted here on the shipped format string, so a later reword that
    /// dropped the count would be caught even though every rule test would still pass.
    /// </summary>
    [Fact]
    public void TheSummaryFormat_LeadsWithTheCountExamined_NotWithTheCountCollected()
    {
        var empty = new PrebuiltBundleSweepPlan(
            "s-live", "3.1.0-ci.9000", [], [], System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
            [], null, []);

        Assert.StartsWith("0 identity directory(ies)", empty.Summary, StringComparison.Ordinal);
        Assert.Contains("collectable=0", empty.Summary, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate the repository root (MeshWeaver.slnx).");
        return dir!.FullName;
    }
}
