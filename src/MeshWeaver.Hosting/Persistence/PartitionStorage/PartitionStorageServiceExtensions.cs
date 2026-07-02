using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.Hosting.Persistence.PartitionStorage;

/// <summary>
/// DI wiring for the per-(schema, table) partition-storage hub system.
/// Registering this replaces the silo-wide <see cref="IStorageAdapter"/>
/// binding so every consumer of <see cref="IStorageAdapter"/> routes through
/// the new <see cref="RoutingProxyAdapter"/> + <see cref="PartitionStorageRouter"/>.
/// </summary>
public static class PartitionStorageServiceExtensions
{
    /// <summary>
    /// Registers the partition-storage hub infrastructure as silo singletons:
    /// <list type="bullet">
    ///   <item><see cref="IMemoryCache"/> (if not already present) for the hub cache.</item>
    ///   <item><see cref="PartitionStorageRouter"/> — the routing table / lazy hub spawner.</item>
    ///   <item><see cref="IStorageAdapter"/> bound to <see cref="RoutingProxyAdapter"/>
    ///         (replaces any prior registration).</item>
    /// </list>
    /// <para>Call <em>after</em> the <see cref="IPartitionStorageProvider"/> registrations.
    /// First-match-wins is in registration order, so callers should add specific providers
    /// (Embedded, Static, Postgres) before catch-all providers (FileSystem, InMemory).</para>
    /// </summary>
    public static IServiceCollection AddPartitionStorageHubs(this IServiceCollection services)
    {
        services.TryAddSingleton<IMemoryCache>(_ => new MemoryCache(new MemoryCacheOptions()));

        services.AddSingleton<PartitionStorageRouter>(sp => new PartitionStorageRouter(
            sp.GetRequiredService<IMessageHub>(),
            sp.GetServices<IPartitionStorageProvider>(),
            sp.GetRequiredService<IMemoryCache>()));

        // Replace any prior IStorageAdapter binding with the proxy. The proxy
        // posts via the silo's mesh hub directly to the resolved partition-hub
        // address — caller-hub → partition-hub, no intermediate routing hub.
        services.Replace(ServiceDescriptor.Singleton<IStorageAdapter>(sp =>
            new RoutingProxyAdapter(
                sp.GetRequiredService<IMessageHub>(),
                sp.GetRequiredService<PartitionStorageRouter>(),
                sp.GetService<Microsoft.Extensions.Logging.ILogger<RoutingProxyAdapter>>())));

        return services;
    }
}
