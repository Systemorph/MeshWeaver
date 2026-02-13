using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Activity;

/// <summary>
/// Extension methods for configuring activity tracking.
/// </summary>
public static class ActivityTrackingExtensions
{
    /// <summary>
    /// Adds user activity tracking to the persistence service.
    /// Requires an IActivityStore to be registered (e.g., PostgreSqlActivityStore).
    /// </summary>
    public static MeshBuilder AddActivityTracking(this MeshBuilder builder)
    {
        return builder.ConfigureServices(services =>
        {
            // Use the decorator pattern manually: wrap the existing IPersistenceServiceCore
            // with ActivityTrackingPersistenceDecorator
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IPersistenceServiceCore));
            if (descriptor != null)
            {
                services.Remove(descriptor);

                // Re-register the original implementation
                if (descriptor.ImplementationType != null)
                {
                    services.Add(new ServiceDescriptor(
                        descriptor.ImplementationType,
                        descriptor.ImplementationType,
                        descriptor.Lifetime));
                }
                else if (descriptor.ImplementationFactory != null)
                {
                    services.Add(new ServiceDescriptor(
                        typeof(IPersistenceServiceCore),
                        sp =>
                        {
                            var inner = (IPersistenceServiceCore)descriptor.ImplementationFactory(sp);
                            var activityStore = sp.GetRequiredService<IActivityStore>();
                            var accessService = sp.GetRequiredService<AccessService>();
                            var logger = sp.GetRequiredService<ILogger<ActivityTrackingPersistenceDecorator>>();
                            return new ActivityTrackingPersistenceDecorator(inner, activityStore, accessService, logger);
                        },
                        descriptor.Lifetime));
                    return services;
                }

                // Register the decorator
                services.Add(new ServiceDescriptor(
                    typeof(IPersistenceServiceCore),
                    sp =>
                    {
                        var inner = descriptor.ImplementationType != null
                            ? (IPersistenceServiceCore)sp.GetRequiredService(descriptor.ImplementationType)
                            : (IPersistenceServiceCore)descriptor.ImplementationInstance!;
                        var activityStore = sp.GetRequiredService<IActivityStore>();
                        var accessService = sp.GetRequiredService<AccessService>();
                        var logger = sp.GetRequiredService<ILogger<ActivityTrackingPersistenceDecorator>>();
                        return new ActivityTrackingPersistenceDecorator(inner, activityStore, accessService, logger);
                    },
                    descriptor.Lifetime));
            }

            return services;
        });
    }
}
