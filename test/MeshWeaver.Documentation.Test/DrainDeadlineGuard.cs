using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A roll must not guarantee the abrupt teardown it exists to prevent</b> (#1971).
///
/// <para><c>preStop</c> polls <c>/drain</c> until the last circuit closes, and it used to have no
/// deadline of its own — <c>terminationGracePeriodSeconds</c> was the only thing that stopped it.
/// So whenever a session outlived the grace, the kubelet SIGKILLed the process <b>with a live
/// Orleans silo</b>: preStop never returned, the host's 90 s <c>ShutdownTimeout</c> never ran,
/// <c>ApplicationStopping</c> never fired, and the silo never departed membership. The
/// deployment's own <c>cluster-autoscaler.kubernetes.io/safe-to-evict: "false"</c> annotation
/// records what that costs — <i>"each abrupt departure left a ZOMBIE entry in the Orleans
/// membership table: the cluster kept placing new grain activations on the dead silo, so writes
/// timed out mesh-wide"</i>.</para>
///
/// <para>And riding to the ceiling looked like the NORMAL outcome of a roll rather than the
/// exception (memex, 2026-08-21: a terminating pod still executing application code 68 s before the
/// ceiling, while a serving pod had five circuits open at an arbitrary moment). So the invariant is
/// arithmetic, and it belongs in a guard: <b>the session drain must end with enough grace left for
/// the process to shut down in.</b></para>
///
/// <para>This is a text guard over the chart because the invariant lives in a Helm template and a
/// shell loop, where no C# unit test can observe it. The chart previously carried a scar of exactly
/// this class — a preStop that probed with <c>wget</c>, absent from the image, so the loop could
/// never succeed and EVERY termination hung to the ceiling — and it was caught by reading, not by a
/// test. This is that test.</para>
/// </summary>
public class DrainDeadlineGuard
{
    private const string Deployment = "deploy/helm/templates/memex-portal/deployment.yaml";
    private const string Values = "deploy/helm/values.yaml";

    /// <summary>Where the drain endpoint the preStop loop probes is actually mapped.</summary>
    private const string ServiceDefaults = "memex/aspire/Memex.Portal.ServiceDefaults/ServiceDefaults.cs";

    /// <summary>The base image the chart's default portal image is built on — where the probe tool
    /// preStop shells out to has to actually exist.</summary>
    private const string PortalBaseImage = "deploy/base-images/portal-ai/Dockerfile";

    /// <summary>
    /// The host's own shutdown budget (<c>Memex.Portal.Distributed/Program.cs</c>). The margin
    /// exists to make room for exactly this, so it must not be smaller.
    /// </summary>
    private const int HostShutdownTimeoutSeconds = 90;

    [Fact]
    public void ThePreStopDrain_HasADeadlineOfItsOwn_AndDoesNotRelyOnTheGraceCeiling()
    {
        var preStop = ExecutableLinesOf(File.ReadAllText(Path.Combine(FindRepoRoot(), Deployment)));

        Assert.True(preStop.Contains("/drain", StringComparison.Ordinal),
            $"{Deployment} must still run the SESSION drain — without it a roll cuts every open "
            + "circuit the moment the ingress window closes (#1342).");

        Assert.True(
            preStop.Contains("DEADLINE=", StringComparison.Ordinal)
            && preStop.Contains("shutdownMarginSeconds", StringComparison.Ordinal),
            $"{Deployment}'s preStop must bound its own poll at "
            + "(portal.drainSeconds − portal.shutdownMarginSeconds). Polling until the grace "
            + "ceiling means the kubelet SIGKILLs the process with a live Orleans silo, which "
            + "leaves a zombie membership entry the cluster keeps placing activations on (#1971).");

        Assert.True(preStop.Contains("drainSeconds", StringComparison.Ordinal),
            $"{Deployment}'s deadline must be DERIVED from portal.drainSeconds, never a second "
            + "hard-coded number — two independent constants drift, and the drift is invisible "
            + "until a pod is killed at the wrong moment.");

        // The give-up path must RETURN (exit 0) so SIGTERM is delivered, not fail — a non-zero
        // preStop is an event nobody reads and changes nothing about the kill.
        Assert.True(preStop.Contains("exit 0", StringComparison.Ordinal),
            $"{Deployment}'s preStop must exit 0 at the deadline: returning is what delivers "
            + "SIGTERM while there is still grace left to shut down in.");
    }

    /// <summary>
    /// 🚨 The arithmetic. A margin at or below the host's own <see cref="HostShutdownTimeoutSeconds"/>
    /// reserves nothing, and a margin at or above the grace would leave no session drain at all —
    /// both silently reintroduce the defect while the template still LOOKS bounded.
    /// </summary>
    [Fact]
    public void TheMargin_LeavesRoomForTheHostsOwnShutdown_AndStillLeavesASessionDrain()
    {
        var values = File.ReadAllText(Path.Combine(FindRepoRoot(), Values));

        var drain = ScalarUnder(values, "portal", "drainSeconds");
        var margin = ScalarUnder(values, "portal", "shutdownMarginSeconds");

        Assert.True(margin >= HostShutdownTimeoutSeconds,
            $"portal.shutdownMarginSeconds ({margin}s) must be at least the host's own "
            + $"ShutdownTimeout ({HostShutdownTimeoutSeconds}s, Memex.Portal.Distributed/Program.cs) "
            + "— the margin exists precisely to make room for it, and a smaller one hands the "
            + "process a shutdown it cannot finish before the SIGKILL.");

        Assert.True(drain > margin * 2,
            $"portal.drainSeconds ({drain}s) must leave a real session drain after the margin "
            + $"({margin}s). A grace that is mostly margin is a roll that cuts sessions off "
            + "immediately, which is the regression #1342 fixed.");
    }

    /// <summary>
    /// The grace ceiling must still BE drainSeconds. If they ever diverge, the deadline computed
    /// from drainSeconds stops relating to the ceiling it is supposed to stay inside.
    /// </summary>
    [Fact]
    public void TheGraceCeiling_IsStillDrainSeconds()
    {
        var deployment = ExecutableLinesOf(
            File.ReadAllText(Path.Combine(FindRepoRoot(), Deployment)));

        Assert.Contains("terminationGracePeriodSeconds:", deployment, StringComparison.Ordinal);
        Assert.True(
            Regex.IsMatch(deployment, @"terminationGracePeriodSeconds:\s*\{\{[^}]*drainSeconds"),
            $"{Deployment} must keep terminationGracePeriodSeconds derived from "
            + "portal.drainSeconds — the preStop deadline is computed against it, so a second "
            + "source for the ceiling makes the margin meaningless.");
    }

    /// <summary>
    /// 🚨 <b>The chart must probe a URL the code actually answers on.</b>
    ///
    /// <para>Everything above is arithmetic INSIDE the chart. This is the other half of #1971 —
    /// the agreement between the chart and the process — and it is the half that has already
    /// broken once: the preStop hook probed with <c>wget</c>, which is absent from the image, so
    /// the loop could never succeed and EVERY termination hung to the grace ceiling. Nothing
    /// failed; a chart and a container image simply disagreed.</para>
    ///
    /// <para>The same disagreement is available in two more places, and both are just as silent.
    /// preStop probes with <c>curl -sf -m 5 -o /dev/null</c>, which cannot tell a 404 from a 503
    /// from a refused connection — all three read as "not drained yet, keep waiting". So renaming
    /// the route, or moving the app off the probed port, does not error: it makes every roll sit
    /// out its whole drain window and then cut every open session at the deadline. The path and
    /// port are therefore part of the chart↔code contract, and this pins them to the one place
    /// each is declared. (What the endpoint ANSWERS — 200 drained / 503 with the count, and
    /// without a session — is pinned over real HTTP by <c>DrainEndpointTest</c>.)</para>
    /// </summary>
    [Fact]
    public void ThePreStopProbe_TargetsARouteTheCodeMaps_OnThePortTheContainerServes()
    {
        var root = FindRepoRoot();
        var deployment = ExecutableLinesOf(File.ReadAllText(Path.Combine(root, Deployment)));

        var probe = Regex.Match(deployment, @"curl[^\n]*?http://127\.0\.0\.1:(?<port>\d+)(?<path>/\S*)");
        Assert.True(probe.Success,
            $"{Deployment}'s preStop must probe the drain endpoint over loopback HTTP so this "
            + "guard can read the port and path it targets. If the probe shape changes, update "
            + "this guard deliberately — do not let it stop matching.");

        var path = probe.Groups["path"].Value.TrimEnd(';');
        var port = probe.Groups["port"].Value;

        var serviceDefaults = File.ReadAllText(Path.Combine(root, ServiceDefaults));
        Assert.True(
            serviceDefaults.Contains($"MapGet(\"{path}\"", StringComparison.Ordinal),
            $"{Deployment}'s preStop probes '{path}', but {ServiceDefaults} does not map that "
            + "route. curl -sf reads the resulting 404 as 'still draining', so the drain would "
            + "silently run to its deadline on every roll and cut every open session (#1971).");

        Assert.True(
            serviceDefaults.Contains(".AllowAnonymous()", StringComparison.Ordinal),
            $"the drain route must stay anonymous — a preStop exec carries no cookie and no "
            + "token, and a 401/302 is indistinguishable from 'still draining' to curl -sf.");

        // The port the container actually serves on, from the chart's own declaration of it.
        Assert.True(
            deployment.Contains($"containerPort: {port}", StringComparison.Ordinal),
            $"{Deployment}'s preStop probes port {port}, which is not a containerPort this "
            + "deployment declares. A probe of a port nothing listens on is a refused connection, "
            + "which curl -sf reports exactly like an open session.");

        var values = File.ReadAllText(Path.Combine(root, Values));
        Assert.True(
            Regex.IsMatch(values, $@"ASPNETCORE_HTTP_PORTS:\s*""?{Regex.Escape(port)}""?"),
            $"{Values} must configure the portal to listen on {port} (ASPNETCORE_HTTP_PORTS) — the "
            + "port the preStop probe targets. Two independent numbers drift, and the drift is "
            + "invisible until a roll cuts sessions.");
    }

    /// <summary>
    /// 🚨 <b>The probe tool must be IN the image.</b> This is the scar itself, pinned: preStop once
    /// probed with <c>wget</c> on the reasoning "curl is not in the base image; wget is (Debian
    /// aspnet)" — false for the image this chart actually deploys. `while ! wget …` with no wget can
    /// never succeed, so the loop would spin to the grace ceiling and SIGKILL, turning every single
    /// pod termination into a thirty-minute hang. It was found by reading a running container, not
    /// by anything that could fail.
    ///
    /// <para>The chart hedges with <c>command -v curl … || exit 0</c>, and that hedge is correct —
    /// losing the session drain costs one rollout's circuits, while hanging every pod to the ceiling
    /// costs every rollout. But a fail-open hedge is exactly the shape that makes a broken drain
    /// SILENT, so the hedge must never be the only thing standing between the chart and the image.
    /// This is the assertion that fails loudly instead.</para>
    /// </summary>
    [Fact]
    public void ThePreStopProbeTool_IsInstalledInThePortalBaseImage()
    {
        var root = FindRepoRoot();
        var preStop = ExecutableLinesOf(File.ReadAllText(Path.Combine(root, Deployment)));

        var guard = Regex.Match(preStop, @"command -v (?<tool>\w+)");
        Assert.True(guard.Success,
            $"{Deployment}'s preStop must keep its `command -v <tool>` guard — without it, a missing "
            + "probe tool hangs every termination to the grace ceiling instead of skipping the drain.");

        var tool = guard.Groups["tool"].Value;
        Assert.True(preStop.Contains($"{tool} -sf", StringComparison.Ordinal)
                    || Regex.IsMatch(preStop, $@"!\s*{Regex.Escape(tool)}\b"),
            $"{Deployment}'s preStop guards on '{tool}' but polls with something else — the guard "
            + "then protects the wrong binary and the loop can still spin to the ceiling.");

        var dockerfile = File.ReadAllText(Path.Combine(root, PortalBaseImage));
        Assert.True(
            Regex.IsMatch(dockerfile, $@"apt-get install[^\n]*\b{Regex.Escape(tool)}\b"),
            $"{Deployment}'s preStop probes with '{tool}', but {PortalBaseImage} does not install "
            + $"it. The chart's `command -v {tool} || exit 0` hedge then SKIPS the session drain on "
            + "every roll — silently, because a skipped drain and a fast one look identical from "
            + "outside. This is the wget defect (#1971), and it shipped once already.");
    }

    /// <summary>
    /// 🚨 Comment lines are stripped before every probe above. Three of the strings this guard
    /// looks for also appear in the prose explaining them, so without stripping the guard would be
    /// satisfied by the explanation while the command doing it was gone — a check that cannot fail
    /// is not a check.
    /// </summary>
    private static string ExecutableLinesOf(string yaml) =>
        string.Join("\n", yaml.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#')));

    /// <summary>Reads an integer scalar nested one level under a top-level key.</summary>
    private static int ScalarUnder(string yaml, string parent, string key)
    {
        var match = Regex.Match(
            yaml,
            $@"^{Regex.Escape(parent)}:\s*$(?<body>(?:\n(?:[ \t].*|\s*))*?)^\S",
            RegexOptions.Multiline);
        var body = match.Success ? match.Groups["body"].Value : yaml;

        var scalar = Regex.Match(body, $@"^\s+{Regex.Escape(key)}:\s*(?<value>\d+)\s*$",
            RegexOptions.Multiline);
        Assert.True(scalar.Success,
            $"{Values} must declare {parent}.{key} as an integer — it is half of the arithmetic "
            + "that keeps a roll from SIGKILLing a live silo (#1971).");
        return int.Parse(scalar.Groups["value"].Value);
    }

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
