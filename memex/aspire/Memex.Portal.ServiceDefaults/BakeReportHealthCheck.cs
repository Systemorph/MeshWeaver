using MeshWeaver.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.ServiceDefaults;

/// <summary>
/// The <c>/health</c> check over <see cref="NodeTypeBakeReportRegistry"/>: this replica's own bake
/// verdict, published so MeshWeaver#3703 can be read with an unauthenticated <c>curl</c> instead of
/// a Loki query nobody on this fleet is authorised to run.
///
/// <para><b>What it answers.</b> How many dynamic NodeTypes this replica's report covered, how many
/// of those the record and the share agree on, how many are pending — and, the verdict #3703 turns
/// on, how many were classified from a definition THIS process wrote because the enumeration
/// snapshot predates that write. The adoption-stamp count is printed beside it, because the whole
/// of #3703 was two counters in different units being read as a contradiction.</para>
///
/// <para>🚨 <b>Degraded when there is NO report, and that is the point.</b> The adopt-only probe
/// runs on every boot of every host that pre-warms, so an absent report means the enumeration
/// faulted or nothing published one — this replica measured NOTHING about its bake. Reporting that
/// as Healthy would make a missing instrument read as a clean result, which is the one thing this
/// publication exists to prevent. Degraded, never Unhealthy: a replica that cannot describe its
/// bake must not be pulled from the Service for it, and <c>/health</c> answers 200 for Degraded.</para>
///
/// <para>🚨 <b>It carries <see cref="ProbeEndpoints.CensusTag"/>, so the CLEAN reading prints
/// too.</b> Without it a clean report would be Healthy-and-silent — byte-identical on the wire to
/// this check never having been registered, which is the ambiguity again in a different costume.
/// The registry is resolved per call because the mesh's services are composed after the host's
/// health checks are registered — the same reason <see cref="ContentTypeHealthCheck"/> does.</para>
/// </summary>
public sealed class BakeReportHealthCheck(IServiceProvider services) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // 🚨 A null REGISTRY is not a null reading — it is a host that never registered the
        // instrument, and Describe(null) already says "measured NOTHING" in those words. Reporting
        // Healthy here would be the missing-instrument-reads-as-clean bug, one layer up.
        var reading = services.GetService<NodeTypeBakeReportRegistry>()?.Latest;
        var description = NodeTypeBakeReportRegistry.Describe(reading);

        if (!NodeTypeBakeReportRegistry.IsClean(reading))
            return Task.FromResult(HealthCheckResult.Degraded(description, data: DataOf(reading)));

        return Task.FromResult(HealthCheckResult.Healthy(description, data: DataOf(reading)));
    }

    private static IReadOnlyDictionary<string, object> DataOf(BakeReportReading? reading) =>
        reading is null
            ? new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["report"] = "none",
            }
            : new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["pass"] = reading.Pass,
                ["framework"] = reading.FrameworkVersion,
                ["total"] = reading.Total,
                ["baked"] = reading.Baked,
                ["pending"] = reading.Pending,
                ["classifiedFromLocalAdoption"] = reading.ClassifiedFromLocalAdoption,
                ["adoptionStamps"] = reading.AdoptionStamps,
                ["at"] = reading.At,
            };
}
