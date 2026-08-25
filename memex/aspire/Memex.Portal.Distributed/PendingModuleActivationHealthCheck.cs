using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.Distributed;

/// <summary>
/// Reports modules that are LANDED but not yet LOADED — the restart-as-activation signal that
/// #1979 found written by two sites, consumed at boot, and read by nothing else.
///
/// <para><b>Why a health check rather than a log line.</b> Loading is restart-as-activation by
/// design, which makes the restart part of the install — and an install whose last step is
/// invisible reads as a broken install: buy → Get → "installed" → the feature is not there →
/// nothing anywhere says why. The same shape the Store already learned once with
/// paid-and-never-delivered: a success message that is true about the half that ran. An operator
/// surface makes a fleet with pending activations visible without opening a portal.</para>
///
/// <para>🚨 <b>Per PROCESS, not per deployment.</b> It asks
/// <see cref="PendingModuleActivations"/> — the persisted sidecar compared against the assemblies
/// THIS pod actually loaded — rather than reading <c>PendingRestart</c>, which is one
/// deployment-wide boolean that the next boot clears. On a multi-replica deployment the pod that
/// clears it is not the pod that is missing the module: replica A lands one and sets the flag,
/// replica B restarts for an unrelated reason and resets it, and A keeps serving without the
/// module while every surface reads "nothing pending". The comparison also names only what is
/// actually missing here, instead of every enabled entry.</para>
///
/// <para>🚨 <b>DEGRADED, never Unhealthy.</b> A pending activation is a to-do, not a fault: the pod
/// is serving correctly with the modules it loaded, and failing readiness here would stall a
/// rollout over work the rollout itself performs. Degraded is reported in the payload and by
/// <c>/health</c>'s aggregate status without taking the instance out of service.</para>
///
/// <para>🚨 <b>An UNREADABLE sidecar is Degraded too, never Healthy.</b> A file that cannot be
/// parsed is the absence of an answer, and this check reported it as "no pending activation" —
/// because <see cref="ModuleActivationSidecar.Read"/> swallows corruption into the empty list. A
/// check that cannot reach its evidence must say so, not default to the reassuring
/// answer.</para>
/// </summary>
public sealed class PendingModuleActivationHealthCheck(PendingModuleActivations activations) : IHealthCheck
{
    /// <summary>Constructs the reader from a module root — the shape the portal's boot uses.</summary>
    public PendingModuleActivationHealthCheck(string moduleRoot)
        : this(new PendingModuleActivations(moduleRoot)) { }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ModuleActivationReport report;
        try
        {
            report = activations.Read();
        }
        catch (Exception exception)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "module activation state could not be determined "
                + $"({exception.GetType().Name}: {exception.Message}) — this pod cannot say whether "
                + "a landed module is waiting on a restart"));
        }

        if (report.IsUndetermined)
            return Task.FromResult(HealthCheckResult.Degraded(report.Describe()));

        // 🚨 An ACTIVATED module whose landed assembly is GONE degrades too, and says so in its own
        // words (#2093). It is not "pending": no restart loads it, so folding it into the pending
        // line would print a restart prompt that can never come true — the exact false promise that
        // let /mcp 404 for a pod's whole lifetime while every surface read "restart required".
        return report.HasPending || report.HasUnresolvable
            ? Task.FromResult(HealthCheckResult.Degraded(report.Describe()))
            : Task.FromResult(HealthCheckResult.Healthy(report.Describe()));
    }
}
