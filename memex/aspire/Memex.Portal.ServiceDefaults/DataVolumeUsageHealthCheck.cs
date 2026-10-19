using MeshWeaver.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.ServiceDefaults;

/// <summary>
/// The <c>/health</c> census over <see cref="DataVolumeUsageCensus"/>: the last background walk of
/// the data volume, per top-level directory with its largest children (MeshWeaver#6983).
///
/// <para>Healthy whenever the census can say something true — including "not measured yet" and
/// "no data volume configured", each its own sentence — because a CENSUS publishes a number and
/// <see cref="ProbeEndpoints.CensusTag"/> prints it whatever the status. Degraded only when a walk
/// FAULTED or could not read its root: an instrument that broke must not read as one that has
/// simply not run yet. Never Unhealthy, and no probe tag: knowing what fills the volume is never a
/// reason to restart or unroute a pod.</para>
///
/// <para><c>ServiceDefaults</c> registers the census and its walk beside this check, so the two
/// cannot be separated; the census is still resolved per call, and a host that somehow lacks it
/// answers in words that say so, never with a silent Healthy.</para>
/// </summary>
public sealed class DataVolumeUsageHealthCheck(IServiceProvider services) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var census = services.GetService<DataVolumeUsageCensus>();
        if (census is null)
            return Task.FromResult(HealthCheckResult.Degraded(
                "the data-volume usage census is not registered on this host — nothing measures what fills the volume"));
        var description = census.Describe();
        return Task.FromResult(census.IsDegraded
            ? HealthCheckResult.Degraded(description)
            : HealthCheckResult.Healthy(description));
    }
}
