using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data.Validation;
using MeshWeaver.Domain;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Data;

/// <summary>
/// Configuration and runtime registry of a workspace's data sources, type sources and reduce
/// manager. Built up immutably via the With* methods, then <see cref="Initialize"/> wires up every
/// configured source and registers their types.
/// </summary>
public sealed record DataContext : IDisposable
{
    /// <summary>Name of the message-hub gate that stays closed until the data context has finished initializing.</summary>
    public const string InitializationGateName = "DataContextInit";

    /// <summary>The type registry that maps CLR types to collection names for this context.</summary>
    public ITypeRegistry TypeRegistry { get; }

    /// <summary>Creates a data context for <paramref name="workspace"/>, wiring its hub, reduce manager and type registry.</summary>
    /// <param name="workspace">The workspace this context belongs to.</param>
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

    /// <summary>
    /// When set, the DataContext is in a failed state and all future SubscribeRequests
    /// should immediately return DeliveryFailure with this error.
    /// </summary>
    public Exception? InitializationError { get; private set; }

    /// <summary>
    /// True while at least one configured data source is still running its
    /// initial load. Display-grade: the layout-area progress milestones read it
    /// to decide whether to show the "Initializing data sources…" phase.
    /// </summary>
    public bool IsInitializing => tasks.Any(t => !t.IsCompleted);

    /// <summary>
    /// Display-grade completion signal for the initial load of every configured
    /// data source. Subscribed by the layout-area progress milestones
    /// ("Initializing data sources…" → "Rendering…"); emits exactly one
    /// <see cref="System.Reactive.Unit"/> when every source's initial load has
    /// settled — successfully OR faulted — then completes. A faulted init still
    /// ENDS the "initializing" phase, which is the only semantic this signal
    /// carries; the failure itself surfaces authoritatively through
    /// <see cref="InitializationError"/> and the data streams' OnError (this is
    /// not an error-handling channel, so nothing is swallowed). Cold: evaluated
    /// per subscription against the current task set.
    /// </summary>
    public IObservable<System.Reactive.Unit> Initialization =>
        Observable.Defer(() => Task.WhenAll(tasks.ToArray())
            .ToObservable()
            .Catch<System.Reactive.Unit, Exception>(_ =>
                Observable.Return(System.Reactive.Unit.Default)));

    private Dictionary<Type, ITypeSource> TypeSourcesByType { get; set; } = new();

    /// <summary>All configured data sources.</summary>
    public IEnumerable<IDataSource> DataSources => DataSourcesById.Values;

    private ImmutableDictionary<object, IDataSource> DataSourcesById { get; set; } =
        ImmutableDictionary<object, IDataSource>.Empty;

    /// <summary>Looks up a configured data source by its id.</summary>
    /// <param name="id">Data source id.</param>
    /// <returns>The matching data source, or null if none is registered.</returns>
    public IDataSource? GetDataSourceForId(object id) => DataSourcesById.GetValueOrDefault(id);

    /// <summary>Finds the data source that owns the given type, walking up the base-type chain if needed.</summary>
    /// <param name="type">Entity type to resolve.</param>
    /// <returns>The owning data source, or null if the type is not mapped.</returns>
    public IDataSource? GetDataSourceForType(Type type) => DataSourcesByType.GetValueOrDefault(type)
          ?? (type.BaseType == typeof(object) || type.BaseType == null ? null : GetDataSourceForType(type.BaseType));

    /// <summary>Map of mapped entity type to the data source that owns it.</summary>
    public IReadOnlyDictionary<Type, IDataSource> DataSourcesByType { get; private set; } = new Dictionary<Type, IDataSource>();
    /// <summary>Map of collection name to the data source that owns it.</summary>
    public IReadOnlyDictionary<string, IDataSource> DataSourcesByCollection { get; private set; } = new Dictionary<string, IDataSource>();

    /// <summary>Returns a copy of this context with an additional data source builder.</summary>
    /// <param name="dataSourceBuilder">Builder that creates the data source from a hub.</param>
    /// <returns>A new context including the builder.</returns>
    public DataContext WithDataSource(DataSourceBuilder dataSourceBuilder) =>
        this with { DataSourceBuilders = DataSourceBuilders.Add(dataSourceBuilder), };

    /// <summary>Map of collection name to the type source backing it, populated during initialization.</summary>
    public IReadOnlyDictionary<string, ITypeSource> TypeSources { get; private set; } = new Dictionary<string, ITypeSource>();

    /// <summary>Looks up the type source for a collection name.</summary>
    /// <param name="collection">Collection name.</param>
    /// <returns>The type source, or null if the collection is unknown.</returns>
    public ITypeSource? GetTypeSource(string collection) =>
        TypeSources.GetValueOrDefault(collection);

    /// <summary>Finds the type source for a CLR type, walking up the base-type chain if needed.</summary>
    /// <param name="type">Entity type to resolve.</param>
    /// <returns>The type source, or null if the type is not mapped.</returns>
    public ITypeSource? GetTypeSource(Type type) =>
        TypeSourcesByType.GetValueOrDefault(type)
        ?? (type.BaseType == typeof(object) || type.BaseType == null ? null : GetTypeSource(type.BaseType));


    /// <summary>The data source builders registered on this context; invoked during <see cref="Initialize"/>.</summary>
    public ImmutableList<DataSourceBuilder> DataSourceBuilders { get; set; } =
        ImmutableList<DataSourceBuilder>.Empty;

    internal ReduceManager<EntityStore> ReduceManager { get; init; }

    /// <summary>
    /// Upper bound on DataContext initialization, consumed by
    /// <see cref="OpenInitializationGate"/>. A data-source init that HANGS (e.g. a
    /// stuck NodeType/scope Roslyn compile, or a dependency that never initialises)
    /// trips this and drives the hub to a terminal FAILED state instead of leaving
    /// <see cref="InitializationGateName"/> closed forever (the 2026-06-26 atioz
    /// wedge). Defaults to <c>120s</c> — the same budget top-level hubs get via
    /// <c>MessageHub.DefaultInitializationTimeout</c> — and is overridable per
    /// context via <see cref="WithInitializationTimeout"/> (tests set it short).
    /// </summary>
    internal TimeSpan InitializationTimeout { get; set; } = TimeSpan.FromSeconds(120);
    /// <summary>The message hub that owns this context's workspace.</summary>
    public IMessageHub Hub { get; }
    /// <summary>The workspace this context belongs to.</summary>
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

    /// <summary>Returns a copy of this context with the given initial-load timeout.</summary>
    /// <param name="timeout">Maximum time to wait for data sources to finish their initial load.</param>
    /// <returns>A new context with the timeout applied.</returns>
    public DataContext WithInitializationTimeout(TimeSpan timeout) =>
        this with { InitializationTimeout = timeout };

    /// <summary>Returns a copy of this context with its reduce manager transformed by <paramref name="change"/>.</summary>
    /// <param name="change">Function that augments the entity-store reduce manager.</param>
    /// <returns>A new context with the updated reduce manager.</returns>
    public DataContext Configure(
        Func<ReduceManager<EntityStore>, ReduceManager<EntityStore>> change
    ) => this with { ReduceManager = change.Invoke(ReduceManager) };

    /// <summary>Factory that builds an <see cref="IDataSource"/> from the owning hub.</summary>
    /// <param name="hub">The hub the data source will run on.</param>
    /// <returns>The constructed data source.</returns>
    public delegate IDataSource DataSourceBuilder(IMessageHub hub);

    /// <summary>
    /// Builds every configured data source, registers their types with the type registry, populates the
    /// collection and type lookups, and starts each source's initial load.
    /// </summary>
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
        var allInit = Task.WhenAll(tasks);

        // Observe any eventual fault of a still-running init so a late completion
        // (after we've already failed the gate on timeout below) never surfaces as
        // an UnobservedTaskException.
        _ = allInit.ContinueWith(static t => { _ = t.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

        // Bound the wait. The IsFaulted branch below already gives a THROWN init the
        // wedges-to-zero treatment (fail fast → reject subsequent requests). A HUNG
        // init (Task.WhenAll never completing) had NO such bound: the gate never
        // opened and every subsequent message deferred → NACKed → resubscribed
        // forever, a path-resolution storm that GC-thrashed the portal (2026-06-26
        // atioz wedge). Time-box it so a hang reaches the SAME terminal failed state
        // as a fault — mirroring MessageHub.HandleInitialize's .Timeout(StartupTimeout).
        Task.WhenAny(allInit, Task.Delay(InitializationTimeout))
            .ContinueWith(_ =>
            {
                Exception? failure = null;
                if (!allInit.IsCompleted)
                {
                    failure = new TimeoutException(
                        $"Hub '{Hub.Address}' DataContext initialization did not complete within "
                        + $"{InitializationTimeout.TotalSeconds:F0}s — likely a stuck NodeType compile, "
                        + "or a data source that never initialised.");
                    logger.LogError(failure,
                        "DataContext initialization TIMED OUT for {Address}. Hub is now in FAILED state.", Hub.Address);
                }
                else if (allInit.IsFaulted)
                {
                    failure = new InvalidOperationException(
                        $"Hub '{Hub.Address}' initialization failed", allInit.Exception);
                    logger.LogError(allInit.Exception,
                        "DataContext initialization failed for {Address}. Hub is now in FAILED state.", Hub.Address);
                }
                else if (allInit.IsCanceled)
                {
                    logger.LogWarning("DataContext initialization was canceled for {Address}", Hub.Address);
                }
                else
                {
                    logger.LogDebug("Finished initialization of DataContext for {Address}", Hub.Address);
                }

                if (failure is not null)
                {
                    InitializationError = failure;

                    // Register a global rejection handler for all data requests: every
                    // subsequent request to this hub gets an immediate DeliveryFailure,
                    // so callers (and the MeshNodeStreamCache negative cache) get a
                    // TERMINAL answer and stop re-subscribing — never the 30s-defer loop.
                    RegisterInitializationFailureHandler(failure);

                    // Also propagate to existing data source streams.
                    foreach (var ds in DataSources)
                    {
                        try
                        {
                            var stream = ds.GetStreamForPartition(null);
                            stream?.OnError(failure);
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "Error propagating init failure to data source {Id}", ds.Id);
                        }
                    }
                }

                // Always open the gate so the hub can process messages.
                // On failure/timeout, streams already have errors propagated and the
                // rejection handler is registered; keeping the gate closed would hang
                // the hub forever.
                logger.LogDebug("DataContext: Opening {GateName} gate for {Address} (failed={Failed})",
                    InitializationGateName, Hub.Address, failure is not null);
                Hub.OpenGate(InitializationGateName);
            }, TaskScheduler.Default);
    }

    /// <summary>
    /// Registers a global handler on the hub that rejects all incoming requests
    /// with a DeliveryFailure when initialization has failed.
    /// Skips DeliveryFailure messages themselves to avoid loops.
    /// </summary>
    private void RegisterInitializationFailureHandler(Exception initException)
    {
        var errorMessage = $"Hub '{Hub.Address}' initialization failed: {initException.Message}";
        Hub.Register(delivery =>
        {
            if (delivery.Message is DeliveryFailure)
                return delivery;

            logger.LogWarning("Hub {Hub} is in FAILED state. Rejecting {MessageType} from {Sender}: {Error}",
                Hub.Address, delivery.Message.GetType().Name, delivery.Sender, errorMessage);
            Hub.Post(new DeliveryFailure(delivery)
            {
                ErrorType = ErrorType.Failed,
                Message = errorMessage
            }, o => o.ResponseFor(delivery));
            return delivery.Processed();
        });
    }

    /// <summary>All entity types mapped by the configured data sources.</summary>
    public IEnumerable<Type> MappedTypes => DataSourcesByType.Keys;
    private readonly List<Task> tasks = new();
    private readonly List<WorkspaceReference> initialized = new();
    /// <summary>Disposes every configured data source.</summary>
    public void Dispose()
    {
        foreach (var dataSource in DataSourcesById.Values)
        {
            dataSource.Dispose();
        }
    }

    /// <summary>Returns the collection name for a mapped type, or null if the type is not mapped.</summary>
    /// <param name="type">Entity type.</param>
    /// <returns>The collection name, or null.</returns>
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
            (action, ctx, accessCtx) => Observable.Return(restriction(action, ctx, accessCtx)),
            name);
    }
}
