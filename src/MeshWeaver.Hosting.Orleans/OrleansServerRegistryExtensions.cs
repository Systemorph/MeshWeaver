using MeshWeaver.Hosting;
using MeshWeaver.Connection.Orleans;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MeshWeaver.Graph;

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
    /// <param name="configurePubSubStore">Optional DURABLE backing for the streaming pub-sub
    /// subscription registry — see <see cref="ConfigureMeshWeaverServer"/>. A host that can ever run
    /// more than one silo MUST supply this; leaving it null keeps the single-silo-only in-memory
    /// store.</param>
    /// <returns>The configured mesh host application builder for further chaining.</returns>
    public static MeshHostApplicationBuilder UseOrleansMeshServer(
        this IHostApplicationBuilder hostBuilder,
        Address address,
        Func<ISiloBuilder, ISiloBuilder>? orleansConfiguration = null,
        Action<ISiloBuilder>? configurePubSubStore = null
        )
    {
        var meshBuilder = hostBuilder.CreateOrleansConnectionBuilder(address);
        meshBuilder.Host.UseOrleans(silo =>
        {
            silo.ConfigureMeshWeaverServer(configurePubSubStore);
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
    ///
    /// <para>🚨 <b><paramref name="configurePubSubStore"/> is a correctness decision, not a tuning
    /// knob (issue #1729).</b> Cross-silo delivery to every <c>StreamRoutedAddressTypes</c> hub
    /// (<c>mesh</c>, <c>portal</c>, <c>client</c>, <c>cache</c>, <c>import</c>) — which includes
    /// every REPLY to such a hub — rides an Orleans memory stream, and whether that stream can find
    /// its subscriber is decided entirely by what backs <see cref="StreamProviders.PubSubStore"/>.
    /// With the in-memory default, the subscription registry dies with the silo that happened to
    /// host the <c>PubSubRendezvousGrain</c>: the consumer's handle stays valid, the publish still
    /// reports success, and the message is DISCARDED with no error anywhere. Every rolling deploy
    /// creates that silo departure, which is how memex-cloud served ~50 % of anonymous content reads
    /// and hung the rest for a full 60 s reply budget across several image rolls.</para>
    ///
    /// <para>So: <b>a host that can ever run more than one silo MUST pass a durable store</b> (the
    /// portal derives it from its clustering provider — Postgres for AdoNet clustering). Leaving it
    /// null selects <c>AddMemoryGrainStorage</c>, which is correct ONLY for a process that is a
    /// cluster of one by construction: the Monolith, a local <c>UseLocalhostClustering</c> silo, the
    /// bake silo, and in-process <c>TestCluster</c> fixtures (whose "silos" share one process and one
    /// memory store, which is exactly why an in-process cluster cannot reproduce this defect).</para>
    ///
    /// <para>Full reference:
    /// <c>src/MeshWeaver.Documentation/Data/Architecture/OrleansStreamPubSubDurability.md</c>.</para>
    /// </summary>
    /// <param name="silo">The Orleans silo builder to configure.</param>
    /// <param name="configurePubSubStore">Registers the grain storage named
    /// <see cref="StreamProviders.PubSubStore"/>. When null, an in-memory store is registered —
    /// single-silo hosts only. The delegate is invoked INSTEAD of the memory registration, so the
    /// store has exactly one provider and no accidental last-one-wins shadowing.</param>
    /// <returns>The same silo builder for further chaining.</returns>
    public static ISiloBuilder ConfigureMeshWeaverServer(
        this ISiloBuilder silo,
        Action<ISiloBuilder>? configurePubSubStore = null)
    {
        // 🚨 SILO-ONLY, deliberately not in AddOrleansMeshServices. IClusterMembershipService and
        // ILocalSiloDetails exist only in a silo's container, and AddOrleansMeshServices also runs on
        // the Orleans CLIENT host — registering there would produce a service that throws on
        // resolution. Consumers treat "not registered" as "no cluster" (ClusterMemberState.Unknown),
        // which is exactly right for a client, a monolith, or a test.
        silo.ConfigureServices(services =>
        {
            services.TryAddSingleton<IClusterMembership, OrleansClusterMembership>();
            // 🚨 The EDGE sibling, registered under the SAME silo-only rule and for the same reason
            // ISiloStatusOracle exists nowhere else. It is what lets the pod-hub claim re-assert
            // itself when the grain directory it publishes into is re-partitioned — see
            // IClusterMembershipFeed and OrleansRoutingService.AttachPodHub. Absent on a client or
            // a monolith, where membership cannot change under this process at all.
            services.TryAddSingleton<IClusterMembershipFeed, OrleansClusterMembershipFeed>();
        });

        silo.AddMemoryStreams(StreamProviders.Memory);

        // 🚨 INSTEAD of, never in addition to. Two providers under the same name leave the
        // effective store decided by registration order — a deployment that had correctly asked
        // for durability could still be running on RAM. OrleansPubSubStoreConfigurationTest pins
        // "exactly one, and it is the caller's" for precisely that reason.
        if (configurePubSubStore is null)
            silo.AddMemoryGrainStorage(StreamProviders.PubSubStore);
        else
            configurePubSubStore(silo);

        return silo.AddIncomingGrainCallFilter<AccessContextGrainCallFilter>();
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
        // Deterministic streaming-readiness signal (silo lifecycle → Active) that the routing
        // service orders its Orleans stream subscriptions on — see OrleansStreamingReadiness.
        services.AddOrleansStreamingReadiness();
        // 🚨 Cancel + JOIN the pooled I/O before the silo releases. The silo is itself a hosted
        // service, so it stops BEFORE MeshTeardownHostedService (which drains in StoppedAsync) —
        // every grain has already deactivated and unloaded its collectible ALC by then. A pooled
        // leaf still executing that ALC's code when it unloads is the use-after-unload SIGSEGV
        // at process exit (#613). Subscribed at stage First ⇒ stops LAST, so grains keep their
        // full chance to flush before the terminal drain. See IoPoolSiloTeardown.
        services.AddIoPoolSiloTeardown();
        // 🚨 Let ACCEPTED routing work LAND before the silo stops (issue #2638). RoutingGrain's
        // turn is O(1), so Orleans' grain deactivation never waits on a route; the leg runs on
        // the routing pool but holds its permit only for the subscribe; and the per-node delivery
        // plus every NACK were detached from even that. The silo stop therefore ran — grains
        // deactivated, transport stopped, mesh drained, container disposed — over legs that were
        // still executing, and their tails died resolving grain proxies from a disposed Autofac
        // scope. RoutingQuiescence counts that work; its participant holds the silo stop at stage
        // Active (BEFORE membership announces ShuttingDown and BEFORE any grain deactivates) until
        // the count is zero, bounded, so each leg lands or is NACK'd over a live transport.
        services.AddRoutingQuiescence();
        // The root mesh hub's cross-silo REPLY stream (core#694 layer 2) — see
        // RootMeshHubReplyStreamService for the full story.
        services.AddRootMeshHubReplyStream();
        // Same wiring as the monolith: defined NodeTypes register their content types at start,
        // instance or no instance (see ContentTypeRegistrationSweep).
        services.AddContentTypeRegistrationSweep();

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
