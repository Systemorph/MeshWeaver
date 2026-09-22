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
        var registry = services.GetService<NodeTypeBakeReportRegistry>();
        var reading = registry?.Latest;
        // 🚨 #4632 — the LIVE half: the catalog as it stands now, not as it stood at boot. A record
        // re-keyed to another framework after this replica booted degrades the entry; an absent
        // live reading prints as its own sentence and degrades nothing (a replica still coming up
        // has not emitted yet, and every boot degrading would teach readers to ignore the entry).
        var live = registry?.LiveRecords;
        // A FAULTED watch degrades too: it was armed and stopped, and its last reading is frozen.
        var liveFault = registry?.LiveRecordsFault;
        var description = NodeTypeBakeReportRegistry.DescribeIncludingLive(reading, live, liveFault);
        var data = DataOf(reading, live, liveFault);

        if (!NodeTypeBakeReportRegistry.IsCleanIncludingLive(reading, live, liveFault))
            return Task.FromResult(HealthCheckResult.Degraded(description, data: data));

        return Task.FromResult(HealthCheckResult.Healthy(description, data: data));
    }

    private static IReadOnlyDictionary<string, object> DataOf(
        BakeReportReading? reading, NodeTypeLiveRecordCensus? live, string? liveFault)
    {
        var data = reading is null
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
                // #4258 — the partition each non-baked type lives in, so `previouslybroken=1` can be
                // routed to an owner. Partitions only; a node title is not published here.
                ["ownership"] = reading.Ownership,
                ["at"] = reading.At,
            };
        // #4632 — the live catalog census, in the same partition-only discipline.
        data["liveRecords"] = liveFault is not null ? "faulted" : live is null ? "none" : "taken";
        if (liveFault is not null)
            data["liveFault"] = liveFault;
        if (live is not null)
        {
            data["liveTotal"] = live.Total;
            data["liveUntyped"] = live.Untyped;
            data["liveForeign"] = live.Foreign;
            data["liveForeignSinceBoot"] = live.ForeignSinceBoot;
            data["liveForeignDetail"] = live.ForeignDetail;
            data["liveBootedAt"] = live.BootedAt;
            data["liveAt"] = live.At;
        }
        return data;
    }
}
