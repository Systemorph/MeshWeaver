using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>Liveness and readiness must not share a path</b> (MeshWeaver#3330).
///
/// <para><b>The defect this exists to make impossible.</b> The chart pointed BOTH post-startup
/// probes at <c>/alive</c>. That was safe only while <c>/alive</c> stayed the trivial process-up
/// check it shipped as — nothing carried the <c>live</c> tag its predicate filters on, so an empty
/// check set answered 200 for any process that could accept a socket.
/// <c>Systemorph/MeshWeaver.Plugins#1234</c> then registered <c>ProcessProgressHealthCheck</c> on
/// that tag (merged 2026-09-03), which was correct and closed a real blindness — and READINESS
/// silently inherited it, because two probes cannot be given different SEMANTICS while they share
/// a PATH.</para>
///
/// <para>What that bought was an amplifier built out of the containment. Readiness trips at
/// <c>10s × 3 = 30s</c>, liveness at <c>15s × 6 = 90s</c>, so a GC-bound replica left the Service a
/// full minute before anything restarted it, and for that minute its traffic went to siblings
/// converging on the SAME ceiling (measured 2026-09-04 in ns <c>memex</c>: two 28 h replicas at
/// 9936Mi and 9409Mi — ratio 1.06). One sick replica became a cascade. Confirmed still live on
/// 2026-09-05: both probes <c>/alive</c>, readiness 30 s, liveness 90 s.</para>
///
/// <para>🚨 <b>Why a comment was not enough.</b> The chart already carried this hazard, in these
/// words, on the readiness probe: <i>"liveness and readiness cannot be given different SEMANTICS
/// while they share a PATH"</i>. It was written down, it was correct, and it did not stop the
/// change — because the change was in a DIFFERENT REPOSITORY, where nobody was reading this chart.
/// A rule stated in a comment is not a gate; this is the gate, and it lives on the side of the
/// contract that cannot move away from the chart.</para>
///
/// <para><b>What it asserts.</b> It resolves each chart probe path through the ONE place the paths
/// and tags are declared (<c>ProbeEndpoints.cs</c>) to the health-check TAG that path filters on,
/// and requires readiness and liveness to end up on different tags. So it fails not only when the
/// paths are equal, but also when two different paths are wired to the same predicate — the same
/// defect wearing a disguise. (What the endpoints ANSWER, over real HTTP, is
/// <c>ProbeSeparationTest</c> in Memex.Portal.Shared.Test.)</para>
/// </summary>
public class ProbeSemanticsGuard
{
    private const string Deployment = "deploy/helm/templates/memex-portal/deployment.yaml";
    private const string ServiceDefaults = "memex/aspire/Memex.Portal.ServiceDefaults/ServiceDefaults.cs";
    private const string ProbeEndpoints = "memex/aspire/Memex.Portal.ServiceDefaults/ProbeEndpoints.cs";

    /// <summary>
    /// 🚨 The invariant itself. Readiness answers "give my traffic to my siblings" and liveness
    /// answers "restart me" — opposite remedies, and on a fleet whose replicas converge on one
    /// memory ceiling the first one is a cascade. They cannot be the same question.
    /// </summary>
    [Fact]
    public void ReadinessAndLiveness_DoNotShareAProbePath()
    {
        var probes = ChartProbePaths();

        Assert.NotEqual(probes["livenessProbe"], probes["readinessProbe"]);
    }

    /// <summary>
    /// The message the assertion above cannot carry, plus the two neighbouring shapes: readiness
    /// must not be on the STARTUP probe's heavy path either (the 2026-07-21 death spiral), and
    /// every probe must actually name a path.
    /// </summary>
    [Fact]
    public void EachProbe_AsksItsOwnQuestion()
    {
        var probes = ChartProbePaths();

        Assert.True(probes["readinessProbe"] != probes["livenessProbe"],
            $"{Deployment} points readinessProbe and livenessProbe at the same path "
            + $"('{probes["readinessProbe"]}'). They cannot then be given different semantics: the "
            + "moment that path answers a progress-aware verdict, a GC-bound replica is EVICTED at "
            + "10s×3=30s and only RESTARTED at 15s×6=90s, and for the minute in between its traffic "
            + "lands on siblings converging on the same memory ceiling (ratio 1.06, ns memex, "
            + "2026-09-04). That is the 2026-07-21 death spiral rebuilt out of the #2194 item-4 fix "
            + "— see #3330. Readiness gets its own path and its own tag.");

        Assert.True(probes["readinessProbe"] != probes["startupProbe"],
            $"{Deployment} points readinessProbe at the STARTUP probe's path "
            + $"('{probes["startupProbe"]}'), which runs every registered check — the DB and the "
            + "mesh included. Under load a heavy readiness check times out, the pod is yanked from "
            + "the Service, and the survivors inherit its traffic: the 2026-07-21 death spiral in "
            + "its original form. The startup probe already holds readiness on the heavy path until "
            + "the mesh is up; after that, readiness must be cheap.");
    }

    /// <summary>
    /// 🚨 <b>Two different paths wired to the SAME predicate is the same defect in disguise.</b>
    /// The paths are only the visible half — what makes readiness progress-aware is the TAG its
    /// endpoint filters on. So resolve chart path → <c>ProbeEndpoints</c> constant → tag, and
    /// require the two probes to land on different tags.
    ///
    /// <para>This is also the chart↔code agreement: a path the chart probes and the code does not
    /// map is a 404, and a 404 readiness probe means the pod NEVER reports Ready — the rollout
    /// stalls with the old replica keeping the traffic, and nothing in the log names the cause.</para>
    /// </summary>
    [Fact]
    public void EachPostStartupProbe_ResolvesToItsOwnHealthCheckTag()
    {
        var probes = ChartProbePaths();
        var constants = DeclaredConstants();
        var wiring = EndpointTagWiring(constants);

        foreach (var probe in new[] { "readinessProbe", "livenessProbe" })
        {
            Assert.True(wiring.ContainsKey(probes[probe]),
                $"{Deployment} probes '{probes[probe]}' ({probe}), but {ServiceDefaults} maps no "
                + $"health-check endpoint on that path. The kubelet reads the resulting 404 as a "
                + $"FAILING probe, so a readiness path nothing maps means the pod never reports "
                + $"Ready and the roll stalls — silently, because the portal is serving fine. "
                + $"Mapped: {string.Join(", ", wiring.Keys.OrderBy(k => k, StringComparer.Ordinal))}.");
        }

        Assert.True(wiring[probes["readinessProbe"]] != wiring[probes["livenessProbe"]],
            $"{Deployment}'s readinessProbe ('{probes["readinessProbe"]}') and livenessProbe "
            + $"('{probes["livenessProbe"]}') are different paths but both filter on the health-check "
            + $"tag '{wiring[probes["readinessProbe"]]}' in {ServiceDefaults} — so they still ask the "
            + "SAME question, and #3330 is back with two URLs instead of one. Readiness must filter "
            + "on its own tag, and that tag must be an ALLOW-list: a deny-list (everything not "
            + "tagged live) sweeps the untagged DB and mesh checks into readiness and rebuilds the "
            + "2026-07-21 spiral instead.");
    }

    /// <summary>
    /// 🚨 <b>An endpoint whose predicate matches nothing is vacuously healthy.</b> That is exactly
    /// how <c>/alive</c> was blind for months — mapped, filtered on <c>live</c>, and answering 200
    /// for a process pegged in GC because NOTHING carried the tag (#2194). A readiness endpoint
    /// with an empty check set would be that same defect, newly built, so the trivial process-up
    /// check must carry the readiness tag.
    /// </summary>
    [Fact]
    public void TheReadinessTag_IsCarriedByAtLeastOneRegisteredCheck()
    {
        var probes = ChartProbePaths();
        var constants = DeclaredConstants();
        var tag = EndpointTagWiring(constants)[probes["readinessProbe"]];

        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), ServiceDefaults));
        var registrations = Regex.Matches(source, @"AddCheck[^;]*?;", RegexOptions.Singleline)
            .Select(m => m.Value)
            .Where(r => constants.Any(c => c.Value == tag
                                           && r.Contains($"ProbeEndpoints.{c.Key}", StringComparison.Ordinal)))
            .ToList();

        Assert.True(registrations.Count > 0,
            $"{ServiceDefaults} registers no health check tagged '{tag}', which is the tag the "
            + $"chart's readiness path ('{probes["readinessProbe"]}') filters on. An empty predicate "
            + "set is VACUOUSLY healthy: the endpoint would answer 200 for any process that can "
            + "still accept a socket, which is precisely how /alive stayed blind through the "
            + "2026-08-25 incident (#2194). Tag the trivial process-up check.");
    }

    // ---------------------------------------------------------------------------------------
    // Parsing. Comment lines are stripped first: every string these probes look for also appears
    // in the prose explaining it, so without stripping the guard could be satisfied by the
    // explanation while the probe it describes was gone — a check that cannot fail is not a check.
    // ---------------------------------------------------------------------------------------

    /// <summary>The <c>httpGet</c> path each probe in the portal deployment names.</summary>
    private static IReadOnlyDictionary<string, string> ChartProbePaths()
    {
        var yaml = ExecutableLinesOf(File.ReadAllText(Path.Combine(FindRepoRoot(), Deployment)));
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var probe in new[] { "startupProbe", "readinessProbe", "livenessProbe" })
        {
            // Both YAML shapes: the inline `httpGet: { path: X, port: N }` this chart uses, and the
            // block form. SetupProbeEndpointsTest was once blind to the inline form and passed
            // anyway off a hard-coded fallback — so match both, and assert the match.
            var match = Regex.Match(
                yaml,
                $@"^\s*{probe}:\s*$(?<body>(?:\n(?!\s*\w+Probe:).*)*)",
                RegexOptions.Multiline);
            Assert.True(match.Success,
                $"{Deployment} declares no {probe}. All three are load-bearing: the startup probe "
                + "is what suspends the other two through a slow boot, and without a liveness probe "
                + "a wedged pod is never restarted.");

            var body = match.Groups["body"].Value;
            var inline = Regex.Match(body, @"httpGet:\s*\{[^}]*?\bpath:\s*(?<p>[^,}\s]+)");
            var block = Regex.Match(body, @"httpGet:\s*(?:\r?\n\s+(?!path:)\w+:.*)*\r?\n\s+path:\s*(?<p>\S+)");
            var hit = inline.Success ? inline : block;
            Assert.True(hit.Success,
                $"{Deployment}'s {probe} names no httpGet path this guard can read. If the probe "
                + "shape changed, update the parse DELIBERATELY — a guard that quietly stops "
                + "matching its subject passes having checked nothing.");

            paths[probe] = hit.Groups["p"].Value.Trim();
        }

        return paths;
    }

    /// <summary>The <c>ProbeEndpoints</c> string constants, by name — the one place they are declared.</summary>
    private static IReadOnlyDictionary<string, string> DeclaredConstants()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), ProbeEndpoints));
        var constants = Regex.Matches(source, @"public const string (?<name>\w+)\s*=\s*""(?<value>[^""]+)"";")
            .ToDictionary(m => m.Groups["name"].Value, m => m.Groups["value"].Value, StringComparer.Ordinal);

        Assert.True(constants.Count >= 4,
            $"{ProbeEndpoints} declares only {constants.Count} constant(s) — the parse is not "
            + "reading the file this guard resolves every path and tag through. Fix the parse; do "
            + "not let it degrade to matching nothing.");
        return constants;
    }

    /// <summary>
    /// Route → health-check tag, read off the <c>MapHealthChecks</c> calls: which predicate tag
    /// each probe path actually filters on.
    /// </summary>
    private static IReadOnlyDictionary<string, string> EndpointTagWiring(
        IReadOnlyDictionary<string, string> constants)
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), ServiceDefaults));
        var wiring = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match m in Regex.Matches(
                     source,
                     @"MapHealthChecks\(\s*ProbeEndpoints\.(?<route>\w+)\s*,[^;]*?Tags\.Contains\(\s*ProbeEndpoints\.(?<tag>\w+)\s*\)",
                     RegexOptions.Singleline))
        {
            Assert.True(constants.TryGetValue(m.Groups["route"].Value, out var route),
                $"{ServiceDefaults} maps ProbeEndpoints.{m.Groups["route"].Value}, which "
                + $"{ProbeEndpoints} does not declare.");
            Assert.True(constants.TryGetValue(m.Groups["tag"].Value, out var tag),
                $"{ServiceDefaults} filters on ProbeEndpoints.{m.Groups["tag"].Value}, which "
                + $"{ProbeEndpoints} does not declare.");
            wiring[route!] = tag!;
        }

        Assert.True(wiring.Count >= 2,
            $"{ServiceDefaults} declares only {wiring.Count} tag-filtered health endpoint(s). "
            + "Readiness and liveness each need one, filtered on their OWN tag — if this parse "
            + "found fewer, either the wiring moved or the guard stopped seeing it, and both are "
            + "'checked nothing'.");
        return wiring;
    }

    private static string ExecutableLinesOf(string yaml) =>
        string.Join("\n", yaml.Split('\n').Where(line => !line.TrimStart().StartsWith('#')));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
