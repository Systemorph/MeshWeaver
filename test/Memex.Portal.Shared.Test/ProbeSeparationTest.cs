using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Memex.Portal.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A failing LIVENESS check must not evict the pod from the Service</b> (MeshWeaver#3330).
///
/// <para><b>What went wrong.</b> The chart pointed both post-startup probes at <c>/alive</c>, which
/// was harmless only while <c>/alive</c> was the trivial process-up check it shipped as: nothing
/// carried the <c>live</c> tag its predicate filters on, so an empty check set answered 200 for any
/// process that could still accept a socket. <c>MeshWeaver.Plugins#1234</c> then registered
/// <c>ProcessProgressHealthCheck</c> on that tag — correct, and the fix to a real blindness — and
/// READINESS inherited it silently, because two probes cannot be given different semantics while
/// they share a path.</para>
///
/// <para>The arithmetic then does the damage: readiness trips at <c>10s × 3 = 30s</c> and liveness
/// at <c>15s × 6 = 90s</c>, so a GC-bound replica leaves the Service a full minute before anything
/// restarts it, and for that minute its traffic lands on siblings converging on the SAME memory
/// ceiling (measured 2026-09-04 in ns <c>memex</c>: two 28 h replicas at 9936Mi and 9409Mi, ratio
/// 1.06). One sick replica becomes a cascade — the 2026-07-21 death spiral, rebuilt out of the
/// containment that was supposed to prevent it.</para>
///
/// <para><b>Why this test and not only a text guard.</b> <c>ProbeSemanticsGuard</c> reads the chart
/// and the wiring and proves they are declared apart. This drives the two paths the CHART actually
/// names over a REAL HTTP pipeline through the REAL
/// <see cref="ServiceDefaults.MapDefaultEndpoints"/> composition, with a liveness check that is
/// genuinely failing — the running shape of the incident. It reads the paths out of the chart
/// rather than restating them, because a restatement would agree with itself while the deployment
/// probed something else.</para>
/// </summary>
public class ProbeSeparationTest
{
    /// <summary>A check that fails the way a GC-bound process fails: reporting, not throwing.</summary>
    private const string GcBound =
        "GC-bound: pause share 71% over the last 120 s — the process is not making progress.";

    /// <summary>
    /// 🚨 The invariant. A process failing its LIVENESS check must still answer READY: the remedy
    /// for "not making progress" is a restart, never an eviction onto siblings at the same point on
    /// the same curve.
    /// </summary>
    [Fact]
    public async Task AFailingLivenessCheck_DoesNotTakeThePodOutOfRotation()
    {
        var probes = ChartProbePaths();

        var (liveness, _) = await ProbeAsync(probes.Liveness, ProbeEndpoints.LiveTag);
        var (readiness, _) = await ProbeAsync(probes.Readiness, ProbeEndpoints.LiveTag);

        Assert.True(liveness.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"the chart's livenessProbe path ('{probes.Liveness}') answered {(int)liveness.StatusCode} "
            + "while a check tagged 'live' was Unhealthy. Liveness is the probe that must see a "
            + "GC-bound process — this is #2194 item 4 and it must keep working.");

        Assert.True(readiness.StatusCode == HttpStatusCode.OK,
            $"the chart's readinessProbe path ('{probes.Readiness}') answered "
            + $"{(int)readiness.StatusCode} because a LIVENESS check was failing"
            + (probes.Readiness == probes.Liveness
                ? " — and it answered it on the very same path the livenessProbe uses, so the two "
                  + "probes cannot be given different semantics at all. "
                : " — so readiness is wired to the liveness predicate even though the paths differ. ")
            + "That evicts a GC-bound replica from the Service at 10s×3=30s and only restarts it at "
            + "15s×6=90s; for the minute in between its traffic goes to siblings converging on the "
            + "same memory ceiling (ratio 1.06, ns memex, 2026-09-04), turning one sick replica into "
            + "a cascade. #3330: readiness needs its own path AND its own tag.");
    }

    /// <summary>
    /// 🚨 Non-vacuity, and it is the whole reason the readiness endpoint runs a check at all: a
    /// <c>MapHealthChecks</c> whose predicate matches NOTHING answers 200 forever. That is exactly
    /// how <c>/alive</c> stayed blind through the 2026-08-25 incident (#2194) — mapped, filtered,
    /// and matching an empty set. Without this case the assertion above would be satisfied by a
    /// readiness endpoint that can never fail, which is not a probe.
    /// </summary>
    [Fact]
    public async Task TheReadinessPath_CanStillFail_WhenItsOwnCheckFails()
    {
        var probes = ChartProbePaths();

        var (readiness, body) = await ProbeAsync(probes.Readiness, ProbeEndpoints.ReadyTag);

        Assert.True(readiness.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"the chart's readinessProbe path ('{probes.Readiness}') answered "
            + $"{(int)readiness.StatusCode} ('{body}') with a check tagged "
            + $"'{ProbeEndpoints.ReadyTag}' reporting Unhealthy. Its predicate is matching nothing, "
            + "so it is vacuously healthy — a probe that cannot fail is not a probe, and that is "
            + "the #2194 blindness rebuilt on a new path.");
    }

    /// <summary>The healthy baseline: with nothing failing, both post-startup probes answer 200.</summary>
    [Fact]
    public async Task BothPostStartupProbes_AnswerWhenNothingIsWrong()
    {
        var probes = ChartProbePaths();

        var (liveness, _) = await ProbeAsync(probes.Liveness, failingTag: null);
        var (readiness, _) = await ProbeAsync(probes.Readiness, failingTag: null);

        Assert.True(liveness.StatusCode == HttpStatusCode.OK,
            $"the chart's livenessProbe path ('{probes.Liveness}') answered "
            + $"{(int)liveness.StatusCode} on a healthy process — the code does not map it. The "
            + "kubelet reads a 404 as a failing probe, so this restarts every pod in a loop.");
        Assert.True(readiness.StatusCode == HttpStatusCode.OK,
            $"the chart's readinessProbe path ('{probes.Readiness}') answered "
            + $"{(int)readiness.StatusCode} on a healthy process — the code does not map it. The pod "
            + "then never reports Ready, the old replica keeps the traffic, and the roll stalls with "
            + "nothing in the log naming the cause.");
    }

    /// <summary>
    /// The pipeline under test: the REAL <see cref="ServiceDefaults.MapDefaultEndpoints"/>
    /// composition over a TestServer, with one extra check reporting Unhealthy under
    /// <paramref name="failingTag"/> (null = nothing failing).
    /// </summary>
    private static async Task<(HttpResponseMessage Response, string Body)> ProbeAsync(
        string route, string? failingTag)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddDefaultHealthChecks();

        if (failingTag is not null)
            builder.Services.AddHealthChecks()
                .AddCheck("progress", () => HealthCheckResult.Unhealthy(GcBound), [failingTag]);

        var app = builder.Build();
        app.MapDefaultEndpoints();

        await using (app)
        {
            await app.StartAsync(TestContext.Current.CancellationToken);
            using var client = app.GetTestClient();
            var response = await client.GetAsync(route, TestContext.Current.CancellationToken);
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            return (response, body);
        }
    }

    /// <summary>
    /// The two post-startup probe paths, read out of the CHART. Restating them here would let this
    /// test agree with itself while the deployment probed something else.
    /// </summary>
    private static (string Readiness, string Liveness) ChartProbePaths()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var path = Path.Combine(dir!.FullName, "deploy", "helm", "templates",
            "memex-portal", "deployment.yaml");
        Assert.True(File.Exists(path), $"the portal deployment template is not at {path}");

        // Comments stripped: the prose around these probes discusses both paths by name, so an
        // un-stripped parse could read the explanation instead of the probe.
        var yaml = string.Join("\n", File.ReadAllLines(path)
            .Where(line => !line.TrimStart().StartsWith('#')));

        return (PathOf(yaml, "readinessProbe"), PathOf(yaml, "livenessProbe"));
    }

    private static string PathOf(string yaml, string probe)
    {
        // Bounded to lines indented deeper than the probe key: the liveness probe is the last in
        // the template, so an unbounded body runs to EOF and a sidecar httpGet added later would
        // be read as the liveness path — the guard silently changing subject.
        var block = Regex.Match(yaml, $@"^(?<indent>[ ]*){probe}:[ ]*$(?<body>(?:\n\k<indent>[ ].*)*)",
            RegexOptions.Multiline);
        Assert.True(block.Success, $"the deployment template declares no {probe}");

        var body = block.Groups["body"].Value;
        var inline = Regex.Match(body, @"httpGet:\s*\{[^}]*?\bpath:\s*(?<p>[^,}\s]+)");
        var nested = Regex.Match(body, @"httpGet:\s*(?:\r?\n\s+(?!path:)\w+:.*)*\r?\n\s+path:\s*(?<p>\S+)");
        var hit = inline.Success ? inline : nested;
        Assert.True(hit.Success,
            $"the deployment template's {probe} names no httpGet path this test can read — update "
            + "the parse deliberately rather than letting it stop matching its subject.");
        return hit.Groups["p"].Value.Trim();
    }
}
