using MeshWeaver.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.ServiceDefaults;

/// <summary>
/// The <c>/health</c> check over <see cref="SourceDiscoveryRegistry"/>: the batched source
/// discovery's own chunk timing, published so MeshWeaver#3704's discriminating measurement can be
/// read with an unauthenticated <c>curl</c> instead of a Loki query nobody on this fleet is
/// authorised to run.
///
/// <para><b>What it answers.</b> Per discovery query: how many <c>QueryResultChange</c> events the
/// fold consumed, what they delivered, where it settled, and the LARGEST inter-chunk gap against
/// the completion window. That last number is the whole discriminator #3704 asks for — a gap
/// approaching the window indicts the completion rule; all-small gaps exonerate it and move the
/// search upstream to what the providers returned.</para>
///
/// <para>🚨 <b>"No pass ran" is Healthy AND it still PRINTS.</b> The batch bake issues a discovery
/// query only when something needs building, so a warm replica records none — making that Degraded
/// would leave every healthy portal permanently non-Healthy, which is a check that cannot pass.
/// But it must not be SILENT either: <see cref="ProbeEndpoints.CensusTag"/> makes this check print
/// its reading whatever the status, so "no pass was measured" and "passes were measured and were
/// clean" are two different printed sentences instead of the same absence. The STATUS is reserved
/// for the thing that is genuinely wrong — a gap that reached
/// <see cref="SourceDiscoveryRegistry.GapShareWarnPercent"/> of the window, the same threshold the
/// log's own warning uses, so the two can never disagree about one pass.</para>
///
/// <para>Degraded, never Unhealthy: a wide gap costs the batch (it falls back to the
/// activation-driven sweep, #3698) and is never a reason to pull the replica from the Service.</para>
/// </summary>
public sealed class SourceDiscoveryHealthCheck(IServiceProvider services) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var registry = services.GetService<SourceDiscoveryRegistry>();
        var passes = registry?.Snapshot() ?? [];
        var widest = registry?.Widest;
        var description = SourceDiscoveryRegistry.Describe(passes, widest);

        var data = passes.ToDictionary(
            p => p.Query,
            p => (object)p.Line,
            StringComparer.Ordinal);

        return Task.FromResult(SourceDiscoveryRegistry.IsIndicted(widest)
            ? HealthCheckResult.Degraded(description, data: data)
            : HealthCheckResult.Healthy(description, data: data));
    }
}
