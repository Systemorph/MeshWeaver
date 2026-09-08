using MeshWeaver.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Memex.Portal.ServiceDefaults;

/// <summary>
/// The <c>/health</c> check over <see cref="ContentDegradationRegistry"/>: <c>Healthy</c> when
/// every node content read on this replica typed; <c>Degraded</c> — never <c>Unhealthy</c>, a
/// replica that renders one type empty must not be pulled from the Service — naming each node
/// type and the count. The STATE lives in the framework (the stream cache records into the
/// registry); this host-side check only reads it, exactly as the bake gate's check reads
/// <see cref="NodeTypeBakeGateState"/>. The registry is resolved per call because the mesh's
/// services are composed after the host's health checks are registered.
/// </summary>
public sealed class ContentTypeHealthCheck(IServiceProvider services) : IHealthCheck
{
    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var registry = services.GetService<ContentDegradationRegistry>();
        if (registry is null || registry.IsEmpty)
            return Task.FromResult(HealthCheckResult.Healthy(ContentDegradationRegistry.Describe([])));

        var degraded = registry.Snapshot();
        var data = degraded.ToDictionary(
            d => d.NodeType,
            d => (object)$"{d.Count} degraded read(s), last {d.LastPath} at {d.LastAt:O} via {d.Seam}",
            StringComparer.Ordinal);
        return Task.FromResult(
            HealthCheckResult.Degraded(ContentDegradationRegistry.Describe(degraded), data: data));
    }
}
