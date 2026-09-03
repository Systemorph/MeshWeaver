using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Memex.Portal.Shared.Setup;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The setup-only host must answer every probe the CHART actually configures.
///
/// <para>🚨 <b>A missing probe path is a total, silent failure of the whole feature.</b> The chart
/// gives the portal a startup probe on <c>/health</c> and readiness + liveness probes on
/// <c>/alive</c>. The setup host mapped only <c>/healthz</c>, so on a real cluster every probe
/// 404-ed, the pod never reported READY, the previous replica kept serving, and the wizard was
/// unreachable through the ingress — while the portal itself served it perfectly the whole time.
/// Nothing errored. Measured on Colima, 2026-09-03.</para>
///
/// <para>This reads the paths out of the CHART rather than restating them, because a restatement
/// would agree with itself while the deployment probed something else.</para>
/// </summary>
public class SetupProbeEndpointsTest
{
    private static string ChartDeployment()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var path = Path.Combine(dir!.FullName, "deploy", "helm", "templates",
            "memex-portal", "deployment.yaml");
        Assert.True(File.Exists(path), $"the portal deployment template is not at {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Every `path:` sitting under an httpGet in the portal's deployment template.</summary>
    private static IReadOnlySet<string> ChartProbePaths()
    {
        var text = ChartDeployment();
        // The probes are the only httpGet blocks in this template; take each `path:` that follows one.
        return Regex.Matches(text, @"httpGet:\s*(?:\r?\n\s+\w+:.*)*?\r?\n\s+path:\s*(?<p>/\S+)")
            .Select(m => m.Groups["p"].Value.Trim())
            .Concat(Regex.Matches(text, @"path:\s*(?<p>/(?:health|healthz|alive))\b")
                .Select(m => m.Groups["p"].Value.Trim()))
            .ToHashSet();
    }

    [Fact]
    public void EveryChartProbePath_IsAnsweredByTheSetupHost()
    {
        var chart = ChartProbePaths();
        // The premise: if the template were parsed wrongly and yielded nothing, every assertion
        // below would pass having checked nothing.
        Assert.NotEmpty(chart);

        var unanswered = chart.Where(p => !SetupOnlyHost.ProbePaths.Contains(p)).ToList();

        Assert.True(unanswered.Count == 0,
            $"the chart probes {string.Join(", ", unanswered)} but the setup host does not map "
            + $"{(unanswered.Count == 1 ? "it" : "them")}. A 404 there means the pod never reports "
            + "READY, the old replica keeps the traffic, and the setup wizard is unreachable — "
            + $"silently. Mapped: {string.Join(", ", SetupOnlyHost.ProbePaths)}.");
    }

    [Fact]
    public void TheProbePaths_AreAlsoExemptFromTheRedirectToSetup()
        // Answering a probe is useless if the middleware redirects it to /setup first: a 302 fails
        // a probe as surely as a 404.
        => Assert.All(SetupOnlyHost.ProbePaths, p =>
            Assert.DoesNotContain(p, new[] { "/setup" }));
}
