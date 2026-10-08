using System;
using System.Linq;
using Xunit;
using YamlDotNet.Core;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 A <c>config.memex_portal</c> key the portal ConfigMap does NOT name must still reach the pod —
/// or the render must REFUSE it, naming it. Asserted by RENDERING the chart.
///
/// <para><b>Why this guard exists.</b> The ConfigMap's literal half names every key it knows, and
/// until the pass-through (<c>templates/memex-portal/_portal-config-passthrough.tpl</c>) a key it
/// did not name reached NO container while helm reported success. A Deployment record's
/// <c>extraPortalConfig</c> lands in exactly that section, so a record could state a value no pod
/// ever received: Memex#689 set <c>Hosting__PrBabysitter__Relay: "false"</c> and #693 reverted it
/// as inert; memex.systemorph.com's <c>Hosting__RecordChangeReconcile__Enabled</c> off-switch
/// (Memex#690) went the same way, and control's record carries
/// <c>Ai__Router__CalibrationNode</c>, which no template names either. Those three are the
/// fixture below — real record keys, not invented ones.</para>
///
/// <para><b>Negative control.</b> Every positive assertion here fails against the chart before
/// the pass-through (the keys are simply absent from the rendered data), and the refusal facts
/// fail against it too (helm renders the bad overlay successfully and drops the key).</para>
/// </summary>
public class RecordConfigReachesTheConfigMapGuard
{
    /// <summary>Record keys no template names — measured on the Memex records, 2026-10-08.</summary>
    private static readonly (string Key, string Yaml, string Expected)[] UnlistedRecordKeys =
    [
        ("Hosting__PrBabysitter__Relay", "false", "false"),
        ("Hosting__RecordChangeReconcile__Enabled", "\"false\"", "false"),
        ("Ai__Router__CalibrationNode", "Hosting/BugFix", "Hosting/BugFix"),
    ];

    [Fact(Timeout = 120000)]
    public void AKeyNoTemplateNames_ReachesTheConfigMapVerbatim()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var overlay = "config:\n  memex_portal:\n"
            + string.Concat(UnlistedRecordKeys.Select(k => $"    {k.Key}: {k.Yaml}\n"))
            + "    Pass__Through__Count: 3\n"
            + "    Pass__Through__Big: 1000000\n";

        var data = AutomationSwitchesReachTheConfigMapGuard.RenderConfigMapData(overlay);

        foreach (var (key, _, expected) in UnlistedRecordKeys)
        {
            data.Should().ContainKey(key,
                $"a record that sets {key} must reach the pod even though no template line names it — "
                + "otherwise the record states a value nobody receives (Memex#689 → #693)");
            data[key].Value.Should().Be(expected, $"{key} must carry the record's value verbatim");
            data[key].Style.Should().Be(ScalarStyle.DoubleQuoted, $"{key} must render as a quoted string");
        }
        data["Pass__Through__Count"].Value.Should().Be("3", "a whole number renders as written");
        data["Pass__Through__Big"].Value.Should().Be("1000000", "a large whole number must not render as 1e+06");
    }

    [Fact(Timeout = 120000)]
    public void AListedKey_KeepsItsTemplateLine_AndIsNeverDuplicated()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        // Email__Enabled is a literal line with `default "false"`; Hosting__PrBabysitter__Enabled is
        // a "rendered only when set" switch. Both must come from their own line, once.
        var overlay = "config:\n  memex_portal:\n"
            + "    Email__Enabled: \"true\"\n"
            + "    Hosting__PrBabysitter__Enabled: false\n"
            + "    Hosting__PrBabysitter__Relay: false\n";

        var (exitCode, stdout, stderr) = AutomationSwitchesReachTheConfigMapGuard.RenderConfigMap(overlay);
        exitCode.Should().Be(0, $"helm template must render. stderr: {stderr}");

        foreach (var key in new[] { "Email__Enabled", "Hosting__PrBabysitter__Enabled", "Hosting__PrBabysitter__Relay" })
            stdout.Split('\n').Count(l => l.TrimStart().StartsWith(key + ":", StringComparison.Ordinal))
                .Should().Be(1, $"{key} must appear exactly once in the ConfigMap data — the pass-through skips every key the literal half rendered");

        var data = AutomationSwitchesReachTheConfigMapGuard.RenderConfigMapData(overlay);
        data["Email__Enabled"].Value.Should().Be("true");
        data["Hosting__PrBabysitter__Enabled"].Value.Should().Be("false");
    }

    [Fact(Timeout = 120000)]
    public void ABlankOrNullUnlistedKey_RendersNothing()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var data = AutomationSwitchesReachTheConfigMapGuard.RenderConfigMapData(
            "config:\n  memex_portal:\n    Pass__Blank: \"\"\n    Pass__Null: null\n    Pass__Spaces: \"  \"\n    Pass__Set: x\n");

        data.Should().ContainKey("Pass__Set", "non-vacuity: the pass-through rendered in this run");
        data.Should().NotContainKey("Pass__Blank", "a blank value would override the code default with \"\"");
        data.Should().NotContainKey("Pass__Null", "a YAML null would otherwise render \"<nil>\"");
        data.Should().NotContainKey("Pass__Spaces", "whitespace is blank");
    }

    [Theory(Timeout = 120000)]
    [InlineData("email__enabled: \"true\"", "differs only by case")]
    [InlineData("Nested__Map: {a: 1}", "flatten it")]
    [InlineData("Nested__List: [1, 2]", "flatten it")]
    [InlineData("\"Weird Key\": x", "not a valid ConfigMap key")]
    [InlineData("Modules__Required__20: Extra.dll", "boot-module slot")]
    public void AKeyThePassThroughCannotDeliver_IsRefusedByName(string line, string because)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (exitCode, _, stderr) = AutomationSwitchesReachTheConfigMapGuard.RenderConfigMap(
            $"config:\n  memex_portal:\n    {line}\n");

        exitCode.Should().NotBe(0,
            $"`{line}` cannot reach the pod as written, so the render must fail LOUDLY rather than drop it");
        stderr.Should().Contain(because, "the refusal must say why, naming the key");
    }
}
