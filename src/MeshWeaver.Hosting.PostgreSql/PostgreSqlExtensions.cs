using MeshWeaver.Hosting.Embeddings;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Persistence.Query;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Factory for creating PostgreSqlStorageAdapter instances from configuration.
/// </summary>
public class PostgreSqlStorageAdapterFactory(
    IOptions<PostgreSqlStorageOptions> options) : IStorageAdapterFactory
{
    /// <summary>The storage-type key under which this factory is registered.</summary>
    public const string StorageType = "PostgreSql";

    private NpgsqlDataSource? _cachedDataSource;

    /// <inheritdoc />
    public IStorageAdapter Create(GraphStorageConfig config, IServiceProvider serviceProvider)
    {
        // Try to use an Aspire-injected or externally-registered NpgsqlDataSource first
        var dataSource = serviceProvider.GetService<NpgsqlDataSource>();

        if (dataSource == null)
        {
            // Cache the data source so multiple Create() calls share a single connection pool
            if (_cachedDataSource == null)
            {
                var opts = options.Value;
                var connectionString = opts.ConnectionString
                    ?? config.ConnectionString
                    ?? throw new InvalidOperationException(
                        "PostgreSQL connection string not configured. " +
                        "Set PostgreSqlStorageOptions.ConnectionString or Graph:Storage:ConnectionString.");

                var csb = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    MaxPoolSize = 20
                };
                var dataSourceBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
                dataSourceBuilder.UseVector();
                _cachedDataSource = dataSourceBuilder.Build();
            }
            dataSource = _cachedDataSource;
        }

        var embeddingProvider = serviceProvider.GetService<IEmbeddingProvider>();
        // Both bounds, from the MESH-SCOPED registry (never a second instance) — the same wiring
        // the partitioned provider does. Without it this adapter runs every read AND write on
        // IoPool.Unbounded against its shared data source, which is how #1310 happened on the
        // partitioned path. Pool NAMES match PostgreSqlPartitionStorageProvider.Name ("Postgres")
        // on purpose: when Aspire injects the data source, that IS the same connection pool, so
        // the bound has to be the same bound.
        var ioPoolRegistry = serviceProvider.GetService<IoPoolRegistry>();
        return new PostgreSqlStorageAdapter(
            dataSource,
            embeddingProvider,
            readPool: ioPoolRegistry?.Get(IoPoolNames.PostgresReadAdapterPrefix + "Postgres"),
            ioPool: ioPoolRegistry?.Get(IoPoolNames.PostgresAdapterPrefix + "Postgres"));
    }
}

/// <summary>
/// Extension methods for configuring PostgreSQL persistence.
/// </summary>
public static class PostgreSqlExtensions
{
    /// <summary>
    /// Registers an embedding provider from an <see cref="EmbeddingOptions"/> instance,
    /// selecting the backend by <see cref="EmbeddingOptions.Provider"/>:
    /// <list type="bullet">
    /// <item>"Ollama" / "OpenAICompatible" → <see cref="OllamaEmbeddingProvider"/> (local, on-host).</item>
    /// <item>anything else (default) → the Azure Foundry provider (the MeshWeaver.AI.AzureFoundry module) (cloud; requires an API key).</item>
    /// </list>
    /// No <see cref="EmbeddingOptions.Endpoint"/> ⇒ no provider registered, so the query path
    /// falls back to the ILIKE text search via <see cref="NullEmbeddingProvider"/>.
    /// </summary>
    public static IServiceCollection AddEmbeddings(
        this IServiceCollection services, EmbeddingOptions options)
    {
        if (services.TryAddEmbeddingProvider(options))
            services.Configure<PostgreSqlStorageOptions>(o => o.VectorDimensions = options.Dimensions);
        return services;
    }

    /// <summary>
    /// Registers the Azure Foundry embedding provider from an <see cref="EmbeddingOptions"/> instance.
    /// Back-compat shim — prefer <see cref="AddEmbeddings"/>, which also handles the local Ollama path.
    /// </summary>
    public static IServiceCollection AddAzureFoundryEmbeddings(
        this IServiceCollection services, EmbeddingOptions options)
        => services.AddEmbeddings(options);

    /// <summary>
    /// Registers the PostgreSQL storage adapter factory for use with AddPersistenceFromConfig.
    /// Also registers PostgreSqlMeshQuery for native SQL queries.
    /// </summary>
    public static IServiceCollection AddPostgreSqlStorageFactory(
        this IServiceCollection services, Action<PostgreSqlStorageOptions>? configure = null)
    {
        if (configure != null)
            services.Configure(configure);
        services.AddKeyedSingleton<IStorageAdapterFactory, PostgreSqlStorageAdapterFactory>(
            PostgreSqlStorageAdapterFactory.StorageType);

        // Register PostgreSqlMeshQuery so it takes priority over StorageAdapterMeshQueryProvider.
        // The same instance is registered under IVectorSearchProvider so the search box /
        // MCP find / agent tools resolve vector-search via the contract.
        services.AddSingleton<PostgreSqlMeshQuery>(sp =>
        {
            // GetRawStorageAdapter, never `GetRequiredService<IStorageAdapter>() as …` — the
            // default registration is a three-deep decorator chain, so the plain cast is always
            // null once AddCoreAndWrapperServices has run. Latent here only because this lane
            // has no callers today (the portals use AddPartitionedPostgreSqlPersistence); it is
            // the SAME defect that made the Cosmos lane unbootable.
            var adapter = sp.GetRawStorageAdapter<PostgreSqlStorageAdapter>()
                ?? throw new InvalidOperationException(
                    "PostgreSqlMeshQuery requires PostgreSqlStorageAdapter.");
            return new PostgreSqlMeshQuery(
                adapter,
                sp.GetService<AccessService>(),
                meshConfiguration: null,
                excludedNamespaces: null,
                embeddingProvider: sp.GetService<IEmbeddingProvider>(),
                ioPoolRegistry: sp.GetService<IoPoolRegistry>());
        });
        services.AddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<PostgreSqlMeshQuery>());
        services.AddSingleton<IVectorSearchProvider>(sp => sp.GetRequiredService<PostgreSqlMeshQuery>());

        return services;
    }

    /// <summary>
    /// Adds PostgreSQL persistence services with automatic schema creation.
    /// </summary>
    public static IServiceCollection AddPostgreSqlPersistence(
        this IServiceCollection services,
        string connectionString,
        Action<PostgreSqlStorageOptions>? configure = null)
    {
        var opts = new PostgreSqlStorageOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        var csb = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 3, ConnectionIdleLifetime = 30 };
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();

        // 🚨 The adapter is built from the REAL container, never from a
        // services.BuildServiceProvider() taken mid-registration. That shortcut builds a SECOND,
        // throwaway container: IEmbeddingProvider is registered by AddEmbeddings, so whether this
        // adapter got one came down to whether the host happened to call AddEmbeddings BEFORE this
        // method — and when it did, the adapter held a DUPLICATE singleton, not the one every other
        // consumer resolves. Both are invisible: semantic search silently degrades to the ILIKE
        // substring scan (issue #1642's real user-visible shape).
        services.AddSingleton(sp =>
            new PostgreSqlStorageAdapter(dataSource, sp.GetService<IEmbeddingProvider>()));

        // Register PostgreSqlMeshQuery BEFORE AddPersistence so TryAddSingleton picks it up.
        // Same instance under IVectorSearchProvider so the search box / MCP find / agent
        // tools route through HNSW cosine similarity when bare-text tokens are present.
        services.AddSingleton<PostgreSqlMeshQuery>(sp =>
            new PostgreSqlMeshQuery(
                sp.GetRequiredService<PostgreSqlStorageAdapter>(),
                sp.GetService<AccessService>(),
                meshConfiguration: null,
                excludedNamespaces: null,
                embeddingProvider: sp.GetService<IEmbeddingProvider>(),
                ioPoolRegistry: sp.GetService<IoPoolRegistry>()));
        services.AddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<PostgreSqlMeshQuery>());
        services.AddSingleton<IVectorSearchProvider>(sp => sp.GetRequiredService<PostgreSqlMeshQuery>());

        // Version history over this schema's mesh_node_history. BEFORE AddPersistence so its
        // NoOpVersionQuery TryAdd (in AddCoreAndWrapperServices) no-ops.
        services.AddSingleton<IVersionQuery>(_ => new PostgreSqlVersionQuery(dataSource, opts.Schema));

        services.AddPersistence(sp => sp.GetRequiredService<PostgreSqlStorageAdapter>());

        // Register access control and activity store
        services.TryAddSingleton(new PostgreSqlAccessControl(dataSource));

        return services;
    }

    /// <summary>
    /// Adds PostgreSQL persistence with LISTEN/NOTIFY change notification support.
    /// </summary>
    public static IServiceCollection AddPostgreSqlPersistenceWithChangeNotifications(
        this IServiceCollection services,
        string connectionString,
        Action<PostgreSqlStorageOptions>? configure = null)
    {
        var opts = new PostgreSqlStorageOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        var csb = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = 3, ConnectionIdleLifetime = 30 };
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(csb.ConnectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();

        // Concrete adapter type, built from the REAL container — see the same-shape comment in
        // AddPostgreSqlPersistence for why this is not services.BuildServiceProvider().
        services.AddSingleton(sp =>
            new PostgreSqlStorageAdapter(dataSource, sp.GetService<IEmbeddingProvider>()));

        // PostgreSqlMeshQuery + IVectorSearchProvider — same instance.
        services.AddSingleton<PostgreSqlMeshQuery>(sp =>
            new PostgreSqlMeshQuery(
                sp.GetRequiredService<PostgreSqlStorageAdapter>(),
                sp.GetService<AccessService>(),
                meshConfiguration: null,
                excludedNamespaces: null,
                embeddingProvider: sp.GetService<IEmbeddingProvider>(),
                ioPoolRegistry: sp.GetService<IoPoolRegistry>()));
        services.AddSingleton<IMeshQueryProvider>(sp => sp.GetRequiredService<PostgreSqlMeshQuery>());
        services.AddSingleton<IVectorSearchProvider>(sp => sp.GetRequiredService<PostgreSqlMeshQuery>());

        // Version history over this schema's mesh_node_history — see the same registration
        // in AddPostgreSqlPersistence for why it precedes AddPersistence.
        services.AddSingleton<IVersionQuery>(_ => new PostgreSqlVersionQuery(dataSource, opts.Schema));

        // Register core persistence services (IStorageAdapter, IStorageService, etc.)
        services.AddPersistence(sp => sp.GetRequiredService<PostgreSqlStorageAdapter>());

        // Register the Change Listener — feeds the adapter's Changes feed.
        services.AddSingleton(sp =>
        {
            var logger = sp.GetService<ILogger<PostgreSqlChangeListener>>();
            return new PostgreSqlChangeListener(
                dataSource, sp.GetRequiredService<PostgreSqlStorageAdapter>().ChangeObserver, logger);
        });

        // Register access control and activity store
        services.TryAddSingleton(new PostgreSqlAccessControl(dataSource));

        return services;
    }

    /// <summary>
    /// Initializes the PostgreSQL schema (tables, indexes, triggers).
    /// Call this during application startup.
    /// </summary>
    public static async Task InitializePostgreSqlSchemaAsync(
        this IServiceProvider serviceProvider,
        CancellationToken ct = default)
    {
        // Try to get data source from a registered PostgreSqlStorageAdapter, or from DI directly
        var dataSource = (serviceProvider.GetService<IStorageAdapter>() as PostgreSqlStorageAdapter)?.DataSource
            ?? serviceProvider.GetService<NpgsqlDataSource>()
            ?? throw new InvalidOperationException(
                "No NpgsqlDataSource found. Register via AddPostgreSqlPersistence or Aspire AddNpgsqlDataSource.");

        var options = serviceProvider.GetService<IOptions<PostgreSqlStorageOptions>>()?.Value
            ?? new PostgreSqlStorageOptions();

        await PostgreSqlSchemaInitializer.InitializeAsync(dataSource, options, ct).ConfigureAwait(false);

        // 🔒 #953 — the node-type-permission sync that used to run here is gone, together with the
        // `public_read` RLS term it fed. Nothing ever called this method (the migration container
        // calls PostgreSqlSchemaInitializer.InitializeAsync directly), so the table stayed empty and
        // the term was a constant `false`. Do not re-add a "populate public_read" step: see
        // PostgreSqlAccessControl and PostgreSqlSqlGenerator.BuildPerSchemaAccessClause.
    }

    /// <summary>
    /// Adds partitioned PostgreSQL persistence where each top-level path segment
    /// gets its own PostgreSQL schema with isolated tables.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="configure">Optional configuration for PostgreSqlStorageOptions</param>
    /// <param name="configureDataSource">Optional hook to further configure the <see cref="NpgsqlDataSourceBuilder"/> before the data source is built.</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPartitionedPostgreSqlPersistence(
        this IServiceCollection services,
        string connectionString,
        Action<PostgreSqlStorageOptions>? configure = null,
        Action<NpgsqlDataSourceBuilder>? configureDataSource = null)
    {
        var opts = new PostgreSqlStorageOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        // 🚨 Leak fix: the base NpgsqlDataSource (and its connection pool) used to be
        // built here as a captured local and handed to the singleton factories below.
        // A pool built outside the container is never tracked for disposal — DI only
        // disposes IDisposables IT creates (type/factory registrations, NOT externally
        // built instances passed to AddSingleton(instance)). So every mesh that ran
        // this overload leaked a full connection pool on disposal; across a test
        // project's many meshes the open server connections accumulated until Postgres
        // rejected new ones ("53300: sorry, too many clients already"). Registering the
        // data source via a factory makes the container its creator → it is disposed
        // (pool closed) when the mesh's ServiceProvider is disposed. This also unifies
        // both overloads on a single DI-resolved NpgsqlDataSource (the Aspire overload
        // already resolves it from DI).
        services.AddSingleton<NpgsqlDataSource>(_ =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.UseVector();
            configureDataSource?.Invoke(dataSourceBuilder);
            return dataSourceBuilder.Build();
        });

        // No need to remove a pre-registered InMemory wildcard: PersistenceService
        // orders wildcards by IPartitionStorageProvider.Priority desc, and
        // PostgreSqlPartitionStorageProvider returns 100 (schema-aware) vs.
        // InMemory's default 0 (catch-all). Postgres claims rbuergi (schema
        // exists) before InMemory is asked; for paths Postgres doesn't own
        // (Matches emits false), InMemory's catch-all wins.

        services.AddSingleton<PostgreSqlPartitionStorageProvider>(sp =>
            new PostgreSqlPartitionStorageProvider(
                sp.GetRequiredService<NpgsqlDataSource>(),
                connectionString,
                opts,
                partitions: null,
                sp.GetService<IEmbeddingProvider>(),
                configureDataSource,
                contexts: null,
                sp.GetService<ILogger<PostgreSqlPartitionStorageProvider>>(),
                sp.GetService<MeshWeaver.Mesh.Threading.IoPoolRegistry>()));
        services.AddSingleton<IPartitionStorageProvider>(sp =>
            sp.GetRequiredService<PostgreSqlPartitionStorageProvider>());

        // Durable event-log store (the events schema) — overrides the in-memory default from
        // AddMeshEventLog(), so the app-level outbox survives restarts in prod.
        services.Replace(ServiceDescriptor.Singleton<MeshWeaver.Hosting.IEventLogStore>(sp =>
            new MeshWeaver.Hosting.PostgreSql.PostgreSqlEventLogStore(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetService<MeshWeaver.Mesh.Threading.IoPoolRegistry>())));

        // Cross-schema query provider — UNION fan-out over searchable partitions.
        services.AddSingleton<ICrossSchemaQueryProvider>(sp =>
            new PostgreSqlCrossSchemaQueryProvider(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<PostgreSqlCrossSchemaQueryProvider>()));

        // Fan-out IMeshQueryProvider — picks up unscoped + wildcard-namespace
        // queries (Activity Feed, Latest Threads, Recently Viewed) and routes
        // them through the cross-schema UNION. Scoped queries fall through to
        // the per-schema StorageAdapterMeshQueryProvider unchanged.
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new PostgreSqlPartitionedMeshQuery(
                sp.GetRequiredService<ICrossSchemaQueryProvider>(),
                sp.GetService<AccessService>(),
                sp.GetService<ILogger<PostgreSqlPartitionedMeshQuery>>(),
                sp.GetRequiredService<PostgreSqlPartitionStorageProvider>(),
                sp.GetService<IoPoolRegistry>(),
                sp.GetService<MeshConfiguration>()));

        services.AddPartitionedChangeListener();

        // Boot-time seed: CREATE SCHEMA + table init for every framework
        // partition advertised by a static node provider. No enumeration —
        // only what's explicitly registered.
        services.AddHostedService<PostgreSqlPartitionSubscriptionHostedService>();
        // #15: the cross-silo partition-state invalidation listener
        // (PgPartitionNotifyListener / LISTEN partition_changes) is gone. The
        // router no longer caches/probes schema existence — it maps the first
        // path segment to a schema synchronously and reads tolerate an absent
        // schema (42P01 → empty). A partition created on another silo therefore
        // becomes routable immediately, with no invalidation round-trip.

        // #20: PostgreSqlPartitionedMeshQuery serves unscoped + satellite queries via
        // fast SQL fan-out, so tell the pedestrian StorageAdapterMeshQueryProvider to
        // DEFER those (walk only scoped mesh_nodes). This removes the pedestrian's
        // redundant ListChildPaths walk from those merges — the walk that gated the
        // 60-70s cross-schema ResolvePath/onboarding stall — without dropping rows
        // (the pedestrian never visited satellite tables anyway).
        services.AddSingleton(new StorageAdapterQueryProviderOptions
        {
            DeferToNativeProvider = true
        });

        // Version history: read the per-partition mesh_node_history the schema trigger
        // already populates. Registered BEFORE AddPartitionedCoreAndWrapperServices so its
        // NoOpVersionQuery TryAdd no-ops. Without this the portal "Versions" panel reads
        // through NoOpVersionQuery and shows "No version history available" for every node.
        services.AddSingleton<IVersionQuery>(sp =>
            new PostgreSqlPartitionedVersionQuery(sp.GetRequiredService<PostgreSqlPartitionStorageProvider>()));

        services.AddPartitionedCoreAndWrapperServices();

        return services;
    }

    /// <summary>
    /// Adds partitioned PostgreSQL persistence using an Aspire-injected NpgsqlDataSource from DI.
    /// Each top-level path segment gets its own PostgreSQL schema with isolated tables.
    /// Resolves the connection string from IConfiguration (Aspire convention) because
    /// NpgsqlDataSource.ConnectionString strips the password by default.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration for PostgreSqlStorageOptions</param>
    /// <param name="configureDataSource">Optional hook to further configure the <see cref="NpgsqlDataSourceBuilder"/> before the data source is built.</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddPartitionedPostgreSqlPersistence(
        this IServiceCollection services,
        Action<PostgreSqlStorageOptions>? configure = null,
        Action<NpgsqlDataSourceBuilder>? configureDataSource = null)
    {
        services.AddSingleton<PostgreSqlPartitionStorageProvider>(sp =>
        {
            var baseDataSource = sp.GetRequiredService<NpgsqlDataSource>();
            var config = sp.GetService<IConfiguration>();
            var connectionString = config?.GetConnectionString("memex")
                                   ?? baseDataSource.ConnectionString;

            var baseCsb = new NpgsqlConnectionStringBuilder(baseDataSource.ConnectionString);
            if (!string.IsNullOrEmpty(baseCsb.Username))
            {
                var csb = new NpgsqlConnectionStringBuilder(connectionString);
                if (string.IsNullOrEmpty(csb.Username))
                {
                    csb.Username = baseCsb.Username;
                    connectionString = csb.ConnectionString;
                }
            }
            var opts = new PostgreSqlStorageOptions { ConnectionString = connectionString };
            configure?.Invoke(opts);

            return new PostgreSqlPartitionStorageProvider(
                baseDataSource,
                connectionString,
                opts,
                partitions: null,
                sp.GetService<IEmbeddingProvider>(),
                configureDataSource,
                contexts: null,
                sp.GetService<ILogger<PostgreSqlPartitionStorageProvider>>(),
                sp.GetService<MeshWeaver.Mesh.Threading.IoPoolRegistry>());
        });
        services.AddSingleton<IPartitionStorageProvider>(sp =>
            sp.GetRequiredService<PostgreSqlPartitionStorageProvider>());

        // Cross-schema query provider — uses stored procedure for single-query fan-out.
        // Self-contained discovery via information_schema; no provider/factory dependency.
        services.AddSingleton<ICrossSchemaQueryProvider>(sp =>
            new PostgreSqlCrossSchemaQueryProvider(
                sp.GetRequiredService<NpgsqlDataSource>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<PostgreSqlCrossSchemaQueryProvider>()));

        // Fan-out IMeshQueryProvider — picks up unscoped + wildcard-namespace
        // queries (Activity Feed, Latest Threads, Recently Viewed) and routes
        // them through the cross-schema UNION. Scoped queries fall through to
        // the per-schema StorageAdapterMeshQueryProvider unchanged.
        services.AddSingleton<IMeshQueryProvider>(sp =>
            new PostgreSqlPartitionedMeshQuery(
                sp.GetRequiredService<ICrossSchemaQueryProvider>(),
                sp.GetService<AccessService>(),
                sp.GetService<ILogger<PostgreSqlPartitionedMeshQuery>>(),
                sp.GetRequiredService<PostgreSqlPartitionStorageProvider>(),
                sp.GetService<IoPoolRegistry>(),
                sp.GetService<MeshConfiguration>()));

        services.AddPartitionedChangeListener();

        // Start the Admin/Partition/* subscription so writes can route — see
        // the longer comment on the same registration in the connection-string
        // overload above.
        services.AddHostedService<PostgreSqlPartitionSubscriptionHostedService>();

        // #20: PostgreSqlPartitionedMeshQuery serves unscoped + satellite queries via
        // fast SQL fan-out, so tell the pedestrian StorageAdapterMeshQueryProvider to
        // DEFER those (walk only scoped mesh_nodes). This removes the pedestrian's
        // redundant ListChildPaths walk from those merges — the walk that gated the
        // 60-70s cross-schema ResolvePath/onboarding stall — without dropping rows
        // (the pedestrian never visited satellite tables anyway).
        services.AddSingleton(new StorageAdapterQueryProviderOptions
        {
            DeferToNativeProvider = true
        });

        // Version history reader over each partition's schema-qualified mesh_node_history —
        // see the connection-string overload above for why this precedes the core call.
        services.AddSingleton<IVersionQuery>(sp =>
            new PostgreSqlPartitionedVersionQuery(sp.GetRequiredService<PostgreSqlPartitionStorageProvider>()));

        services.AddPartitionedCoreAndWrapperServices();

        return services;
    }

    /// <summary>
    /// Wires the cross-process change feed: a <see cref="PostgreSqlChangeListener"/> holding one
    /// <c>LISTEN mesh_node_changes</c> session, an <c>IHostedService</c> that OPENS it at
    /// host startup, and a <see cref="PartitionChangeRouter"/> that delivers each event to the
    /// per-partition feed owning its path.
    ///
    /// <para>🚨 <b>Both registrations, always.</b> Registering the listener without the hosted
    /// service is the shape this repo shipped for months (<c>AddPostgreSqlPersistenceWithChangeNotifications</c>
    /// still does): a singleton nobody resolves, so the LISTEN session never opens and the defect
    /// is INVISIBLE — no error, no log line, just a change feed that carries only this process's
    /// own writes. That is #1440, and it is the middle leg of the three-way-broken notify chain
    /// behind #1814 (the trigger, restored by V54/#1816; this listener; and the consumer that used
    /// to discard an entity-less notification, fixed in <c>MeshDataSource</c>).</para>
    ///
    /// <para>The routing that was missing — and the reason the wiring was left commented out — is
    /// <see cref="PartitionChangeRouter"/>: the NOTIFY channel is per database while the change
    /// feed is per schema, so an event has to be resolved to a partition before it can be
    /// published. It resolves exactly as a WRITE resolves, through the routing adapter.</para>
    /// </summary>
    private static IServiceCollection AddPartitionedChangeListener(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var provider = sp.GetRequiredService<PostgreSqlPartitionStorageProvider>();
            return PostgreSqlChangeListener.OwningDataSource(
                // Built BY THE PROVIDER, from its resolved connection string and its
                // configureDataSource hook — never from NpgsqlDataSource.ConnectionString (Npgsql
                // strips the password) and never from the connection string alone (on Azure the
                // credential arrives only through that hook).
                provider.CreateChangeListenerDataSource(),
                new PartitionChangeRouter(provider, sp.GetService<ILogger<PartitionChangeRouter>>()),
                sp.GetService<ILogger<PostgreSqlChangeListener>>());
        });
        services.AddHostedService<PostgreSqlChangeListenerHostedService>();
        return services;
    }
}
