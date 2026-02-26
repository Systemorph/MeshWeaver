using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Hosting.Activity;

/// <summary>
/// Extension methods for configuring activity tracking.
/// </summary>
public static class ActivityTrackingExtensions
{
    /// <summary>
    /// Adds user activity tracking.
    /// Activity is tracked at the navigation level (ApplicationPage) via NavigationService,
    /// not at the persistence layer, to avoid noisy internal data fetches.
    /// Falls back to InMemoryActivityStore if no IActivityStore has been registered.
    /// </summary>
    public static MeshBuilder AddActivityTracking(this MeshBuilder builder)
    {
        return builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<IActivityStore, InMemoryActivityStore>();
            return services;
        });
    }
}
