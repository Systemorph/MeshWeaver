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
    ///
    /// <para>🚨 <b>And a path may be a VALUE</b> (<c>probes.startup.path</c>, MeshWeaver#4588). The
    /// same drop was waiting one layer down: a <c>{{ … }}</c> expression captures as <c>{{</c>,
    /// which does not start with <c>/</c>, so the old filter would have silently removed the
    /// STARTUP probe from this guard's subject — leaving an overlay free to point it at a path the
    /// setup host answers with 404, which parks every pod in startup forever. Templated paths are
    /// resolved against <c>values.yaml</c>, and anything this parse cannot turn into a path FAILS
    /// rather than being filtered away: a silent drop is how a guard stops covering something.</para>
    /// </summary>
    private static IReadOnlySet<string> ChartProbePaths()
    {
        var text = ChartDeployment();
        var inline = Regex.Matches(text, @"httpGet:\s*\{[^}]*?\bpath:\s*(?<p>[^,}\s]+)")
            .Select(m => Raw(text, m));
        var block = Regex.Matches(text, @"httpGet:\s*(?:\r?\n\s+(?!path:)\w+:.*)*\r?\n\s+path:\s*(?<p>\S+)")
            .Select(m => Raw(text, m));
        return inline.Concat(block).Select(Resolve).ToHashSet();

        // The raw token, plus enough of the line after it to carry a whole {{ … }} expression:
        // the capture stops at the first whitespace, and a template expression has several.
        static string Raw(string text, Match m)
        {
            var token = m.Groups["p"].Value.Trim();
            if (!token.StartsWith("{{", StringComparison.Ordinal)) return token;
            var line = text[m.Groups["p"].Index..];
            var end = line.IndexOf("}}", StringComparison.Ordinal);
            Assert.True(end > 0, $"an unterminated template expression in a probe path: {token}");
            return line[..(end + 2)];
        }
    }

    /// <summary>
    /// A literal path, or the value a <c>{{ .Values.x.y.z | default "/p" }}</c> expression ships.
    /// Asserted at every step: a path this cannot resolve is a probe nothing is checking, which is
    /// strictly worse than a parse that fails loudly.
    /// </summary>
    private static string Resolve(string raw)
    {
        if (raw.StartsWith('/')) return raw;

        Assert.True(raw.StartsWith("{{", StringComparison.Ordinal),
            $"the chart names a probe path this guard cannot read: '{raw}'. Teach the parse the new "
            + "shape DELIBERATELY — a path silently filtered out is a probe nothing covers.");

        var key = Regex.Match(raw, @"\.Values\.(?<key>[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)*)");
        Assert.True(key.Success,
            $"the probe path expression '{raw}' names no .Values key this guard can resolve.");

        var declared = ValuesLeaf(key.Groups["key"].Value);
        Assert.True(declared is not null,
            $"the chart reads .Values.{key.Groups["key"].Value} for a probe path, but "
            + "deploy/helm/values.yaml declares no such key — the template's own `default` would "
            + "ship, so the path is stated in one place and defaulted in another.");

        var fallback = Regex.Match(raw, @"default\s+""(?<p>[^""]+)""");
        if (fallback.Success)
            Assert.True(fallback.Groups["p"].Value == declared,
                $"the probe path defaults to '{fallback.Groups["p"].Value}' while values.yaml "
                + $"declares '{declared}' — the probe would read a different path depending on "
                + "whether an overlay carries the key.");

        return declared!;
    }

    /// <summary>The scalar at a dotted key in <c>values.yaml</c>, walked by indentation.</summary>
    private static string? ValuesLeaf(string dottedKey)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var segments = dottedKey.Split('.');
        var depth = 0;
        var indent = -1;

        foreach (var line in File.ReadAllLines(
                     Path.Combine(dir!.FullName, "deploy", "helm", "values.yaml")))
        {
            if (line.TrimStart().StartsWith('#') || line.Trim().Length == 0) continue;
            var thisIndent = line.Length - line.TrimStart().Length;
            if (thisIndent <= indent) continue;

            var m = Regex.Match(line, @"^\s*(?<k>[A-Za-z0-9_]+):\s*(?<v>\S*)\s*$");
            if (!m.Success || m.Groups["k"].Value != segments[depth]) continue;

            if (depth == segments.Length - 1) return m.Groups["v"].Value.Trim('"', '\'');

            depth++;
            indent = thisIndent;
        }

        return null;
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
