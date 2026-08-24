using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.Distributed;

/// <summary>
/// Reports whether this instance is actually ADOPTING the assemblies the registry is meant to serve
/// it, or quietly compiling them itself (#1782 gap 4).
///
/// <para>🚨 <b>Why a surface at all.</b> Adoption's only evidence was a log line, and the miss that
/// matters most — the registry not advertising a package for this lane — had no line at all. The
/// measurement that justified the whole lane is a pair of log lines (prod: 80 compiles / 64.8 s →
/// 0 compiles, 84 adopted, 32.1 s), and with instance-level pre-bake giving way to lazy
/// compile-on-access the fetch path becomes the PRIMARY way assemblies arrive. A lazy compile
/// absorbs a miss completely: the lane can go entirely dark while every surface looks like a
/// healthy day. That is what 2026-08-20 was — an empty index, every consumer quietly compiling,
/// nothing anywhere saying so.</para>
///
/// <para>🚨 <b>DEGRADED on a miss, never Unhealthy.</b> Compiling is correct, always-available
/// behaviour — the instance serves fine, it just paid for something it should not have. Failing
/// readiness would turn a distribution regression into an outage, which is strictly worse than the
/// regression.</para>
///
/// <para>🚨 <b>"Nothing was attempted" is its own answer.</b> A deployment with no registry
/// configured never attempts adoption, and reporting that as a clean sweep would make the absence
/// of the lane look like the success of it.</para>
/// </summary>
public sealed class BundleAdoptionHealthCheck(BundleAdoptionLedger ledger) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var description = ledger.Describe();
        return Task.FromResult(ledger.Misses.IsEmpty
            ? HealthCheckResult.Healthy(description)
            : HealthCheckResult.Degraded(description));
    }
}
