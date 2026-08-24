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
