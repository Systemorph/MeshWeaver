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
///
/// <para>🚨 <b>Wired by the HOST that collects, never by every mesh.</b> It was first registered in
/// <c>MeshHostApplicationBuilder</c>, which would have put a hosted service resolving the root hub
/// into every mesh in the fleet — including the several thousand a test run builds, none of which
/// has a collector. Instrumentation that alters the startup path of hosts that will never be
/// scraped is cost without a reader, and it makes every timing-sensitive test's failure a question
/// about the meter. So the arming lives beside the OpenTelemetry subscription in the portal's
/// service defaults: both halves of the wiring in one place, and a test mesh is untouched.</para>
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
        // 🚨 Dispose-ONCE, and the field is cleared to make it so. The hub's own disposal may
        // already have taken it (RegisterForDisposal), and a comment claiming once-only semantics
        // that the code does not implement is worse than neither — Meter.Dispose happens to be
        // idempotent today, which is not a property this class should be relying on during
        // shutdown.
        var taken = metrics;
        metrics = null;
        taken?.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Wires <see cref="PlatformMetricsHostedService"/> — called by a host that actually
/// collects metrics (#3488).</summary>
public static class PlatformMetricsRegistration
{
    /// <summary>Arms the platform meter for this host.</summary>
    /// <param name="services">The host's service collection.</param>
    /// <returns>The same collection.</returns>
    public static IServiceCollection AddPlatformMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddHostedService<PlatformMetricsHostedService>();
        return services;
    }
}
