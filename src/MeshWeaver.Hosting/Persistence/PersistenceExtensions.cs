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

    /// <summary>
    /// Finds the last <see cref="IStorageAdapter"/> registration, re-exposes it as a
    /// keyed singleton with key "inner", then re-registers the default
    /// <see cref="IStorageAdapter"/> service as a <see cref="VersionWritingStorageAdapter"/>
    /// that wraps the inner. Replaces the historical
    /// <c>FileSystemPersistenceService.SaveNodeAsync</c> chokepoint deleted in the
    /// persistence cull (2026-05-12) — without this, every save path skipped the
    /// version-history snapshot.
    /// </summary>
    private static void DecorateStorageAdapterWithVersionWriting(IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(IStorageAdapter));
        if (descriptor == null) return;
        // Already decorated — bail out (idempotent for callers that compose
        // AddCoreAndWrapperServices into AddPartitionedCoreAndWrapperServices).
        if (descriptor.ImplementationType == typeof(VersionWritingStorageAdapter)) return;

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
    /// the unsecured surface (no <c>ISecurityService</c> dep) used by
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
        services.TryAddSingleton<IStorageAdapter>(sp =>
            new InMemoryStorageAdapter(sp.GetService<ILogger<InMemoryStorageAdapter>>()));
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
        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();
        // One shared in-memory adapter + provider — InMemoryPartitionStorageProvider
        // is a wildcard catch-all that handles every first-segment partition.
        services.AddSingleton<InMemoryStorageAdapter>(sp =>
            new InMemoryStorageAdapter(sp.GetService<ILoggerFactory>()?.CreateLogger<InMemoryStorageAdapter>()));
        services.AddSingleton<IPartitionStorageProvider>(sp =>
            new InMemoryPartitionStorageProvider(sp.GetRequiredService<InMemoryStorageAdapter>()));
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
        return services.AddCoreAndWrapperServices();
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
        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        // One shared FileSystemStorageAdapter + wildcard provider — handles every
        // first-segment partition rooted at baseDirectory. No factory, no per-segment
        // store provisioning.
        var fsAdapter = new FileSystemStorageAdapter(baseDirectory, writeOptionsModifier);
        services.AddSingleton(fsAdapter);
        services.AddSingleton<IPartitionStorageProvider>(new FileSystemPartitionStorageProvider(fsAdapter));

        return services.AddPartitionedCoreAndWrapperServices();
    }

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
            return services;
        });
        return builder;
    }

    /// <summary>
    /// Adds partitioned persistence using a custom IPartitionedStoreFactory.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPartitionedCoreAndWrapperServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();

        // PersistenceService bundles all IPartitionStorageProvider adapters.
        // Pure delegation by path — no cache, no init, no factory wrapper.
        services.AddSingleton<IStorageAdapter>(sp =>
            new PersistenceService(sp.GetServices<IPartitionStorageProvider>()));

        // Default adapter-backed query provider for backends without a native
        // query layer. Each backend that has a native query (Postgres, Cosmos)
        // registers its own IMeshQueryProvider separately; MeshQuery aggregates
        // every IMeshQueryProvider singleton via sp.GetServices<>().
        services.TryAddSingleton<StorageAdapterMeshQueryProvider>();
        services.TryAddSingleton<IMeshQueryProvider>(sp =>
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

        // Surface AddMeshNodes seed (held in MeshConfiguration.Nodes) as an
        // IStaticNodeProvider so the seed is visible to the static-node fan-in.
        services.AddSingleton<IStaticNodeProvider, MeshConfigurationStaticNodeProvider>();

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

        return services;
    }

    /// <summary>
    /// Helper method to register common services and wrapper services.
    /// </summary>
    private static IServiceCollection AddCoreAndWrapperServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IDataChangeNotifier, DataChangeNotifier>();
        services.TryAddSingleton<StorageAdapterMeshQueryProvider>();
        services.TryAddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<StorageAdapterMeshQueryProvider>());
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

        services.AddSingleton<IStaticNodeProvider, MeshConfigurationStaticNodeProvider>();

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

        return services;
    }

    /// <summary>
    /// Registers the MeshCatalog and its public interfaces (IMeshCatalog, IPathResolver).
    /// </summary>
    public static IServiceCollection AddMeshCatalog(this IServiceCollection services)
    {
        services.TryAddSingleton<IMeshChangeFeed, InProcessMeshChangeFeed>();
        services.TryAddSingleton<MeshCatalog>();
        services.TryAddSingleton<IMeshCatalog>(sp => sp.GetRequiredService<MeshCatalog>());
        // PathResolutionService owns the cache + subscribes to IMeshChangeFeed internally
        services.TryAddSingleton<PathResolutionService>();
        services.TryAddSingleton<IPathResolver>(sp => sp.GetRequiredService<PathResolutionService>());
        // Replay-RefCount cache for NodeType MeshNode streams. Routing reads
        // it to wait for compile readiness; grain activations read it for the
        // assembly path. See ~/.claude/plans/splendid-sauteeing-garden.md.
        services.TryAddSingleton<INodeTypeStreamCache, NodeTypeStreamCache>();
        return services;
    }
}
