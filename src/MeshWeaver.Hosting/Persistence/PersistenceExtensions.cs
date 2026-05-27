using System.Text.Json;
using MeshWeaver.Data.Completion;
using MeshWeaver.Hosting.Activity;
using MeshWeaver.Hosting.Completion;
using MeshWeaver.Hosting.Persistence.Http;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Extension methods for registering persistence services.
/// </summary>
public static class PersistenceExtensions
{
    /// <summary>
    /// Keyed-service key used to expose the un-decorated <see cref="IStorageAdapter"/>
    /// to the version-writing decorator (and to <see cref="IVersionQuery"/> factory
    /// type-sniffing for <see cref="FileSystemStorageAdapter"/>).
    /// </summary>
    private const string InnerStorageAdapterKey = "inner";

    /// <summary>Marker singleton: present once <see cref="DecorateStorageAdapterWithVersionWriting"/> ran.</summary>
    private sealed class VersionWritingDecoratedMarker { }

    /// <summary>
    /// Finds the last <see cref="IStorageAdapter"/> registration, re-exposes it as a
    /// keyed singleton with key "inner", then re-registers the default
    /// <see cref="IStorageAdapter"/> service as a <see cref="VersionWritingStorageAdapter"/>
    /// that wraps the inner. Replaces the historical
    /// <c>FileSystemPersistenceService.SaveNodeAsync</c> chokepoint deleted in the
    /// persistence cull (2026-05-12) — without this, every save path skipped the
    /// version-history snapshot.
    ///
    /// <para>Idempotency is via the <see cref="VersionWritingDecoratedMarker"/> singleton:
    /// the previous <c>descriptor.ImplementationType == typeof(VersionWritingStorageAdapter)</c>
    /// check never fired because the decorator is registered as a factory (so
    /// <c>ImplementationType</c> is null). Repeated calls (e.g.
    /// <c>UseOrleansMeshServer</c> wires <c>AddPartitionedInMemoryPersistence</c>
    /// as default, then the host calls another <c>Add…Persistence</c>) stacked
    /// decorators and duplicate keyed <c>"inner"</c> entries — the
    /// <see cref="System.Collections.IEnumerable"/>-mediated cycle that produced
    /// the StackOverflow at Orleans silo bootstrap.</para>
    /// </summary>
    private static void DecorateStorageAdapterWithVersionWriting(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(VersionWritingDecoratedMarker)))
            return;

        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IStorageAdapter));
        if (descriptor == null) return;

        services.Remove(descriptor);

        // Republish the original descriptor under the keyed slot so the
        // decorator (and IVersionQuery factory) can resolve the inner.
        if (descriptor.ImplementationInstance is IStorageAdapter instance)
        {
            services.AddKeyedSingleton(InnerStorageAdapterKey, instance);
        }
        else if (descriptor.ImplementationFactory != null)
        {
            services.AddKeyedSingleton<IStorageAdapter>(
                InnerStorageAdapterKey,
                (sp, _) => (IStorageAdapter)descriptor.ImplementationFactory(sp));
        }
        else if (descriptor.ImplementationType != null)
        {
            services.AddKeyedSingleton<IStorageAdapter>(
                InnerStorageAdapterKey,
                (sp, _) => (IStorageAdapter)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType));
        }
        else
        {
            // Unknown descriptor shape — put it back as-is and bail.
            services.Add(descriptor);
            return;
        }

        services.AddSingleton<IStorageAdapter>(sp =>
            new VersionWritingStorageAdapter(
                sp.GetRequiredKeyedService<IStorageAdapter>(InnerStorageAdapterKey),
                sp.GetService<IVersionQuery>()));

        // Sentinel so repeat calls (Orleans default + host-explicit persistence)
        // see the prior decoration and bail above instead of stacking another
        // decorator + another keyed "inner" entry.
        services.AddSingleton<VersionWritingDecoratedMarker>(new VersionWritingDecoratedMarker());
    }

    /// <summary>
    /// Adds persistence configured from Graph:Storage section.
    /// Uses the Type field to select the appropriate storage adapter factory.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="configuration">Configuration containing Graph:Storage section</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder AddPersistenceFromConfig<TBuilder>(this TBuilder builder, IConfiguration configuration)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddPersistenceFromConfig(configuration));
        return builder.RegisterMeshQueryCoreOnMeshHub();
    }

    /// <summary>
    /// Registers <see cref="IMeshQueryCore"/> as a singleton on the top-most
    /// mesh hub's service container. Resolves to <see cref="StorageAdapterMeshQueryProvider"/>,
    /// the unsecured surface (no <c>SecurityService</c> dep) used by
    /// <see cref="SyncedQueryMeshNodes"/> and other infrastructure paths
    /// (NavigationService, VUserHelper, etc.).
    ///
    /// <para>Per-node hubs inherit the registration through Autofac child-scope
    /// resolution, so <c>hub.ServiceProvider.GetRequiredService&lt;IMeshQueryCore&gt;()</c>
    /// works at every hub depth without walking the parent chain.</para>
    ///
    /// <para>This is the SINGLE registration point for <see cref="IMeshQueryCore"/>.
    /// Do not also register it on the root service collection — keeping it on the
    /// mesh hub avoids the AsiaRe-style "service has not been registered" failure
    /// when a per-hub container couldn't see a root-level singleton.</para>
    /// </summary>
    public static TBuilder RegisterMeshQueryCoreOnMeshHub<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        // IMeshQueryCore registration moved to the root container — see
        // AddCoreAndWrapperServices / AddPartitionedCoreAndWrapperServices.
        // The cross-instance mirror handler was deleted in the persistence-cull
        // (2026-05-12); the new shape will fan out CreateNodeRequest per node.
        return builder;
    }

    /// <summary>
    /// Adds persistence configured from Graph:Storage section.
    /// Uses the Type field to select the appropriate storage adapter factory.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">Configuration containing Graph:Storage section</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPersistenceFromConfig(this IServiceCollection services, IConfiguration configuration)
    {
        var storageConfig = configuration.GetSection("Graph:Storage").Get<GraphStorageConfig>();
        if (storageConfig == null)
        {
            throw new InvalidOperationException(
                "Graph:Storage configuration section is required. " +
                "Configure it in appsettings.json with at least Type and BasePath.");
        }

        return services.AddPersistence(storageConfig);
    }

    /// <summary>
    /// Adds persistence using the specified storage configuration.
    /// Uses the Type field to select the appropriate storage adapter factory.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="config">Storage configuration</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPersistence(this IServiceCollection services, GraphStorageConfig config)
    {
        // Register FileSystem factory as default
        services.AddKeyedSingleton<IStorageAdapterFactory, FileSystemStorageAdapterFactory>(
            FileSystemStorageAdapterFactory.StorageType);

        // Register the storage adapter using the factory
        services.AddSingleton<IStorageAdapter>(sp =>
        {
            var factory = sp.GetKeyedService<IStorageAdapterFactory>(config.Type);
            if (factory == null)
            {
                throw new InvalidOperationException(
                    $"Unknown storage type: '{config.Type}'. " +
                    $"Ensure the appropriate storage factory is registered " +
                    $"(e.g., AddPostgreSqlStorageFactory, AddCosmosStorageFactory).");
            }

            return factory.Create(config, sp);
        });

        // Register common services and wrapper services
        return services.AddCoreAndWrapperServices();
    }

    /// <summary>
    /// Adds file system persistence to the mesh builder.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="baseDirectory">The base directory for storing JSON files</param>
    /// <param name="writeOptionsModifier">Optional modifier for JsonSerializerOptions when writing (e.g., to enable WriteIndented)</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder AddFileSystemPersistence<TBuilder>(
        this TBuilder builder,
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddFileSystemPersistence(baseDirectory, writeOptionsModifier));
        return builder.RegisterMeshQueryCoreOnMeshHub();
    }

    /// <summary>
    /// Adds file system persistence to the mesh builder.
    /// Alias for AddFileSystemPersistence using the "With" naming convention.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="basePath">The base path for storing JSON files</param>
    /// <param name="writeOptionsModifier">Optional modifier for JsonSerializerOptions when writing (e.g., to enable WriteIndented)</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder WithFileSystemPersistence<TBuilder>(
        this TBuilder builder,
        string basePath,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
        where TBuilder : MeshBuilder
        => builder.AddFileSystemPersistence(basePath, writeOptionsModifier);

    /// <summary>
    /// Adds in-memory persistence to the mesh builder (no file system backing).
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder AddInMemoryPersistence<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddInMemoryPersistence());
        return builder.RegisterMeshQueryCoreOnMeshHub();
    }

    /// <summary>
    /// Adds in-memory persistence to the mesh builder (no file system backing).
    /// Alias for AddInMemoryPersistence using the "With" naming convention.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder WithInMemoryPersistence<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
        => builder.AddInMemoryPersistence();

    /// <summary>
    /// Adds in-memory persistence (no file system backing) using a single
    /// <see cref="AdapterPersistenceService"/> singleton. Use this when the test
    /// or app does not need <see cref="IPartitionStorageProvider"/> rules
    /// (e.g. <see cref="EmbeddedResourcePartition"/>) — those go through the
    /// routing core and require <see cref="AddPartitionedInMemoryPersistence(IServiceCollection)"/>.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddInMemoryPersistence(this IServiceCollection services)
    {
        // The in-memory adapter owns its own change-feed Subject and surfaces
        // it via IStorageAdapter.Changes — synced-query providers subscribe
        // there. No standalone IDataChangeNotifier service needed.
        services.TryAddSingleton<IStorageAdapter>(sp =>
            new InMemoryStorageAdapter(
                sp.GetService<ILogger<InMemoryStorageAdapter>>()));
        RegisterDefaultAssemblyStore(services);
        return services.AddCoreAndWrapperServices();
    }

    /// <summary>
    /// Adds in-memory persistence using the partition-routing stack. Each
    /// first-segment partition gets its own <see cref="AdapterPersistenceService"/>
    /// via <see cref="InMemoryPartitionedStoreFactory"/>; explicit
    /// <see cref="IPartitionStorageProvider"/> rules (e.g.
    /// <see cref="EmbeddedResourcePartitionStorageProvider"/> registered by
    /// <c>AddEmbeddedResourcePartition</c>) are honoured first-match-wins; the
    /// in-memory factory is the writable catch-all fallback. Static partitions
    /// (<see cref="PartitionDefinition"/> with <c>DataSource = "static"</c>) are
    /// surfaced read-only by <see cref="RoutingPersistenceServiceCore"/>.
    ///
    /// <para>Use this when the host registers <see cref="EmbeddedResourcePartition"/>
    /// rules — e.g. <see cref="MeshWeaver.Documentation.DocumentationExtensions.AddDocumentation"/>.
    /// Plain <see cref="AddInMemoryPersistence(IServiceCollection)"/> registers a
    /// single store and bypasses the routing core, so embedded-resource
    /// partitions wouldn't be reachable.</para>
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPartitionedInMemoryPersistence(this IServiceCollection services)
    {
        // One shared in-memory adapter + provider — InMemoryPartitionStorageProvider
        // is a wildcard catch-all that handles every first-segment partition.
        services.AddSingleton<InMemoryStorageAdapter>(sp =>
            new InMemoryStorageAdapter(sp.GetService<ILoggerFactory>()?.CreateLogger<InMemoryStorageAdapter>()));
        services.AddSingleton<IPartitionStorageProvider>(sp =>
            new InMemoryPartitionStorageProvider(sp.GetRequiredService<InMemoryStorageAdapter>()));
        RegisterDefaultAssemblyStore(services);
        return services.AddPartitionedCoreAndWrapperServices();
    }

    /// <summary>
    /// Mesh-builder shortcut for <see cref="AddPartitionedInMemoryPersistence(IServiceCollection)"/>.
    /// Also registers <c>IMeshQueryCore</c> on the top-most mesh hub.
    /// </summary>
    public static TBuilder AddPartitionedInMemoryPersistence<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddPartitionedInMemoryPersistence());
        return builder.RegisterMeshQueryCoreOnMeshHub();
    }

    /// <summary>
    /// Adds an existing in-memory storage adapter instance.
    /// Useful for tests that need to seed data before the hub is initialized.
    /// </summary>
    public static IServiceCollection AddInMemoryPersistence(this IServiceCollection services, IStorageAdapter adapter)
    {
        services.AddSingleton(adapter);
        RegisterDefaultAssemblyStore(services);
        return services.AddCoreAndWrapperServices();
    }

    /// <summary>
    /// Default IAssemblyStore: per-process temp-dir FileSystem store. TryAddSingleton
    /// so callers that wire an explicit store (production: AddBlobAssemblyStore;
    /// dev monolith / test base: AddFileSystemAssemblyStore) still win. Without
    /// this fallback, dynamic NodeType compiles run with NullAssemblyStore — the
    /// compile produces a DLL but UploadToStoreIfNeeded silently skips, the
    /// NodeTypeDefinition write-back stamps Status=Ok with null
    /// LatestAssembly{Collection,Path}, and slow-path enrichment stalls every
    /// per-instance hub waiting for assembly fields that never arrive (repro:
    /// MeshNodeVersionSyncTest after AddInMemoryPersistence override that
    /// skipped the base class's AddFileSystemAssemblyStore call).
    /// </summary>
    private static void RegisterDefaultAssemblyStore(IServiceCollection services)
    {
        services.TryAddSingleton<IAssemblyStore>(sp =>
            new MeshWeaver.Graph.Configuration.FileSystemAssemblyStore(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                    $"MeshWeaver-AssemblyStore-pid{System.Environment.ProcessId}"),
                sp.GetRequiredService<ILogger<MeshWeaver.Graph.Configuration.FileSystemAssemblyStore>>()));
    }

    /// <summary>
    /// Adds file system persistence that reads directly from disk.
    /// Uses the hub's JsonSerializerOptions for proper type polymorphism.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="baseDirectory">The base directory for storing JSON files</param>
    /// <param name="writeOptionsModifier">Optional modifier for JsonSerializerOptions when writing (e.g., to enable WriteIndented)</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddFileSystemPersistence(
        this IServiceCollection services,
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
    {
        services.AddSingleton<IStorageAdapter>(new FileSystemStorageAdapter(baseDirectory, writeOptionsModifier));

        // Register common services and wrapper services
        return services.AddCoreAndWrapperServices();
    }

    /// <summary>
    /// Adds cached file system persistence that pre-loads all files into memory at startup.
    /// All reads are served from the in-memory cache with zero disk I/O.
    /// Designed for test scenarios where repeated disk I/O is a bottleneck.
    /// </summary>
    public static TBuilder AddCachedFileSystemPersistence<TBuilder>(
        this TBuilder builder,
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStorageAdapter>(new CachingStorageAdapter(baseDirectory, writeOptionsModifier));
            return services.AddCoreAndWrapperServices();
        });
        return builder.RegisterMeshQueryCoreOnMeshHub();
    }

    /// <summary>
    /// Adds cached partitioned file system persistence that pre-loads all files into memory.
    /// </summary>
    // AddCachedPartitionedFileSystemPersistence deleted — no consumers in
    // src/, test/, samples/, or memex/. CachingStorageAdapter survives for
    // AddCachedFileSystemPersistence (single-partition fixture path).

    /// <summary>
    /// Adds a custom storage adapter with in-memory persistence service.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="storageAdapter">The custom storage adapter</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPersistence(this IServiceCollection services, IStorageAdapter storageAdapter)
    {
        services.AddSingleton(storageAdapter);

        // Register common services and wrapper services
        return services.AddCoreAndWrapperServices();
    }

    // AddPersistence(IServiceCollection, IStorageService) deleted in the cull —
    // IStorageService is gone. Callers register an IStorageAdapter directly
    // and call AddCoreAndWrapperServices().

    /// <summary>
    /// Adds partitioned file system persistence where each top-level path segment
    /// gets its own isolated partition with separate caching.
    /// Files stay in the same directory tree; isolation is logical via routing.
    /// </summary>
    /// <param name="builder">The mesh builder</param>
    /// <param name="baseDirectory">The base directory for storing JSON files</param>
    /// <param name="writeOptionsModifier">Optional modifier for JsonSerializerOptions when writing</param>
    /// <returns>The mesh builder for chaining</returns>
    public static TBuilder AddPartitionedFileSystemPersistence<TBuilder>(
        this TBuilder builder,
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services => services.AddPartitionedFileSystemPersistence(baseDirectory, writeOptionsModifier));
        return builder.RegisterMeshQueryCoreOnMeshHub();
    }

    /// <summary>
    /// Adds partitioned file system persistence where each top-level path segment
    /// gets its own isolated partition with separate caching.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="baseDirectory">The base directory for storing JSON files</param>
    /// <param name="writeOptionsModifier">Optional modifier for JsonSerializerOptions when writing</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPartitionedFileSystemPersistence(
        this IServiceCollection services,
        string baseDirectory,
        Func<JsonSerializerOptions, JsonSerializerOptions>? writeOptionsModifier = null)
    {

        // In-memory routing for Release MeshNodes that the compile watcher
        // emits at {nodeTypePath}/Release/{version}. Registered BEFORE the
        // wildcard FileSystem provider so PersistenceService's first-match-wins
        // iteration picks it for any path containing a /Release/ segment.
        // Rationale: Release nodes are regenerated on every successful compile
        // — persisting them to disk only litters file-system partition
        // directories with per-compile snapshots that go stale across
        // path-format changes (e.g. the _Release → Release rename).
        services.AddSingleton<IPartitionStorageProvider>(sp =>
            new InMemoryPartitionStorageProvider(
                adapter: new InMemoryStorageAdapter(
                    sp.GetService<ILoggerFactory>()?.CreateLogger<InMemoryStorageAdapter>()),
                matches: ContainsReleaseSegment,
                name: "InMemory:Release"));

        // One shared FileSystemStorageAdapter + wildcard provider — handles every
        // first-segment partition rooted at baseDirectory. No factory, no per-segment
        // store provisioning.
        var fsAdapter = new FileSystemStorageAdapter(baseDirectory, writeOptionsModifier);
        services.AddSingleton(fsAdapter);
        services.AddSingleton<IPartitionStorageProvider>(new FileSystemPartitionStorageProvider(fsAdapter));

        // Default IAssemblyStore so dynamic NodeType compiles can persist their
        // bytes cross-silo; see RegisterDefaultAssemblyStore for the rationale.
        RegisterDefaultAssemblyStore(services);

        return services.AddPartitionedCoreAndWrapperServices();
    }

    /// <summary>
    /// Matches a path that contains <c>Release</c> as a whole path segment —
    /// the shape <see cref="MeshDataSourceExtensions"/>'s
    /// <c>TryCreateReleaseNode</c> emits at <c>{nodeTypePath}/Release/{version}</c>.
    /// Anchored at start, after a <c>/</c>, and bounded by <c>/</c> or end-of-string
    /// so partition names that merely start with "Release" don't accidentally
    /// match.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex _releaseSegmentRegex =
        new(@"(^|/)Release(/|$)",
            System.Text.RegularExpressions.RegexOptions.Compiled
            | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool ContainsReleaseSegment(string fullPath)
        => !string.IsNullOrWhiteSpace(fullPath) && _releaseSegmentRegex.IsMatch(fullPath);

    /// <summary>
    /// Includes a specific partition by name in selective partitioned persistence.
    /// When at least one IncludePartition call is made, only explicitly included partitions are loaded.
    /// If no IncludePartition calls are made, all partitions are loaded (backward compatibility).
    /// </summary>
    public static TBuilder IncludePartition<TBuilder>(this TBuilder builder, string partitionName)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(new PartitionInclusion(partitionName));
            // Also surface every PartitionInclusion as a discoverable
            // Admin/Partition/{name} Partition MeshNode via a static provider.
            // TryAddEnumerable so a host calling IncludePartition multiple times
            // registers the provider once; the provider itself enumerates all
            // PartitionInclusion singletons resolved from DI.
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IStaticNodeProvider, IncludedPartitionStaticProvider>());
            return services;
        });
        return builder;
    }

    /// <summary>Marker singleton: present once a core/wrapper-services pass ran.</summary>
    private sealed class CoreAndWrapperServicesMarker { }

    /// <summary>
    /// Adds partitioned persistence using a custom IPartitionedStoreFactory.
    ///
    /// <para>Idempotent — repeat calls (Orleans defaults the partitioned in-memory
    /// stack from <c>UseOrleansMeshServer</c>, the host typically calls another
    /// <c>Add…Persistence</c> right after, both of which funnel here) bail on a
    /// sentinel marker. Otherwise the duplicate <c>IStorageAdapter</c> +
    /// <c>IMeshQueryProvider</c> factory registrations stack and Autofac cycles
    /// through the IEnumerable fan-in.</para>
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPartitionedCoreAndWrapperServices(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(CoreAndWrapperServicesMarker)))
            return services;


        // PersistenceService bundles all IPartitionStorageProvider adapters.
        // Pure delegation by path — no cache, no init, no factory wrapper.
        services.AddSingleton<IStorageAdapter>(sp =>
            new PersistenceService(sp.GetServices<IPartitionStorageProvider>()));

        // Default adapter-backed query provider — pedestrian exact-path probes
        // every backend needs in the IMeshQueryProvider fan-in. Native query
        // backends (Postgres, Cosmos) register their OWN IMeshQueryProvider
        // alongside this one; both are needed because the native provider
        // typically short-circuits scoped queries (path:X) back to this
        // pedestrian one for the actual row fetch.
        //
        // 🚨 AddSingleton, not TryAddSingleton: `TryAddSingleton<IMeshQueryProvider>`
        // is FIRST-WINS by service-type, so a backend that registered its own
        // IMeshQueryProvider BEFORE calling AddPartitionedCoreAndWrapperServices
        // would silently skip this registration, leaving the fan-in without the
        // pedestrian exact-path probe — symptom: PathResolver returns null for
        // any path the backend doesn't handle in fan-out mode (Postgres
        // partition-scoped path queries; repro:
        // PartitionLifecycleTests.LazyCreate_FirstWrite_EnablesSubsequentReads).
        // The CoreAndWrapperServicesMarker idempotency guard above guarantees
        // we only register this once per service collection.
        services.TryAddSingleton<StorageAdapterMeshQueryProvider>();
        services.AddSingleton<IMeshQueryProvider>(sp =>
            sp.GetRequiredService<StorageAdapterMeshQueryProvider>());

        // IMeshQueryCore — one boss for unsecured fan-out across every registered
        // IMeshQueryProvider. Hub null is OK; the unsecured surface takes options
        // explicitly and doesn't read hub.JsonSerializerOptions.
        services.TryAddSingleton<IMeshQueryCore>(sp =>
            new MeshQuery(sp.GetServices<IMeshQueryProvider>(), hub: null!));

        // Versioning is per-backend. Default no-op; PostgreSQL / FileSystem
        // can override with their own IVersionQuery registration.
        services.TryAddSingleton<IVersionQuery>(sp => new NoOpVersionQuery());

        DecorateStorageAdapterWithVersionWriting(services);

        // Static-node query provider for built-in catalogs (Agent, Model, Role).
        services.AddSingleton<IMeshQueryProvider>(sp =>
        {
            var providers = sp.GetServices<IStaticNodeProvider>();
            var config = sp.GetService<MeshConfiguration>();
            return new StaticNodeQueryProvider(
                providers,
                StaticNodeQueryProvider.BuildDefaultMatches(providers, config),
                config,
                sp.GetService<ILoggerFactory>());
        });

        // AddMeshNodes seed flows through StaticMeshNodeListProvider, registered
        // in MeshBuilder.Build — no extra registration needed here.

        services.AddMeshCatalog();

        services.AddScoped<IMeshService>(sp =>
            new MeshService(
                sp.GetServices<IMeshQueryProvider>(),
                sp.GetRequiredService<IMessageHub>()));

        services.TryAddScoped<IChatCompletionOrchestrator>(sp =>
            new ChatCompletionOrchestrator(
                sp.GetRequiredService<IMeshService>(),
                sp.GetRequiredService<IMessageHub>(),
                sp.GetService<ILogger<ChatCompletionOrchestrator>>()));

        services.AddSingleton<CoreAndWrapperServicesMarker>(new CoreAndWrapperServicesMarker());
        return services;
    }

    /// <summary>
    /// Helper method to register common services and wrapper services.
    /// Shares the <see cref="CoreAndWrapperServicesMarker"/> sentinel with the
    /// partitioned overload so mixed callers (e.g. <c>UseOrleansMeshServer</c>
    /// + <c>AddFileSystemPersistence</c>) bail safely on the second pass
    /// instead of stacking duplicate registrations.
    /// </summary>
    private static IServiceCollection AddCoreAndWrapperServices(this IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(CoreAndWrapperServicesMarker)))
            return services;

        services.TryAddSingleton<StorageAdapterMeshQueryProvider>();
        // 🚨 AddSingleton, not TryAddSingleton — see the same-shape comment in
        // AddPartitionedCoreAndWrapperServices. Backends that register their own
        // IMeshQueryProvider (Postgres, Cosmos) BEFORE this method runs would
        // otherwise crowd the pedestrian provider out and break exact-path probes.
        services.AddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<StorageAdapterMeshQueryProvider>());
        // IMeshQueryCore — one boss for unsecured fan-out (see comment on the
        // partitioned registration). Constructs MeshQuery over every
        // IMeshQueryProvider; null hub is OK because the IMeshQueryCore
        // surface takes options explicitly.
        services.TryAddSingleton<IMeshQueryCore>(sp =>
            new MeshQuery(sp.GetServices<IMeshQueryProvider>(), hub: null!));

        services.AddSingleton<StaticNodeQueryProvider>(sp =>
        {
            var providers = sp.GetServices<IStaticNodeProvider>();
            var config = sp.GetService<MeshConfiguration>();
            return new StaticNodeQueryProvider(
                providers,
                StaticNodeQueryProvider.BuildDefaultMatches(providers, config),
                config,
                sp.GetService<ILoggerFactory>());
        });
        services.AddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<StaticNodeQueryProvider>());

        services.TryAddSingleton<IVersionQuery>(sp =>
        {
            // Resolve through the keyed "inner" registration set up by
            // DecorateStorageAdapterWithVersionWriting — otherwise the lookup
            // recurses into the decorator and stack-overflows.
            var inner = sp.GetKeyedService<IStorageAdapter>(InnerStorageAdapterKey)
                        ?? sp.GetService<IStorageAdapter>();
            if (inner is FileSystemStorageAdapter fsAdapter)
                return new FileSystemVersionStore(fsAdapter.BaseDirectory);
            return new NoOpVersionQuery();
        });

        DecorateStorageAdapterWithVersionWriting(services);

        services.AddMeshCatalog();


        services.AddScoped<IMeshService>(sp =>
            new MeshService(
                sp.GetServices<IMeshQueryProvider>(),
                sp.GetRequiredService<IMessageHub>()));
        services.TryAddScoped<IChatCompletionOrchestrator>(sp =>
            new ChatCompletionOrchestrator(
                sp.GetRequiredService<IMeshService>(),
                sp.GetRequiredService<IMessageHub>(),
                sp.GetService<ILogger<ChatCompletionOrchestrator>>()));

        services.AddSingleton<CoreAndWrapperServicesMarker>(new CoreAndWrapperServicesMarker());
        return services;
    }

    /// <summary>
    /// Registers <see cref="IPathResolver"/> + the shared mesh-node stream cache.
    /// (Old name <c>AddMeshCatalog</c> retained for back-compat; the MeshCatalog
    /// class itself is gone — path resolution lives in PathResolutionService now.)
    /// </summary>
    public static IServiceCollection AddMeshCatalog(this IServiceCollection services)
    {
        services.TryAddSingleton<IMeshChangeFeed, InProcessMeshChangeFeed>();
        // PathResolutionService owns the per-path Replay(1).RefCount() cache
        // and subscribes to IMeshChangeFeed internally.
        services.TryAddSingleton<PathResolutionService>();
        services.TryAddSingleton<IPathResolver>(sp => sp.GetRequiredService<PathResolutionService>());
        // Replay-AutoConnect cache for MeshNode streams. Routing reads it for
        // compile readiness; grain activations read it for the assembly path;
        // path-resolution lookups share the same handle so a single live
        // upstream subscription per path serves every consumer.
        services.TryAddSingleton<MeshNodeStreamCache>();
        services.TryAddSingleton<IMeshNodeStreamCache>(sp => sp.GetRequiredService<MeshNodeStreamCache>());
        return services;
    }
}
