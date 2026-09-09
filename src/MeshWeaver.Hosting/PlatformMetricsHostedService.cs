using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Constructs the mesh's <see cref="PlatformMetrics"/> once the root hub exists, and ties it to
/// that hub's disposal (#3488).
///
/// <para>🚨 <b>A meter nobody constructs is the same silence one step later.</b> Subscribing the
/// meter NAME in the collector is half the wiring; something has to create the instance, and the
/// gauge closes over the root hub — which is not available at service-registration time. Same
/// shape, and same reason, as <c>RootMeshHubReplyStreamService</c> beside it: resolve the hub in
/// <c>StartAsync</c>, register the result for disposal on the hub itself so it dies with the mesh
/// rather than at host shutdown.</para>
///
/// <para>🚨 <b>It must never keep the host from starting.</b> Instrumentation that can abort a boot
/// is worse than no instrumentation: a portal that will not come up because its meter threw is a
/// far bigger outage than the question the meter was added to answer. A fault is reported and the
/// host carries on without the gauge.</para>
/// </summary>
/// <param name="services">The mesh's service provider.</param>
internal sealed class PlatformMetricsHostedService(IServiceProvider services) : IHostedService
{
    private PlatformMetrics? metrics;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var hub = services.GetRequiredService<IMessageHub>();
            metrics = new PlatformMetrics(hub);
            hub.RegisterForDisposal(metrics);
        }
        catch (Exception exception)
        {
            services.GetService<ILoggerFactory>()
                ?.CreateLogger<PlatformMetricsHostedService>()
                .LogWarning(exception,
                    "The platform meter could not be armed; this host reports no hub metrics. "
                    + "Everything else is unaffected — instrumentation must never keep a host from "
                    + "starting.");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Dispose-once: the hub's own disposal may already have taken it.
        metrics?.Dispose();
        return Task.CompletedTask;
    }
}
