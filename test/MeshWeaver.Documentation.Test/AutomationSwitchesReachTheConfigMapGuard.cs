using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 Every <c>Hosting__*__Enabled</c> switch the portal ConfigMap renders must carry a record's
/// explicit <c>false</c> into the pod — asserted by RENDERING the chart, not by reading it.
///
/// <para><b>Why this guard exists.</b> The control instance's record set
/// <c>Hosting__Coordinator__Enabled=false</c> and <c>Hosting__StuckDetector__Enabled=false</c>
/// while both kept running model rounds: the memex-portal ConfigMap names every key explicitly and
/// had no line for either, so the setting reached no container and helm reported success. These
/// keys are set ONLY on deployment records (<c>extraPortalConfig</c>), never in a values file this
/// repository ships, so <see cref="PlatformBakeLaneGuard.EveryConfigKeySetInAValuesFile_IsBoundInTheConfigMap"/>
/// — which discovers its keys from those values files — cannot see them: deleting or misspelling
/// one of these template lines would recreate the incident with every other guard green.</para>
///
/// <para><b>Data-driven in both directions.</b> The keys are discovered from the template, and the
/// discovered set must equal <see cref="RecordOnlySwitches"/> ∪ <see cref="AlwaysRendered"/>. A new
/// <c>Hosting__*__Enabled</c> line therefore fails here until it is classified (and so tested), and
/// a listed switch whose line disappears fails twice — once on the set, once on the render.</para>
///
/// <para><b>The value is an UNQUOTED YAML <c>false</c></b>, because that is what a record's
/// overlay writes and it is the shape a <c>default ""</c> or a bare <c>if</c> drops as empty.</para>
/// </summary>
public class AutomationSwitchesReachTheConfigMapGuard
{
    private const string ConfigMapTemplate = "templates/memex-portal/config.yaml";

    /// <summary>
    /// Opt-out switches that exist only on deployment records: rendered when the key is present,
    /// absent from the ConfigMap when it is not (so the code default — ON — applies).
    /// </summary>
    private static readonly ImmutableArray<string> RecordOnlySwitches =
    [
        "Hosting__Coordinator__Enabled",
        "Hosting__StuckDetector__Enabled",
        "Hosting__Triage__PullRequestReview__Enabled",
        "Hosting__Triage__Agent__Enabled",
        "Hosting__TriageAssignments__Enabled",
        "Hosting__Triage__PullRequestSweep__Enabled",
        "Hosting__Triage__IssueSweep__Enabled",
        "Hosting__PrBabysitter__Enabled",
        "Hosting__BugFix__Enabled",
    ];

    /// <summary>
    /// Switches the template renders unconditionally from a chart value with its own default —
    /// <c>Hosting__Operator__Enabled</c> is <c>hostingOperator.enabled | default "false"</c>, a
    /// security property, not a record-only opt-out.
    /// </summary>
    private static readonly ImmutableArray<string> AlwaysRendered = ["Hosting__Operator__Enabled"];

    [Fact]
    public void EveryEnabledSwitchInTheTemplate_IsClassifiedHere()
    {
        var template = File.ReadAllText(Path.Combine(ChartRoot(), ConfigMapTemplate));
        var rendered = Regex.Matches(template, @"^\s*(Hosting__\w+?__Enabled):", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var listed = RecordOnlySwitches.Concat(AlwaysRendered).ToHashSet(StringComparer.Ordinal);

        rendered.Except(listed).Should().BeEmpty(
            "every Hosting__*__Enabled key the portal ConfigMap renders must be listed in this guard "
            + "(RecordOnlySwitches or AlwaysRendered) so its record value is proved to reach the pod");
        listed.Except(rendered).Should().BeEmpty(
            "a listed switch with no template line has left the literal half of the ConfigMap — "
            + "unlist it here once the pass-through is meant to carry it");
    }

    [Fact(Timeout = 120000)]
    public void AnUnquotedFalseOnTheRecord_RendersAsTheStringFalse()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var overlay = "config:\n  memex_portal:\n"
            + string.Concat(RecordOnlySwitches.Select(k => $"    {k}: false\n"));

        var data = RenderConfigMapData(overlay);

        foreach (var key in RecordOnlySwitches)
        {
            data.Should().ContainKey(key,
                $"a record setting {key}: false must reach the pod — without a template line the "
                + "ConfigMap omits it and the automation keeps running on its code default (ON)");
            data[key].Value.Should().Be("false", $"{key} must carry the record's value verbatim");
            data[key].Style.Should().Be(ScalarStyle.DoubleQuoted,
                $"{key} must render as a quoted string — ConfigMap data values are strings");
        }
    }

    [Fact(Timeout = 120000)]
    public void AnUnsetSwitch_IsOmittedFromTheConfigMap()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var data = RenderConfigMapData(overlay: null);

        // Non-vacuity: the render produced the ConfigMap at all.
        data.Should().ContainKey("Hosting__Operator__Enabled",
            "the chart's own defaults must render the portal ConfigMap, or the omission check below "
            + "would pass over an empty render");

        foreach (var key in RecordOnlySwitches)
            data.Should().NotContainKey(key,
                $"{key} is not set by the chart's defaults, so it must be absent and the code "
                + "default must apply — rendering it as \"\" or a default would override that");
    }

    [Fact(Timeout = 120000)]
    public void AnExplicitNullOnTheRecord_IsOmittedFromTheConfigMap()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // `Key: ~` / `Key:` is a PRESENT key with a YAML null: hasKey is true, and `toString` turns
        // the value into "<nil>" — which is not empty, so a template testing only `ne … ""` would
        // render the string "<nil>" and the pod's boolean binding would fail on it.
        var overlay = "config:\n  memex_portal:\n"
            + string.Concat(RecordOnlySwitches.Select(k => $"    {k}: null\n"));

        var data = RenderConfigMapData(overlay);

        data.Should().ContainKey("Hosting__Operator__Enabled",
            "the render must produce the portal ConfigMap, or the omission check below would pass "
            + "over an empty render");
        foreach (var key in RecordOnlySwitches)
            data.Should().NotContainKey(key,
                $"{key}: null carries no value, so it must be omitted and the code default must "
                + "apply — rendering \"<nil>\" or \"\" would break the boolean binding");
    }

    /// <summary>Renders the portal ConfigMap with <paramref name="overlay"/> and returns its <c>data</c>; fails the test when helm refuses.</summary>
    internal static Dictionary<string, YamlScalarNode> RenderConfigMapData(string? overlay)
    {
        var (exitCode, stdout, stderr) = RenderConfigMap(overlay);
        exitCode.Should().Be(0, $"helm template must render the chart. stderr: {stderr}");

        var yaml = new YamlStream();
        yaml.Load(new StringReader(stdout));
        yaml.Documents.Should().HaveCount(1, "--show-only renders exactly the portal ConfigMap");
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var data = (YamlMappingNode)root.Children[new YamlScalarNode("data")];
        return data.Children.ToDictionary(
            kv => ((YamlScalarNode)kv.Key).Value!,
            kv => (YamlScalarNode)kv.Value,
            StringComparer.Ordinal);
    }

    /// <summary>Runs <c>helm template --show-only</c> on the portal ConfigMap; returns helm's exit code and both streams, whatever they are.</summary>
    internal static (int ExitCode, string Stdout, string Stderr) RenderConfigMap(string? overlay)
    {
        var chart = ChartRoot();
        var overlayPath = overlay is null
            ? null
            : Path.Combine(Path.GetTempPath(), $"automation-switches-{Guid.NewGuid():N}.yaml");
        try
        {
            if (overlayPath is not null)
                File.WriteAllText(overlayPath, overlay);

            var psi = new ProcessStartInfo("helm")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in new[] { "template", "guard", chart, "--show-only", ConfigMapTemplate })
                psi.ArgumentList.Add(arg);
            if (overlayPath is not null)
            {
                psi.ArgumentList.Add("--values");
                psi.ArgumentList.Add(overlayPath);
            }

            Process process;
            try
            {
                process = Process.Start(psi)!;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException(
                    "`helm` is not on PATH. This guard renders the chart and has no fallback — a "
                    + "skipped render would read as a pass. Install helm (ubuntu runners ship it).", ex);
            }

            using (process)
            {
                // Drain BOTH pipes concurrently (event readers), never ReadToEnd one after the
                // other: a child that fills the undrained pipe blocks writing it while the test
                // blocks reading the other, and xUnit's timeout cannot reach the child.
                var outBuffer = new StringBuilder();
                var errBuffer = new StringBuilder();
                process.OutputDataReceived += (_, e) => { if (e.Data is not null) outBuffer.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data is not null) errBuffer.AppendLine(e.Data); };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                // `helm template --show-only` of one file renders in well under a second; a minute
                // means helm is wedged, and the guard says so instead of hanging.
                if (!process.WaitForExit(60_000))
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
                    Assert.Fail("`helm template` did not finish within 60s — helm is wedged, not slow.");
                }
                // The timed overload waits for the process only; this one waits for the async
                // readers to flush, so stdout is not read truncated.
                process.WaitForExit();
                return (process.ExitCode, outBuffer.ToString(), errBuffer.ToString());
            }
        }
        finally
        {
            if (overlayPath is not null && File.Exists(overlayPath))
                File.Delete(overlayPath);
        }
    }

    private static string ChartRoot() => Path.Combine(SourceScan.FindRepoRoot(), "deploy", "helm");
}
