using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Data.Validation;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

public sealed record DataContext : IDisposable
{
    public const string InitializationGateName = "DataContextInit";

    public ITypeRegistry TypeRegistry { get; }

    public DataContext(IWorkspace workspace)
    {
        Hub = workspace.Hub;
        logger = Hub.ServiceProvider.GetRequiredService<ILogger<DataContext>>();
        Workspace = workspace;
        ReduceManager = Hub.CreateReduceManager();

        TypeRegistry = Hub.ServiceProvider.GetRequiredService<ITypeRegistry>();
        TypeRegistry.WithKeyFunctionProvider(type =>
            KeyFunctionBuilder.GetFromProperties(
                type,
                type.GetProperties().Where(x => x.HasAttribute<DimensionAttribute>()).ToArray()
            ) ?? null
        );
    }

    private readonly ILogger<DataContext> logger;

    private Dictionary<Type, ITypeSource> TypeSourcesByType { get; set; } = new();

    public IEnumerable<IDataSource> DataSources => DataSourcesById.Values;

    private ImmutableDictionary<object, IDataSource> DataSourcesById { get; set; } =
        ImmutableDictionary<object, IDataSource>.Empty;

    public IDataSource? GetDataSourceForId(object id) => DataSourcesById.GetValueOrDefault(id);

    public IDataSource? GetDataSourceForType(Type type) => DataSourcesByType.GetValueOrDefault(type)
          ?? (type.BaseType == typeof(object) || type.BaseType == null ? null : GetDataSourceForType(type.BaseType));

    public IReadOnlyDictionary<Type, IDataSource> DataSourcesByType { get; private set; } = new Dictionary<Type, IDataSource>();
    public IReadOnlyDictionary<string, IDataSource> DataSourcesByCollection { get; private set; } = new Dictionary<string, IDataSource>();

    public DataContext WithDataSource(DataSourceBuilder dataSourceBuilder) =>
        this with { DataSourceBuilders = DataSourceBuilders.Add(dataSourceBuilder), };

    public IReadOnlyDictionary<string, ITypeSource> TypeSources { get; private set; } = new Dictionary<string, ITypeSource>();

    public ITypeSource? GetTypeSource(string collection) =>
        TypeSources.GetValueOrDefault(collection);

    public ITypeSource? GetTypeSource(Type type) =>
        TypeSourcesByType.GetValueOrDefault(type)
        ?? (type.BaseType == typeof(object) || type.BaseType == null ? null : GetTypeSource(type.BaseType));


    public ImmutableList<DataSourceBuilder> DataSourceBuilders { get; set; } =
        ImmutableList<DataSourceBuilder>.Empty;

    internal ReduceManager<EntityStore> ReduceManager { get; init; }
    internal TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromHours(60);
    public IMessageHub Hub { get; }
    public IWorkspace Workspace { get; }

    /// <summary>
    /// Factory function that provides the default data reference for this context.
    /// Used when accessing data via data:addressType/addressId without specifying a collection.
    /// </summary>
    public Func<IWorkspace, IObservable<object?>>? DefaultDataReferenceFactory { get; init; }

    /// <summary>
    /// Mapping of collection names in data paths to content collection names.
    /// Used for accessing files via data:addressType/addressId/collection/path patterns.
    /// </summary>
    public ImmutableDictionary<string, string> ContentProviders { get; init; } =
        ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Virtual path handlers that resolve custom data paths to streams.
    /// Key is the path prefix (e.g., "OrderSummary"), value is a factory function
    /// that returns an observable stream based on the path.
    /// </summary>
    public ImmutableDictionary<string, VirtualPathHandler> VirtualPaths { get; init; } =
        ImmutableDictionary<string, VirtualPathHandler>.Empty;

    /// <summary>
    /// Unified reference resolvers keyed by prefix (e.g., "data", "area", "content").
    /// Each prefix has a list of resolvers tried in order (first one returning non-null wins).
    /// New resolvers are inserted at position 0 to allow overriding default behavior.
    /// </summary>
    public ImmutableDictionary<string, ImmutableList<UnifiedReferenceResolver>> UnifiedReferenceResolvers { get; init; } =
        ImmutableDictionary<string, ImmutableList<UnifiedReferenceResolver>>.Empty;

    /// <summary>
    /// Global access restrictions applied to all data operations.
    /// Evaluated before type-specific restrictions.
    /// </summary>
    public ImmutableList<AccessRestrictionEntry> GlobalAccessRestrictions { get; init; } =
        ImmutableList<AccessRestrictionEntry>.Empty;

    public DataContext WithInitializationTimeout(TimeSpan timeout) =>
        this with { InitializationTimeout = timeout };

    public DataContext Configure(
        Func<ReduceManager<EntityStore>, ReduceManager<EntityStore>> change
    ) => this with { ReduceManager = change.Invoke(ReduceManager) };

    public delegate IDataSource DataSourceBuilder(IMessageHub hub);

    public void Initialize()
    {
        logger.LogDebug("Starting initialization of DataContext for {Address}", Hub.Address);

        // Build data sources, handling duplicates by keeping the last one with each ID
        // This can happen when multiple configurations add the same data source type
        var dataSources = DataSourceBuilders.Select(x => x.Invoke(Hub)).ToList();
        var deduped = new Dictionary<object, IDataSource>();
        foreach (var ds in dataSources)
        {
            if (deduped.ContainsKey(ds.Id))
            {
                logger.LogDebug("DataContext: Duplicate data source ID '{Id}', keeping last one", ds.Id);
            }
            deduped[ds.Id] = ds;
        }
        DataSourcesById = deduped.ToImmutableDictionary();

        // Build TypeSources first to get collection names
        TypeSources = DataSourcesById
            .Values
            .SelectMany(ds => ds.TypeSources)
            .ToDictionary(x => x.CollectionName);
        TypeSourcesByType = DataSourcesById.Values.SelectMany(ds => ds.TypeSources).ToDictionary(ts => ts.TypeDefinition.Type);

        // Register types with TypeRegistry BEFORE creating DataSourcesByCollection
        // This ensures GetCollectionName returns the correct collection name
        foreach (var typeSource in TypeSources.Values)
        {
            logger.LogDebug("DataContext: Registering type {Type} with collection name {CollectionName}",
                typeSource.TypeDefinition.Type.Name, typeSource.TypeDefinition.CollectionName);
            TypeRegistry.WithType(typeSource.TypeDefinition.Type, typeSource.TypeDefinition.CollectionName);
        }

        DataSourcesByType = DataSourcesById.Values
            .SelectMany(ds => ds.MappedTypes.Select(t => new KeyValuePair<Type, IDataSource>(t, ds))).ToDictionary();
        logger.LogDebug("DataContext: DataSourcesByType has {Count} entries: {Types}",
            DataSourcesByType.Count, string.Join(", ", DataSourcesByType.Keys.Select(t => t.Name)));
        DataSourcesByCollection = DataSourcesByType.Select(kvp =>
        {
            var collectionName = TypeRegistry.GetCollectionName(kvp.Key);
            logger.LogTrace("DataContext: Type {Type} -> CollectionName {CollectionName}",
                kvp.Key.Name, collectionName ?? "NULL");
            return new KeyValuePair<string, IDataSource>(collectionName!, kvp.Value);
        }).ToDictionary();
        logger.LogDebug("DataContext: DataSourcesByCollection has {Count} entries: {Collections}",
            DataSourcesByCollection.Count, string.Join(", ", DataSourcesByCollection.Keys));

        // Initialize each data source
        foreach (var dataSource in DataSourcesById.Values)
        {
            dataSource.Initialize();
            tasks.Add(dataSource.Initialized);
            initialized.Add(dataSource.Reference);
        }

        logger.LogDebug("DataContext initialization setup complete for {Address}, waiting for OpenInitializationGate", Hub.Address);
    }

    /// <summary>
    /// Opens the initialization gate after all message handlers are registered.
    /// Called via SyncBuildupActions to ensure proper ordering.
    /// </summary>
    internal void OpenInitializationGate()
    {
        Task.WhenAll(tasks)
            .ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    logger.LogError(task.Exception, "DataContext initialization failed for {Address}", Hub.Address);
                    // Propagate initialization failure to all data source streams
                    var initException = new InvalidOperationException(
                        $"Hub '{Hub.Address}' initialization failed", task.Exception);
                    foreach (var ds in DataSources)
                    {
                        try
                        {
                            var stream = ds.GetStreamForPartition(null);
                            stream?.OnError(initException);
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Error propagating init failure to data source {Id}", ds.Id);
                        }
                    }
                }
                else if (task.IsCanceled)
                {
                    logger.LogWarning("DataContext initialization was canceled for {Address}", Hub.Address);
                }
                else
                {
                    logger.LogDebug("Finished initialization of DataContext for {Address}", Hub.Address);
                }

                // Always open the gate so the hub can process messages.
                // On failure/cancellation, streams already have errors propagated;
                // keeping the gate closed would hang the hub forever.
                logger.LogWarning("DataContext: Opening {GateName} gate for {Address} (IsFaulted={IsFaulted}, IsCanceled={IsCanceled})",
                    InitializationGateName, Hub.Address, task.IsFaulted, task.IsCanceled);
                Hub.OpenGate(InitializationGateName);
            }, TaskScheduler.Default);
    }

    public IEnumerable<Type> MappedTypes => DataSourcesByType.Keys;
    private readonly List<Task> tasks = new();
    private readonly List<WorkspaceReference> initialized = new();
    public void Dispose()
    {
        foreach (var dataSource in DataSourcesById.Values)
        {
            dataSource.Dispose();
        }
    }

    public string? GetCollectionName(Type type)
        => TypeSourcesByType.GetValueOrDefault(type)?.CollectionName;
}

/// <summary>
/// Handler delegate for virtual data paths.
/// Takes the workspace and optional entity ID, returns an observable stream.
/// </summary>
/// <param name="workspace">The workspace context</param>
/// <param name="entityId">Optional entity ID from the path (e.g., "O1" from "OrderSummary/O1")</param>
/// <returns>An observable stream of the computed data</returns>
public delegate IObservable<object?> VirtualPathHandler(IWorkspace workspace, string? entityId);

/// <summary>
/// Resolver delegate for unified reference paths.
/// Takes the path (without prefix) and returns a synchronization stream, or null if not handled.
/// </summary>
/// <param name="workspace">The workspace context</param>
/// <param name="path">The path after the prefix (e.g., "collection/entity" from "data:addressType/addressId/collection/entity")</param>
/// <returns>A synchronization stream if handled, null otherwise</returns>
public delegate ISynchronizationStream<object>? UnifiedReferenceResolver(
    IWorkspace workspace,
    string? path);

/// <summary>
/// Extensions for DataContext to support virtual data sources and default data references
/// </summary>
public static class DataContextExtensions
{
    /// <summary>
    /// Adds a virtual data source to the data context.
    /// Virtual data sources compute their data from streams rather than storing it directly.
    /// </summary>
    /// <param name="dataContext">The data context to extend</param>
    /// <param name="id">Unique identifier for the virtual data source</param>
    /// <param name="configure">Configuration function to set up the virtual data source</param>
    /// <returns>Updated data context</returns>
    public static DataContext WithVirtualDataSource(
        this DataContext dataContext,
        object id,
        Func<VirtualDataSource, VirtualDataSource> configure
    )
    {
        return dataContext.WithDataSource(_ =>
        {
            var virtualDataSource = new VirtualDataSource(id, dataContext.Workspace);
            return configure(virtualDataSource);
        });
    }

    /// <summary>
    /// Configures the default data reference for this context.
    /// The default data reference is used when accessing data via data:addressType/addressId
    /// without specifying a collection name.
    /// </summary>
    /// <typeparam name="T">The type of data to return</typeparam>
    /// <param name="dataContext">The data context to configure</param>
    /// <param name="factory">Factory function that creates an observable for the default data</param>
    /// <returns>Updated data context with the default data reference configured</returns>
    /// <example>
    /// <code>
    /// .AddData(data => data
    ///     .AddSource(src => src.WithType&lt;Pricing&gt;(...))
    ///     .WithDefaultDataReference(workspace =>
    ///         workspace.GetObservable&lt;Pricing&gt;().Select(p => p.FirstOrDefault()))
    /// )
    /// </code>
    /// </example>
    public static DataContext WithDefaultDataReference<T>(
        this DataContext dataContext,
        Func<IWorkspace, IObservable<T?>> factory)
    {
        return dataContext with
        {
            DefaultDataReferenceFactory = workspace =>
                factory(workspace).Select(x => (object?)x)
        };
    }

    /// <summary>
    /// Registers a content provider that maps a collection name in data paths to a content collection.
    /// This enables accessing files via data:addressType/addressId/collection/path patterns.
    /// </summary>
    /// <param name="dataContext">The data context to configure</param>
    /// <param name="collectionName">The collection name used in data paths (e.g., "Submissions")</param>
    /// <param name="contentCollectionName">The actual content collection name to use (optional, defaults to collectionName)</param>
    /// <returns>Updated data context with the content provider configured</returns>
    /// <example>
    /// <code>
    /// .AddData(data => data
    ///     .AddSource(...)
    ///     .WithContentProvider("Submissions")  // Maps data:pricing/id/Submissions/file.xlsx to Submissions collection
    /// )
    /// </code>
    /// </example>
    public static DataContext WithContentProvider(
        this DataContext dataContext,
        string collectionName,
        string? contentCollectionName = null)
    {
        return dataContext with
        {
            ContentProviders = dataContext.ContentProviders.Add(
                collectionName,
                contentCollectionName ?? collectionName)
        };
    }

    /// <summary>
    /// Registers a virtual path handler that computes data from multiple streams.
    /// Virtual paths allow custom data paths like "OrderSummary" or "OrderSummary/O1"
    /// that resolve to computed/joined data from the workspace.
    /// </summary>
    /// <param name="dataContext">The data context to configure</param>
    /// <param name="pathPrefix">The path prefix to match (e.g., "OrderSummary")</param>
    /// <param name="handler">Handler function that returns an observable stream for the path</param>
    /// <returns>Updated data context with the virtual path configured</returns>
    /// <example>
    /// <code>
    /// .AddData(data => data
    ///     .AddSource(src => src.WithType&lt;Order&gt;(...))
    ///     .AddSource(src => src.WithType&lt;Customer&gt;(...))
    ///     .WithVirtualPath("OrderSummary", (workspace, entityId) =>
    ///     {
    ///         var orders = workspace.GetStream(typeof(Order));
    ///         var customers = workspace.GetStream(typeof(Customer));
    ///
    ///         return Observable.CombineLatest(orders, customers, (o, c) =>
    ///         {
    ///             // Join orders with customers
    ///             var result = JoinOrdersWithCustomers(o, c);
    ///             // If entityId specified, return single entity
    ///             return entityId != null
    ///                 ? result.FirstOrDefault(x => x.Id == entityId)
    ///                 : result;
    ///         });
    ///     })
    /// )
    /// </code>
    /// </example>
    public static DataContext WithVirtualPath(
        this DataContext dataContext,
        string pathPrefix,
        VirtualPathHandler handler)
    {
        return dataContext with
        {
            VirtualPaths = dataContext.VirtualPaths.Add(pathPrefix, handler)
        };
    }

    /// <summary>
    /// Registers a virtual path handler with a simpler signature for collection-only paths.
    /// Use this when the path doesn't need entity-level resolution.
    /// </summary>
    /// <param name="dataContext">The data context to configure</param>
    /// <param name="pathPrefix">The path prefix to match (e.g., "OrderSummary")</param>
    /// <param name="handler">Handler function that returns an observable stream</param>
    /// <returns>Updated data context with the virtual path configured</returns>
    public static DataContext WithVirtualPath(
        this DataContext dataContext,
        string pathPrefix,
        Func<IWorkspace, IObservable<object?>> handler)
    {
        return dataContext.WithVirtualPath(pathPrefix, (workspace, _) => handler(workspace));
    }

    /// <summary>
    /// Registers a unified reference resolver for a specific prefix.
    /// Resolvers are inserted at position 0 to allow later registrations to override earlier ones.
    /// The first resolver returning non-null wins for that prefix.
    /// </summary>
    /// <param name="dataContext">The data context to configure</param>
    /// <param name="prefix">The prefix to match (e.g., "data", "area", "content")</param>
    /// <param name="resolver">Resolver function that creates a stream for a path, or returns null if not handled</param>
    /// <returns>Updated data context with the resolver registered</returns>
    /// <example>
    /// <code>
    /// .AddData(data => data
    ///     .WithUnifiedReference("data", (workspace, path) =>
    ///     {
    ///         // path is the remaining path after "data:addressType/addressId/"
    ///         // e.g., "collection/entityId"
    ///         return CreateMyStream(workspace, path);
    ///     })
    /// )
    /// </code>
    /// </example>
    public static DataContext WithUnifiedReference(
        this DataContext dataContext,
        string prefix,
        UnifiedReferenceResolver resolver)
    {
        var normalizedPrefix = prefix.TrimEnd(':').ToLowerInvariant();
        var existingResolvers = dataContext.UnifiedReferenceResolvers.GetValueOrDefault(normalizedPrefix)
            ?? ImmutableList<UnifiedReferenceResolver>.Empty;

        return dataContext with
        {
            UnifiedReferenceResolvers = dataContext.UnifiedReferenceResolvers.SetItem(
                normalizedPrefix,
                existingResolvers.Insert(0, resolver))
        };
    }

    /// <summary>
    /// Adds a global access restriction that applies to all data operations.
    /// Global restrictions are evaluated before type-specific restrictions.
    /// </summary>
    /// <param name="dataContext">The data context to configure</param>
    /// <param name="restriction">Async restriction delegate to evaluate</param>
    /// <param name="name">Optional name for logging/debugging</param>
    /// <returns>Updated data context with the restriction added</returns>
    /// <example>
    /// <code>
    /// .AddData(data => data
    ///     .WithAccessRestriction(
    ///         (action, ctx, accessCtx) =>
    ///         {
    ///             // Require authentication for all write operations
    ///             if (action == AccessAction.Read)
    ///                 return Task.FromResult(true);
    ///             return Task.FromResult(accessCtx.UserContext != null);
    ///         },
    ///         "RequireAuthentication")
    ///     .AddSource(...)
    /// )
    /// </code>
    /// </example>
    public static DataContext WithAccessRestriction(
        this DataContext dataContext,
        AccessRestrictionDelegate restriction,
        string? name = null)
    {
        return dataContext with
        {
            GlobalAccessRestrictions = dataContext.GlobalAccessRestrictions.Add(
                new AccessRestrictionEntry(restriction, name))
        };
    }

    /// <summary>
    /// Adds a global access restriction using a synchronous delegate.
    /// </summary>
    /// <param name="dataContext">The data context to configure</param>
    /// <param name="restriction">Sync restriction delegate to evaluate</param>
    /// <param name="name">Optional name for logging/debugging</param>
    /// <returns>Updated data context with the restriction added</returns>
    public static DataContext WithAccessRestriction(
        this DataContext dataContext,
        Func<string, object, AccessRestrictionContext, bool> restriction,
        string? name = null)
    {
        return dataContext.WithAccessRestriction(
            (action, ctx, accessCtx, _) => Task.FromResult(restriction(action, ctx, accessCtx)),
            name);
    }
}
