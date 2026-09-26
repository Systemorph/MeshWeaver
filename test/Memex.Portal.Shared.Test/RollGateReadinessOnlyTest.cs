using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MeshWeaver.Hosting;
using Memex.Portal.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A roll gate — the NodeType bake gate, and the required-modules check — can NEVER fail
/// the startup probe</b> (policies <c>bake-gate-readiness-only</c>, MeshWeaver#5544, and
/// <c>required-modules-readiness-only</c>).
///
/// <para><b>What went wrong.</b> The host registered <c>nodetype_bake</c> untagged, so it landed on
/// <c>/health</c>, and <c>/health</c> is the startup probe. While the gate refused, the probe never
/// recorded a success and the kubelet killed every container at the end of its budget
/// (<c>10 s × 1080</c> on memex, three hours), then restarted it into the same verdict. A restarted
/// pod of the PREVIOUS image had to pass the same probe and could not either, so the gate's own
/// safety net — "the previous image keeps serving" — was gone, and memex.systemorph.com was down
/// from 20:54Z to 04:07Z on 2026-09-25/26 (Doc/Architecture/TheBakeGateOnlyStallsARoll).</para>
///
/// <para><b>The rule this pins.</b> A roll gate's verdict is read by readiness alone: the pod stays
/// alive and out of the Service, the roll stalls, nothing is killed. The check is registered here
/// EXACTLY as the host registers it today — by name, with no tag — so the test proves the rule
/// holds without the host's cooperation. It drives the REAL
/// <see cref="ServiceDefaults.AddDefaultHealthChecks"/> + <see cref="ServiceDefaults.MapDefaultEndpoints"/>
/// composition over a TestServer, on the probe PATHS the chart actually ships, for the reason
/// <see cref="ProbeSeparationTest"/> gives: a restatement of the wiring would agree with itself
/// while the deployment probed something else.</para>
/// </summary>
public class RollGateReadinessOnlyTest
{
    private const string Refusal =
        "NodeType bake regressed on this image — refusing readiness so the rollout stalls with the "
        + "previous image still serving. 1 NodeType(s) regressed on this image: Demo/Type";

    /// <summary>
    /// 🚨 The invariant. A refusing bake gate leaves the startup probe GREEN (so nothing kills the
    /// container), turns readiness RED (so the pod stays out of the Service and the roll stalls),
    /// and still PRINTS on <c>/health</c> (so the operator's instrument is unchanged).
    /// </summary>
    [Fact]
    public async Task ARefusingBakeGate_HoldsReadiness_AndNeverFailsTheStartupProbe()
    {
        var probes = ChartProbePaths();

        var (startup, startupBody) = await ProbeAsync(probes.Startup, BakeGateRefusing);
        var (readiness, readinessBody) = await ProbeAsync(probes.Readiness, BakeGateRefusing);
        var (liveness, _) = await ProbeAsync(probes.Liveness, BakeGateRefusing);

        Assert.True(startup.StatusCode == HttpStatusCode.OK,
            $"the chart's startupProbe path ('{probes.Startup}') answered {(int)startup.StatusCode} "
            + $"because {NodeTypeBakeGateExtensions.HealthCheckName} refused. That is the 2026-09-25/26 "
            + "outage: the kubelet kills every container at the end of the startup budget and a "
            + "restarted pod of the PREVIOUS image cannot pass either, so nothing serves. A roll gate "
            + $"must be read by readiness only. Body was:\n{startupBody}");
        // Degraded, not Healthy, in this bare host: the unconditional bake-report census has no
        // report to read. Degraded is a 200 — what matters is that line one is not the gate's word.
        Assert.False(startupBody.StartsWith("Unhealthy", StringComparison.Ordinal),
            "line one of /health is the startup verdict every reader parses; it must agree with the "
            + $"status code, which excludes the roll gate. Body was:\n{startupBody}");
        Assert.True(startupBody.Contains($"{NodeTypeBakeGateExtensions.HealthCheckName}: Unhealthy", StringComparison.Ordinal)
                    && startupBody.Contains("Demo/Type", StringComparison.Ordinal),
            "the refusing gate vanished from /health's body. Moving the VERDICT off the startup probe "
            + "must not remove the READING: /health is the public, past-RLS instrument operators and "
            + $"the fleet watch read the gate on. Body was:\n{startupBody}");

        Assert.True(readiness.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"the chart's readinessProbe path ('{probes.Readiness}') answered "
            + $"{(int)readiness.StatusCode} while the bake gate refused, so the gate has NO reader: a "
            + "regressed image would go Ready and take traffic. The readiness predicate must include "
            + $"the roll gates. Body was:\n{readinessBody}");
        Assert.True(readinessBody.Contains(NodeTypeBakeGateExtensions.HealthCheckName, StringComparison.Ordinal),
            "readiness refused without naming the gate, so the kubelet's probe-failure event says "
            + $"'Unhealthy' and nothing else. Body was:\n{readinessBody}");

        Assert.True(liveness.StatusCode == HttpStatusCode.OK,
            $"the chart's livenessProbe path ('{probes.Liveness}') answered {(int)liveness.StatusCode} "
            + "while the bake gate refused. Restarting the pod is the same container death on a "
            + "different probe.");
    }

    /// <summary>
    /// The host cannot put the gate back on a killing probe by TAGGING it: a <c>live</c> tag on the
    /// gate is stripped, so liveness never restarts a pod for a roll verdict.
    /// </summary>
    [Fact]
    public async Task ABakeGateTaggedLive_StillCannotRestartThePod()
    {
        var probes = ChartProbePaths();

        var (liveness, _) = await ProbeAsync(probes.Liveness, services => services.AddHealthChecks()
            .AddCheck(NodeTypeBakeGateExtensions.HealthCheckName,
                () => HealthCheckResult.Unhealthy(Refusal), [ProbeEndpoints.LiveTag]));

        Assert.True(liveness.StatusCode == HttpStatusCode.OK,
            $"the chart's livenessProbe path ('{probes.Liveness}') answered {(int)liveness.StatusCode} "
            + "for a bake gate a host had tagged 'live'. A roll gate that restarts the pod kills it "
            + "exactly as the startup probe did.");
    }

    /// <summary>
    /// 🚨 <b>A missing required module is a ROLL GATE too</b> (policy
    /// <c>required-modules-readiness-only</c>). Registered exactly as the host registers it today —
    /// by name, no tag — an Unhealthy <c>required_modules</c> must leave the startup probe and
    /// liveness GREEN (nothing kills the container, and a restarted pod of the previous image, whose
    /// shelf the same registry outage empties, still boots) and turn readiness RED (the roll stalls
    /// with the pod out of the Service). Its reading still prints on <c>/health</c>.
    /// </summary>
    [Fact]
    public async Task AMissingRequiredModule_HoldsReadiness_AndNeverFailsTheStartupProbe()
    {
        var probes = ChartProbePaths();

        var (startup, startupBody) = await ProbeAsync(probes.Startup, RequiredModulesMissing);
        var (readiness, readinessBody) = await ProbeAsync(probes.Readiness, RequiredModulesMissing);
        var (liveness, _) = await ProbeAsync(probes.Liveness, RequiredModulesMissing);

        Assert.True(startup.StatusCode == HttpStatusCode.OK,
            $"the chart's startupProbe path ('{probes.Startup}') answered {(int)startup.StatusCode} "
            + $"because {ProbeEndpoints.RequiredModulesCheckName} was Unhealthy. A missing required "
            + "module must stall the roll, never kill the container: on the startup probe a registry "
            + "outage kills every image's restarted pods alike. Body was:\n" + startupBody);
        Assert.False(startupBody.StartsWith("Unhealthy", StringComparison.Ordinal),
            $"line one of /health must agree with the startup status code. Body was:\n{startupBody}");
        Assert.True(startupBody.Contains($"{ProbeEndpoints.RequiredModulesCheckName}: Unhealthy", StringComparison.Ordinal)
                    && startupBody.Contains("Demo.Module", StringComparison.Ordinal),
            "the missing-module reading vanished from /health's body; only the VERDICT moves off the "
            + $"startup probe, never the READING. Body was:\n{startupBody}");

        Assert.True(readiness.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"the chart's readinessProbe path ('{probes.Readiness}') answered "
            + $"{(int)readiness.StatusCode} with a required module missing, so a pod without it would "
            + $"go Ready and take traffic. Body was:\n{readinessBody}");
        Assert.True(readinessBody.Contains(ProbeEndpoints.RequiredModulesCheckName, StringComparison.Ordinal),
            $"readiness refused without naming the check. Body was:\n{readinessBody}");

        Assert.True(liveness.StatusCode == HttpStatusCode.OK,
            $"the chart's livenessProbe path ('{probes.Liveness}') answered {(int)liveness.StatusCode} "
            + "with a required module missing. Restarting the pod is the same container death on a "
            + "different probe.");
    }

    /// <summary>
    /// A host cannot put <c>required_modules</c> on liveness by tagging it <c>live</c>: the tag is
    /// stripped exactly as it is from the bake gate.
    /// </summary>
    [Fact]
    public async Task ARequiredModulesCheckTaggedLive_StillCannotRestartThePod()
    {
        var probes = ChartProbePaths();

        var (liveness, _) = await ProbeAsync(probes.Liveness, services => services.AddHealthChecks()
            .AddCheck(ProbeEndpoints.RequiredModulesCheckName,
                () => HealthCheckResult.Unhealthy(MissingModule), [ProbeEndpoints.LiveTag]));

        Assert.True(liveness.StatusCode == HttpStatusCode.OK,
            $"the chart's livenessProbe path ('{probes.Liveness}') answered {(int)liveness.StatusCode} "
            + "for a required_modules check a host had tagged 'live'.");
    }

    /// <summary>
    /// 🚨 A STORE-DELIVERED module that has not arrived yet (<c>RequiredModuleStatus.ExpectedLater</c>)
    /// is reported <c>Degraded</c> by the host, and Degraded is a 200 on every probe — so it holds
    /// NOTHING, before this change and after it. Only the host's <c>Unhealthy</c> verdicts (a module
    /// the image should ship is absent, or a present module did not install against this platform)
    /// hold readiness. This pins that boundary so the policy's scope cannot be read wider than it is.
    /// </summary>
    [Fact]
    public async Task ARequiredModuleStillExpectedFromTheStore_HoldsNothing()
    {
        var probes = ChartProbePaths();
        Action<IServiceCollection> expectedLater = services => services.AddHealthChecks()
            .AddCheck(ProbeEndpoints.RequiredModulesCheckName, () => HealthCheckResult.Degraded(
                "1 required module(s) are store-delivered and not here yet: Demo.Module"));

        var (startup, _) = await ProbeAsync(probes.Startup, expectedLater);
        var (readiness, readinessBody) = await ProbeAsync(probes.Readiness, expectedLater);

        Assert.True(startup.StatusCode == HttpStatusCode.OK,
            $"startup answered {(int)startup.StatusCode} for a Degraded required_modules");
        Assert.True(readiness.StatusCode == HttpStatusCode.OK,
            $"readiness answered {(int)readiness.StatusCode} for a Degraded required_modules. Only the "
            + "host's Unhealthy verdicts are meant to hold a roll; a module the store lane has yet to "
            + $"deliver never did. Body was:\n{readinessBody}");
    }

    /// <summary>
    /// Pins the CORE side only: the roll-gate allow-list names the literal the Plugins host
    /// registers today (<c>AddCheck&lt;RequiredModulesHealthCheck&gt;("required_modules")</c> in
    /// Memex.Portal.Distributed). It cannot see that host — if the host renames its registration,
    /// this stays green while <c>TagRollGates</c> stops matching. Closing that needs the host to
    /// register under <see cref="ProbeEndpoints.RequiredModulesCheckName"/>.
    /// </summary>
    [Fact]
    public void TheRollGateAllowList_NamesTheLiteralThePluginsHostRegistersToday() =>
        Assert.Equal("required_modules", ProbeEndpoints.RequiredModulesCheckName);

    /// <summary>
    /// 🚨 The negative control that keeps the first test from being vacuous: an ordinary
    /// startup-critical check (the host's <c>db_version</c> shape) that is Unhealthy STILL fails
    /// the startup probe. Only roll gates moved; a portal that cannot boot must still not start.
    /// </summary>
    [Fact]
    public async Task AStartupCriticalCheck_StillFailsTheStartupProbe()
    {
        var probes = ChartProbePaths();

        var (startup, body) = await ProbeAsync(probes.Startup, services => services.AddHealthChecks()
            .AddCheck("db_version", () => HealthCheckResult.Unhealthy("schema version 57, image needs 58")));

        Assert.True(startup.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"the chart's startupProbe path ('{probes.Startup}') answered {(int)startup.StatusCode} "
            + "with an ordinary Unhealthy check registered. The startup verdict excludes roll gates "
            + $"ONLY; it must still refuse a portal ahead of its schema. Body was:\n{body}");
        Assert.True(body.StartsWith("Unhealthy", StringComparison.Ordinal),
            $"line one must be the startup verdict. Body was:\n{body}");
    }

    private const string MissingModule =
        "1 required module(s) absent from this pod's shelf: Demo.Module";

    private static void RequiredModulesMissing(IServiceCollection services) =>
        // Registered exactly as Memex.Portal.Distributed registers it: by name, no tags.
        services.AddHealthChecks().AddCheck(ProbeEndpoints.RequiredModulesCheckName,
            () => HealthCheckResult.Unhealthy(MissingModule));

    private static void BakeGateRefusing(IServiceCollection services) =>
        // Registered exactly as Memex.Portal.Distributed registers it: by name, no tags.
        services.AddHealthChecks().AddCheck(NodeTypeBakeGateExtensions.HealthCheckName,
            () => HealthCheckResult.Unhealthy(Refusal));

    private static async Task<(HttpResponseMessage Response, string Body)> ProbeAsync(
        string route, Action<IServiceCollection> register)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.AddDefaultHealthChecks();
        register(builder.Services);

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
    /// The three probe paths the chart SHIPS: readiness and liveness from the template, startup
    /// from <c>probes.startup.path</c> in the values (the template reads it from there).
    /// </summary>
    private static (string Startup, string Readiness, string Liveness) ChartProbePaths()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var helm = Path.Combine(dir!.FullName, "deploy", "helm");

        var template = string.Join("\n", File.ReadAllLines(
                Path.Combine(helm, "templates", "memex-portal", "deployment.yaml"))
            .Where(line => !line.TrimStart().StartsWith('#')));
        Assert.True(Regex.IsMatch(template,
                @"startupProbe:(?s).*?httpGet:[^\n]*?path:[^\n]*?\.Values\.probes\.startup\.path"),
            "the startupProbe no longer reads its path from probes.startup.path — update this test "
            + "DELIBERATELY; a guard that stops matching its subject passes having checked nothing.");

        var values = File.ReadAllText(Path.Combine(helm, "values.yaml"));
        var probesBlock = Regex.Match(values, @"^probes:\s*$(?<body>(?:\n(?:[ \t].*)?)+)", RegexOptions.Multiline);
        Assert.True(probesBlock.Success, "deploy/helm/values.yaml has no top-level 'probes:' block");
        var startup = Regex.Match(probesBlock.Groups["body"].Value,
            @"^\s+startup:\s*$(?:\n(?:\s*#.*|\s*))*?\n\s+path:\s*(?<p>\S+)", RegexOptions.Multiline);
        Assert.True(startup.Success, "values.yaml probes.startup declares no path");

        return (startup.Groups["p"].Value.Trim('"', '\''),
            PathOf(template, "readinessProbe"), PathOf(template, "livenessProbe"));
    }

    private static string PathOf(string yaml, string probe)
    {
        var block = Regex.Match(yaml, $@"^(?<indent>[ ]*){probe}:[ ]*$(?<body>(?:\n\k<indent>[ ].*)*)",
            RegexOptions.Multiline);
        Assert.True(block.Success, $"the deployment template declares no {probe}");
        var hit = Regex.Match(block.Groups["body"].Value, @"httpGet:\s*\{[^}]*?\bpath:\s*(?<p>[^,}\s]+)");
        Assert.True(hit.Success, $"the deployment template's {probe} names no inline httpGet path");
        return hit.Groups["p"].Value.Trim();
    }
}
