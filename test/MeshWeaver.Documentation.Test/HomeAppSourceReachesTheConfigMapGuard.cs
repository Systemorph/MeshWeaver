using System;
using System.Linq;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// <c>Home__AppSource</c> (MeshWeaver#6448) reaches the portal ConfigMap only when a deployment states a
/// value: absent, YAML null, empty and whitespace-only are OMITTED so the image default (the app
/// directory) applies, and an explicit <c>Records</c> is rendered exactly once, quoted. A null that
/// rendered as <c>"&lt;nil&gt;"</c> would reach the runtime as an invalid value and log an error on every
/// home render.
/// </summary>
public class HomeAppSourceReachesTheConfigMapGuard
{
    private const string Key = "Home__AppSource";

    [Theory(Timeout = 120000)]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void AnAbsentOrBlankValue_IsOmitted(string? yaml)
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var overlay = yaml is null ? null : $"config:\n  memex_portal:\n    {Key}: {yaml}\n";

        var data = AutomationSwitchesReachTheConfigMapGuard.RenderConfigMapData(overlay);

        data.Should().ContainKey("Hosting__Operator__Enabled",
            "the render must produce the portal ConfigMap, or the omission check would pass over an empty render");
        data.Should().NotContainKey(Key,
            $"{Key}: {yaml ?? "(absent)"} states no value, so the image default must apply");
    }

    [Fact(Timeout = 120000)]
    public void AnExplicitRecords_IsRenderedExactlyOnce()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var overlay = $"config:\n  memex_portal:\n    {Key}: \"Records\"\n";

        var (exitCode, stdout, stderr) = AutomationSwitchesReachTheConfigMapGuard.RenderConfigMap(overlay);
        exitCode.Should().Be(0, $"helm template must render. stderr: {stderr}");
        stdout.Split('\n').Count(l => l.TrimStart().StartsWith(Key + ":", StringComparison.Ordinal))
            .Should().Be(1, $"{Key} must appear exactly once: its literal line, never also the pass-through");

        var data = AutomationSwitchesReachTheConfigMapGuard.RenderConfigMapData(overlay);
        data[Key].Value.Should().Be("Records");
    }
}
