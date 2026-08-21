using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.Distributed;

/// <summary>
/// Reports modules that are LANDED but not yet LOADED — the restart-as-activation signal
/// (<see cref="ModuleActivationList.PendingRestart"/>) that #1979 found written by two sites,
/// consumed at boot, and read by nothing else.
///
/// <para><b>Why a health check rather than a log line.</b> Loading is restart-as-activation by
/// design, which makes the restart part of the install — and an install whose last step is
/// invisible reads as a broken install: buy → Get → "installed" → the feature is not there →
/// nothing anywhere says why. The same shape the Store already learned once with
/// paid-and-never-delivered: a success message that is true about the half that ran. An operator
/// surface makes a fleet with pending activations visible without opening a portal.</para>
///
/// <para>🚨 <b>DEGRADED, never Unhealthy.</b> A pending activation is a to-do, not a fault: the pod
/// is serving correctly with the modules it loaded, and failing readiness here would stall a
/// rollout over work the rollout itself performs. Degraded is reported in the payload and by
/// <c>/health</c>'s aggregate status without taking the instance out of service.</para>
///
/// <para>🚨 Reads the PERSISTED sidecar, not an in-memory flag. The process that landed the module
/// is not necessarily the process a viewer or an operator is talking to — the state outliving the
/// request that created it is the entire point.</para>
/// </summary>
public sealed class PendingModuleActivationHealthCheck(string moduleRoot) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ModuleActivationList activation;
        try
        {
            activation = ModuleActivationSidecar.Read(moduleRoot);
        }
        catch (Exception exception)
        {
            // An unreadable sidecar is not a pending activation, and must not read as one.
            return Task.FromResult(HealthCheckResult.Healthy(
                $"module activation state unreadable ({exception.GetType().Name}) — "
                + "reporting no pending activation rather than inventing one"));
        }

        if (!activation.PendingRestart)
            return Task.FromResult(HealthCheckResult.Healthy("no module activation pending"));

        var names = activation.Entries
            .Where(e => e.Enabled)
            .Select(e => e.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Task.FromResult(HealthCheckResult.Degraded(
            $"{names.Length} module(s) are landed but not yet loaded — a restart activates them: "
            + string.Join(", ", names.Take(10))
            + (names.Length > 10 ? $", …(+{names.Length - 10})" : string.Empty)));
    }
}
