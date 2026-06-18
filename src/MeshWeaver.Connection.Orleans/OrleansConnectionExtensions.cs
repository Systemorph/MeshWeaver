using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MeshWeaver.Domain;
using Microsoft.Extensions.Hosting;
using Orleans.Serialization;
using Orleans.Serialization.Configuration;

namespace MeshWeaver.Connection.Orleans;

public static class OrleansConnectionExtensions
{
    internal static MeshHostApplicationBuilder CreateOrleansConnectionBuilder(this IHostApplicationBuilder hostBuilder, Address? address = null)
    {
        var builder = new MeshHostApplicationBuilder(hostBuilder, address ?? AddressExtensions.CreateMeshAddress());
        ConfigureMeshWeaver(builder);
        builder.ConfigureServices(services =>
            services.AddOrleansMeshServices());

        return builder;
    }
    internal static MeshHostBuilder CreateOrleansConnectionBuilder(this IHostBuilder hostBuilder)
    {
        var builder = new MeshHostBuilder(hostBuilder, AddressExtensions.CreateMeshAddress());
        ConfigureMeshWeaver(builder);
        builder.Host.ConfigureServices(services =>
        {
            services.AddOrleansMeshServices();
        });

        return builder;
    }

    private static IServiceCollection AddOrleansMeshServices(this IServiceCollection services)
    {
        // Partition routing as the default: any host (silo or client) using
        // UseOrleansMeshServer / UseOrleansMeshClient gets the routing core, so
        // IPartitionStorageProvider rules (e.g. AddDocumentation's embedded-resource
        // partition) are reachable from queries without per-test reconfiguration.
        // See Doc/Architecture/PartitionedPersistence.md.
        services.AddPartitionedInMemoryPersistence();
        services.TryAddSingleton<IRoutingService, OrleansRoutingService>();
        return services;
    }

    internal static void ConfigureMeshWeaver(this MeshBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSerializer(serializerBuilder =>
            {
                serializerBuilder.AddJsonSerializer(
                    _ => true,
                    _ => true,
                    ob =>
                        ob.PostConfigure<IMessageHub>(
                            (o, hub) => o.SerializerOptions = hub.JsonSerializerOptions
                        )
                );
            });
            // Orleans' allowed-types manifest gate is enforced independently of which
            // codec handles a type. The JSON serializer above claims every type
            // (`_ => true`), but as of Orleans 10.2.0 that no longer implicitly allows
            // those types through the security gate — a grain call whose parameter is
            // the `IMessageDelivery` envelope (DeliverMessage / RouteMessage / the
            // GetStream<IMessageDelivery> fan-out) is rejected as "type not allowed".
            // This filter re-opens exactly the surface the mesh round-trips: the
            // delivery envelope itself, plus whatever the hub's ITypeRegistry has
            // registered (the single source of truth for the types this mesh
            // serializes). Nothing wider, so the gate's security purpose is intact.
            services.AddSingleton<ITypeNameFilter, MeshTypeNameFilter>();
            // Use TryAdd so user can register their own catalog first
            services.AddMeshCatalog();
            return services;
        });
        builder.ConfigureHub(conf => conf
            .AddMeshTypes()
        );
    }

}

/// <summary>
/// Orleans <see cref="ITypeNameFilter"/> that allows exactly the types this mesh
/// round-trips over grain calls — the <see cref="IMessageDelivery"/> envelope plus
/// everything registered in the hub's <see cref="ITypeRegistry"/>. Registered in
/// <see cref="OrleansConnectionExtensions.ConfigureMeshWeaver"/> so silos and clients
/// share it.
///
/// <para>Returns <c>true</c> to allow, or <c>null</c> ("no opinion") to defer to
/// Orleans' other filters — never <c>false</c>, so it only ever widens the gate for
/// mesh types and never blocks a type another filter would permit.</para>
/// </summary>
internal sealed class MeshTypeNameFilter(IServiceProvider services) : ITypeNameFilter
{
    // The mesh hub (and its ITypeRegistry) is built after this filter is registered;
    // resolve lazily on first use. `??=` retries while still null, so an early call
    // before the hub exists simply defers and a later call binds it.
    private ITypeRegistry? typeRegistry;
    private ITypeRegistry? Registry => typeRegistry ??= services.GetService<IMessageHub>()?.TypeRegistry;

    public bool? IsTypeNameAllowed(string typeName, string assemblyName)
    {
        var type = ResolveType(typeName, assemblyName);
        if (type is null)
            return null; // unresolvable (e.g. a collectible dynamic-NodeType ALC) — defer

        // The delivery envelope crosses every grain call; always allow it regardless
        // of registry contents.
        if (typeof(IMessageDelivery).IsAssignableFrom(type))
            return true;

        // Otherwise allow only what the mesh has actually registered.
        return Registry?.TryGetCollectionName(type, out _) == true ? true : null;
    }

    private static Type? ResolveType(string typeName, string assemblyName)
        => (!string.IsNullOrEmpty(assemblyName)
                ? Type.GetType($"{typeName}, {assemblyName}", throwOnError: false)
                : null)
            ?? Type.GetType(typeName, throwOnError: false);
}
