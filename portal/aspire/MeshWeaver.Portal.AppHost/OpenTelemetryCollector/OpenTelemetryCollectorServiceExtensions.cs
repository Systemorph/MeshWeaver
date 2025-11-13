using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Portal.AppHost.OpenTelemetryCollector;

internal static class OpenTelemetryCollectorServiceExtensions
{
    public static IDistributedApplicationBuilder AddOpenTelemetryCollectorInfrastructure(this IDistributedApplicationBuilder builder)
    {
        builder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>((e, ct) =>
        {
            var appModel = e.Services.GetRequiredService<DistributedApplicationModel>();
            var logger = e.Services.GetRequiredService<ILogger<Program>>();
            return AfterEndpointsAllocatedAsync(appModel, logger, ct);
        });

        return builder;
    }

    private const string OtelExporterOtlpEndpoint = "OTEL_EXPORTER_OTLP_ENDPOINT";

    private static Task AfterEndpointsAllocatedAsync(
        DistributedApplicationModel appModel,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var collectorResource = appModel.Resources.OfType<OpenTelemetryCollectorResource>().FirstOrDefault();
        if (collectorResource == null)
        {
            logger.LogWarning($"No {nameof(OpenTelemetryCollectorResource)} resource found.");
            return Task.CompletedTask;
        }

        var endpoint = collectorResource.GetEndpoint(OpenTelemetryCollectorResource.OtlpGrpcEndpointName);
        if (!endpoint.Exists)
        {
            logger.LogWarning($"No {OpenTelemetryCollectorResource.OtlpGrpcEndpointName} endpoint for the collector.");
            return Task.CompletedTask;
        }

        foreach (var resource in appModel.Resources)
        {
            resource.Annotations.Add(new EnvironmentCallbackAnnotation((EnvironmentCallbackContext context) =>
            {
                if (context.EnvironmentVariables.ContainsKey(OtelExporterOtlpEndpoint))
                {
                    logger.LogDebug("Forwarding telemetry for {ResourceName} to the collector.", resource.Name);

                    context.EnvironmentVariables[OtelExporterOtlpEndpoint] = endpoint;
                }
            }));
        }

        return Task.CompletedTask;
    }
}
