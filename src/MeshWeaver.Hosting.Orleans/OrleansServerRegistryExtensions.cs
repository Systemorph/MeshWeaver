using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Orleans;

/// <summary>
/// Host-builder extension methods that configure a host as a MeshWeaver Orleans silo server,
/// wiring the Orleans silo, mesh services, streams and grain call filters.
/// </summary>
public static class OrleansServerRegistryExtensions
{
    /// <summary>
    /// Configures the application host as a MeshWeaver Orleans mesh server at the given address,
    /// applying the standard silo configuration and any caller-supplied Orleans customisation.
    /// </summary>
    /// <param name="hostBuilder">The application host builder to configure.</param>
    /// <param name="address">The mesh address this server hosts.</param>
    /// <param name="orleansConfiguration">Optional additional Orleans silo configuration applied after the standard setup.</param>
    /// <returns>The configured mesh host application builder for further chaining.</returns>
    public static MeshHostApplicationBuilder UseOrleansMeshServer(
        this IHostApplicationBuilder hostBuilder,
        Address address,
        Func<ISiloBuilder, ISiloBuilder>? orleansConfiguration = null
        )
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder(address);
        meshBuilder.Host.UseOrleans(silo =>
        {
            silo.ConfigureMeshWeaverServer();
            if(orleansConfiguration is not null)
                orleansConfiguration.Invoke(silo);
        });
        return meshBuilder.UseOrleansMeshServer();
    }
    /// <summary>
    /// Configures the host as a MeshWeaver Orleans mesh server using a generated mesh address
    /// and the standard silo configuration.
    /// </summary>
    /// <param name="hostBuilder">The host builder to configure.</param>
    /// <returns>The configured mesh host builder for further chaining.</returns>
    public static MeshHostBuilder UseOrleansMeshServer(this IHostBuilder hostBuilder)
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder();
        meshBuilder.Host.UseOrleans(silo =>
        {
            silo.ConfigureMeshWeaverServer();
        });
        return meshBuilder.UseOrleansMeshServer();
    }

    internal static TBuilder UseOrleansMeshServer<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {

        builder.ConfigureHub(conf => conf
            .WithTypes(typeof(StreamActivity))
            .AddMeshTypes()
        );

        return builder;
    }

    /// <summary>
    /// Applies the standard MeshWeaver silo configuration: memory streams, the PubSub store
    /// grain storage, and the access-context incoming grain call filter.
    /// </summary>
    /// <param name="silo">The Orleans silo builder to configure.</param>
    /// <returns>The same silo builder for further chaining.</returns>
    public static ISiloBuilder ConfigureMeshWeaverServer(this ISiloBuilder silo)
    {
        return silo.AddMemoryStreams(StreamProviders.Memory)
            .AddMemoryGrainStorage("PubSubStore")
            .AddIncomingGrainCallFilter<AccessContextGrainCallFilter>();
    }

    internal static MeshHostApplicationBuilder CreateOrleansConnectionBuilder(this IHostApplicationBuilder hostBuilder, Address address)
    {
        var builder = new MeshHostApplicationBuilder(hostBuilder, address);
        builder.ConfigureMeshWeaver();
        builder.ConfigureServices(services =>
            services.AddOrleansMeshServices());

        return builder;
    }
    internal static MeshHostBuilder CreateOrleansConnectionBuilder(this IHostBuilder hostBuilder)
    {
        var builder = new MeshHostBuilder(hostBuilder, AddressExtensions.CreateMeshAddress());
        builder.ConfigureMeshWeaver();
        builder.Host.ConfigureServices(services =>
        {
            services.AddOrleansMeshServices();
        });

        return builder;
    }

    /// <summary>
    /// Registers the default Orleans mesh services — partitioned in-memory persistence, the
    /// Orleans routing service, the Orleans-distributed change feed and the mesh catalog —
    /// using try-add semantics so a caller may register replacements first.
    /// </summary>
    /// <param name="services">The service collection to add the mesh services to.</param>
    /// <returns>The same service collection for further chaining.</returns>
    public static IServiceCollection AddOrleansMeshServices(this IServiceCollection services)
    {
        // Register defaults if not already registered - user can register their own first.
        // Partition routing is the default (see OrleansConnectionExtensions for rationale).
        services.AddPartitionedInMemoryPersistence();
        services.TryAddSingleton<IRoutingService, OrleansRoutingService>();

        // Mesh-scoped registry of the last per-grain activation failure. MessageHubGrain
        // records the real activation error here (the same one it feeds to _hubReadyRaw.OnError);
        // RoutingGrain falls back to it when a persistent activation-fault loop would otherwise
        // NACK the raw Orleans rejection ("DeactivateOnIdle was called … Rejecting now") instead
        // of the actual cause (a compilation failure). See issue #464, Defect 3.
        // The registry's ctor takes the IMeshChangeFeed (registered below; DI injects it into
        // the optional parameter) so a recycle / post-commit invalidation broadcast clears the
        // stored error — stale pre-recycle error text must never be NACKed after a recycle.
        services.TryAddSingleton<GrainActivationFailureRegistry>();

        // Register Orleans-distributed change feed (wraps local feed + Orleans streams).
        // 🚨 The factory captures the ROOT IServiceProvider (sp), never IMessageHub — the feed is
        // constructed from Workspace..ctor mid-hub-build, and resolving IMessageHub there re-enters
        // BuildHub → new Workspace → IMeshChangeFeed → factory → … and stack-overflows. See
        // OrleansMeshChangeFeed's ctor doc. The cluster client / IoPool are resolved lazily from sp.
        services.TryAddSingleton<InProcessMeshChangeFeed>();
        services.TryAddSingleton<IMeshChangeFeed>(sp =>
            new OrleansMeshChangeFeed(
                sp.GetRequiredService<InProcessMeshChangeFeed>(),
                sp,
                sp.GetService<ILoggerFactory>()?.CreateLogger<OrleansMeshChangeFeed>()));

        services.AddMeshCatalog();

        return services;
    }


}
