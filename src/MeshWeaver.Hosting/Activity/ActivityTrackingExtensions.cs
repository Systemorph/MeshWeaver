using MeshWeaver.Data;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
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
    /// Uses PersistenceActivityStore/PersistenceActivityLogStore when IPersistenceServiceCore is available,
    /// falls back to InMemory stores otherwise.
    /// </summary>
    public static MeshBuilder AddActivityTracking(this MeshBuilder builder)
    {
        return builder.ConfigureServices(services =>
        {
            services.TryAddSingleton<IActivityStore>(sp =>
            {
                var persistence = sp.GetService<IPersistenceServiceCore>();
                if (persistence != null)
                    return new PersistenceActivityStore(persistence);
                return new InMemoryActivityStore();
            });
            services.TryAddSingleton<IActivityLogStore>(sp =>
            {
                var persistence = sp.GetService<IPersistenceServiceCore>();
                if (persistence != null)
                {
                    var adapter = sp.GetService<IStorageAdapter>();
                    return new PersistenceActivityLogStore(persistence, adapter: adapter);
                }
                return new InMemoryActivityLogStore();
            });
            return services;
        });
    }
}
