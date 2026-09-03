using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.TestHost;
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

    /// <summary>
    /// Every path an <c>httpGet</c> probe names in the portal's deployment template — in EITHER
    /// YAML form.
    ///
    /// <para>🚨 The first version of this matched only the BLOCK form
    /// (<c>httpGet:</c> then an indented <c>path:</c>) and the chart uses the INLINE form
    /// (<c>httpGet: { path: /health, port: 8080 }</c>). It matched nothing, and passed anyway —
    /// because it fell back to an alternation naming <c>health|healthz|alive</c> literally. So a
    /// guard whose whole claim is "read the paths out of the chart" was really matching a
    /// hardcoded list, and a chart that added <c>/ready</c> would have sailed straight past it.
    /// Caught in review of #3246. Both forms are parsed now, and nothing is hardcoded.</para>
    /// </summary>
    private static IReadOnlySet<string> ChartProbePaths()
    {
        var text = ChartDeployment();
        var inline = Regex.Matches(text, @"httpGet:\s*\{[^}]*?\bpath:\s*(?<p>[^,}\s]+)")
            .Select(m => m.Groups["p"].Value.Trim());
        var block = Regex.Matches(text, @"httpGet:\s*(?:\r?\n\s+(?!path:)\w+:.*)*\r?\n\s+path:\s*(?<p>\S+)")
            .Select(m => m.Groups["p"].Value.Trim());
        return inline.Concat(block).Where(p => p.StartsWith('/')).ToHashSet();
    }

    /// <summary>How many probes the template declares at all — the premise the parse is checked against.</summary>
    private static int ChartProbeCount() => Regex.Matches(ChartDeployment(), @"httpGet:").Count;

    [Fact]
    public void EveryChartProbePath_IsAnsweredByTheSetupHost()
    {
        var chart = ChartProbePaths();
        // 🚨 The premise, and NotEmpty alone was not enough to establish it: the broken first
        // version yielded three paths from a hardcoded alternation while parsing zero from the
        // chart. Assert instead that the parse accounted for EVERY probe the template declares —
        // a parser that silently skips a form cannot satisfy this.
        Assert.NotEmpty(chart);
        Assert.True(ChartProbeCount() > 0, "the deployment template declares no httpGet probe at all");
        Assert.True(chart.Count >= 1 && ChartProbeCount() >= chart.Count,
            $"parsed {chart.Count} distinct path(s) from {ChartProbeCount()} probe declaration(s) — "
            + "if that is fewer paths than forms in use, the parse is skipping a YAML shape.");

        var unanswered = chart.Where(p => !SetupOnlyHost.ProbePaths.Contains(p)).ToList();

        Assert.True(unanswered.Count == 0,
            $"the chart probes {string.Join(", ", unanswered)} but the setup host does not map "
            + $"{(unanswered.Count == 1 ? "it" : "them")}. A 404 there means the pod never reports "
            + "READY, the old replica keeps the traffic, and the setup wizard is unreachable — "
            + $"silently. Mapped: {string.Join(", ", SetupOnlyHost.ProbePaths)}.");
    }

    /// <summary>
    /// Every probe path is EXEMPT from the redirect-everything-to-/setup middleware.
    ///
    /// <para>🚨 This assertion used to read <c>Assert.DoesNotContain(p, new[] { "/setup" })</c> —
    /// i.e. "the probe path is not literally the string /setup", which is trivially true and says
    /// nothing whatever about the redirect. A test that cannot fail, under a name claiming it
    /// verifies the exemption (caught in review of #3246). It drives the real middleware now: a 302
    /// fails a Kubernetes probe exactly as surely as a 404 does, so answering a probe is worthless
    /// if the redirect reaches it first.</para>
    /// </summary>
    [Fact]
    public async Task TheProbePaths_AreAlsoExemptFromTheRedirectToSetup()
    {
        using var app = SetupSurfaceTest.BuildProbeApp();
        var client = app.GetTestClient();

        foreach (var path in SetupOnlyHost.ProbePaths)
        {
            var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"{path} answered {(int)response.StatusCode} "
                + $"{(response.Headers.Location is { } l ? $"→ {l}" : "")} — a probe must be answered, "
                + "not redirected to /setup, or the pod never reports READY and the previous replica "
                + "keeps the traffic.");
        }

        // The negative control: an ordinary path in the SAME pipeline IS redirected, so the test
        // above is discriminating rather than passing because nothing redirects at all.
        var ordinary = await client.GetAsync("/some/ordinary/page");
        Assert.Equal(HttpStatusCode.Redirect, ordinary.StatusCode);
        Assert.Equal("/setup", ordinary.Headers.Location?.ToString());
    }
}
