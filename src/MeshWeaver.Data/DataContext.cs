using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
    /// <see cref="InitializationGateName"/> closed forever (the 2026-06-26 prod
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

        // Build TypeSources first to get collection names.
        //
        // 🚨 NEVER .ToDictionary() here. A duplicate collection name is a real configuration
        // defect and it FAILS HUB CREATION — but Dictionary.Add's message is the single word
        // that collided ("An item with the same key has already been added. Key: Approval"),
        // naming neither the hub whose workspace was being built nor either contributor. Four
        // separate production/CI reports of Systemorph/MeshWeaver#1684 each had to be
        // reverse-engineered from exactly that. Say what collided, on which node, and who
        // contributed it — the failure stays a failure (a DistinctBy would HIDE a real
        // duplicate), it just becomes a one-line diagnosis.
        var typeSourcesByCollection = new Dictionary<string, ITypeSource>();
        var typeSourcesByType = new Dictionary<Type, ITypeSource>();
        var collectionContributors = new Dictionary<string, IDataSource>();
        var typeContributors = new Dictionary<Type, IDataSource>();
        foreach (var dataSource in DataSourcesById.Values)
        {
            foreach (var typeSource in dataSource.TypeSources)
            {
                var collectionName = typeSource.CollectionName;
                if (typeSourcesByCollection.TryGetValue(collectionName, out var clashingCollection))
                    throw DuplicateRegistration(
                        "collection", collectionName,
                        collectionContributors[collectionName], clashingCollection,
                        dataSource, typeSource);
                typeSourcesByCollection[collectionName] = typeSource;
                collectionContributors[collectionName] = dataSource;

                var entityType = typeSource.TypeDefinition.Type;
                if (typeSourcesByType.TryGetValue(entityType, out var clashingType))
                    throw DuplicateRegistration(
                        "entity type", entityType.FullName ?? entityType.Name,
                        typeContributors[entityType], clashingType,
                        dataSource, typeSource);
                typeSourcesByType[entityType] = typeSource;
                typeContributors[entityType] = dataSource;
            }
        }
        TypeSources = typeSourcesByCollection;
        TypeSourcesByType = typeSourcesByType;

        // Register types with TypeRegistry BEFORE creating DataSourcesByCollection
        // This ensures GetCollectionName returns the correct collection name
        foreach (var typeSource in TypeSources.Values)
        {
            logger.LogDebug("DataContext: Registering type {Type} with collection name {CollectionName}",
                typeSource.TypeDefinition.Type.Name, typeSource.TypeDefinition.CollectionName);
            TypeRegistry.WithType(typeSource.TypeDefinition.Type, typeSource.TypeDefinition.CollectionName);
        }

        // Same contract for the data-source lookups: a type or a collection claimed by two data
        // sources is a defect that must NAME both claimants, not just the key.
        var dataSourcesByType = new Dictionary<Type, IDataSource>();
        var dataSourcesByCollection = new Dictionary<string, IDataSource>();
        foreach (var dataSource in DataSourcesById.Values)
        {
            foreach (var mappedType in dataSource.MappedTypes)
            {
                if (dataSourcesByType.TryGetValue(mappedType, out var owner))
                    throw DuplicateDataSource(
                        "entity type", mappedType.FullName ?? mappedType.Name, owner, dataSource);
                dataSourcesByType[mappedType] = dataSource;

                var collectionName = TypeRegistry.GetCollectionName(mappedType);
                logger.LogTrace("DataContext: Type {Type} -> CollectionName {CollectionName}",
                    mappedType.Name, collectionName ?? "NULL");
                if (collectionName is null)
                    throw Fail(
                        $"Data source '{Describe(dataSource)}' maps entity type "
                        + $"'{mappedType.FullName ?? mappedType.Name}' but the type registry of hub "
                        + $"'{Hub.Address}' resolves no collection name for it. Register the type on the "
                        + "data source (WithType<T>(...)) so its collection name is known.");
                if (dataSourcesByCollection.TryGetValue(collectionName, out var collectionOwner))
                    throw DuplicateDataSource(
                        "collection", collectionName, collectionOwner, dataSource);
                dataSourcesByCollection[collectionName] = dataSource;
            }
        }
        DataSourcesByType = dataSourcesByType;
        logger.LogDebug("DataContext: DataSourcesByType has {Count} entries: {Types}",
            DataSourcesByType.Count, string.Join(", ", DataSourcesByType.Keys.Select(t => t.Name)));
        DataSourcesByCollection = dataSourcesByCollection;
        logger.LogDebug("DataContext: DataSourcesByCollection has {Count} entries: {Collections}",
            DataSourcesByCollection.Count, string.Join(", ", DataSourcesByCollection.Keys));

        logger.LogDebug("DataContext configuration complete for {Address}, waiting for InitializeDataSources", Hub.Address);
    }

    /// <summary>
    /// Starts each data source — the half of initialization that CREATES THINGS, split out of
    /// <see cref="Initialize"/> so it can run OFF the hub's <c>Build</c> (#1868).
    ///
    /// <para>🚨 The split is the point, and it is not cosmetic. <see cref="Initialize"/> is pure
    /// configuration — resolve the data sources, build the type sources, and REGISTER THEIR TYPES
    /// with the hub's <c>ITypeRegistry</c> — and it must keep running inside <c>Build</c>, because a
    /// caller that resolves the hub can read <c>TypeRegistry</c> the instant <c>Build</c> returns
    /// (schema generation, content-discriminator validation). This method is the other half:
    /// <c>IDataSource.Initialize</c> opens streams (<c>HubDataSource</c> eagerly calls
    /// <c>GetStream</c> → <c>SynchronizationStream..ctor</c> → <c>GetHostedHub(sync/…)</c>), so
    /// running it inside <c>Build</c> is what made every data-enabled hub construct a sub-hub
    /// inside its own construction.</para>
    ///
    /// <para>Idempotent: a second call is a no-op, so a configurator that registers the observable
    /// init twice cannot double-start the sources.</para>
    /// </summary>
    public void InitializeDataSources()
    {
        if (dataSourcesInitialized)
            return;
        dataSourcesInitialized = true;

        // 🚨 A transient probe hub never STARTS its sources. It is built purely so the registry
        // maps written by Initialize (inside Build) can be read, and it is disposed in the same
        // breath — so the eager GetStream here (a sync/ sub-hub per source, on the init turn)
        // races that dispose, and when dispose wins HostedHubsCollection logs its "Rejecting
        // hosted hub creation … during disposal" warning: the exact line ProbeHubCostTest pins
        // as a teardown fault, flaking CI red twice on 2026-08-22 and switching CD off both
        // times. Lazy stream creation on a real request is untouched. See TransientNodeProbe.
        if (Hub.Configuration.Get<TransientNodeProbe>() is { StartDataSources: false })
        {
            logger.LogDebug(
                "DataContext: {Address} is a transient node probe — not starting its {Count} data "
                + "sources (streams stay lazy)", Hub.Address, DataSourcesById.Count);
            return;
        }

        foreach (var dataSource in DataSourcesById.Values)
        {
            dataSource.Initialize();
            tasks.Add(dataSource.Initialized);
            initialized.Add(dataSource.Reference);
        }

        logger.LogDebug("DataContext data sources started for {Address}, waiting for OpenInitializationGate", Hub.Address);
    }

    private bool dataSourcesInitialized;

    /// <summary>
    /// Identifies a data source in a diagnostic: its id plus the implementation type, which
    /// together are what a reader needs to find the registration that produced it.
    /// </summary>
    private static string Describe(IDataSource dataSource) =>
        $"{dataSource.Id} ({dataSource.GetType().Name})";

    /// <summary>
    /// Identifies a type source in a diagnostic by its entity type AND the assembly that type
    /// comes from — the closest thing the framework can know to "which module contributed this",
    /// since the contribution itself is an anonymous configuration lambda.
    /// </summary>
    private static string Describe(ITypeSource typeSource)
    {
        var type = typeSource.TypeDefinition.Type;
        return $"{type.FullName ?? type.Name} from {type.Assembly.GetName().Name ?? "(unknown assembly)"}";
    }

    /// <summary>
    /// The diagnostic for two <see cref="ITypeSource"/>s claiming one key. Names the key, the NODE
    /// whose hub was being created, and both contributors — see the remarks at the
    /// <see cref="Initialize"/> call site for why a bare <c>ToDictionary</c> is banned here.
    /// </summary>
    private Exception DuplicateRegistration(
        string keyKind,
        string key,
        IDataSource firstDataSource,
        ITypeSource firstTypeSource,
        IDataSource secondDataSource,
        ITypeSource secondTypeSource) =>
        Fail(
            $"Duplicate {keyKind} '{key}' while building the workspace of node '{Hub.Address}'. "
            + $"It is claimed by data source '{Describe(firstDataSource)}' → {Describe(firstTypeSource)} "
            + $"AND by data source '{Describe(secondDataSource)}' → {Describe(secondTypeSource)}. "
            + $"A {keyKind} may be registered only once per hub. The usual cause is a hub-configuration "
            + "lambda that is applied twice and is not idempotent — give its data source a STABLE id so "
            + "the keep-last-by-id dedupe above collapses the second application (a fresh Guid defeats "
            + "it), and make sure nothing composes the same configuration chain twice. The other cause "
            + "is two modules genuinely claiming the same name; rename one.");

    /// <summary>
    /// The diagnostic for two data sources claiming one key (entity type / collection).
    /// </summary>
    private Exception DuplicateDataSource(
        string keyKind, string key, IDataSource first, IDataSource second) =>
        Fail(
            $"Duplicate {keyKind} '{key}' while building the workspace of node '{Hub.Address}'. "
            + $"Data sources '{Describe(first)}' and '{Describe(second)}' both claim it. "
            + $"A {keyKind} may be owned by only one data source per hub.");

    /// <summary>
    /// Logs at Error (so the diagnostic reaches the log pipeline even where the exception is
    /// swallowed by a hub-creation catch) and returns the exception for the caller to throw.
    /// </summary>
    private Exception Fail(string message)
    {
        logger.LogError("DataContext initialization failed for {Address}: {Message}",
            Hub.Address, message);
        return new InvalidOperationException(message);
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
        // prod wedge). Time-box it so a hang reaches the SAME terminal failed state
        // as a fault — mirroring MessageHub.HandleInitialize's .Timeout(StartupTimeout).
        //
        // 🚨 The wait is a SUBSCRIPTION, never a Task-shaped timer race (#2528; the
        // disposal twin is #2488). This replaced Task.WhenAny(allInit, Task.Delay(…))
        // + ContinueWith + a watchdog CancellationTokenSource. Amb over three one-shot
        // arms; whichever fires first settles the gate, and the outcome is read off
        // allInit's ACTUAL state at settle time (same discrimination the old shape did):
        //
        //   1. init settled — allInit bridged Task→IObservable (the sanctioned
        //      direction) and Materialize'd, so a FAULTED or CANCELED init still RUNS
        //      the settle body instead of skipping past it to an error arm;
        //   2. the time-box — Observable.Timer(InitializationTimeout). The bound itself
        //      is deliberate and STAYS: deleting it re-opens the 2026-06-26 wedge above.
        //      Its expiry fails FAST and LOUDLY (fail-level log + InitializationError +
        //      rejection handler) — never "proceed as if initialized";
        //   3. the disarm — fired by Dispose(). 🚨 Disarm means FIRE NOW, not
        //      unsubscribe (an Amb arm, never a TakeUntil): the settle body must still
        //      run so the gate gets its terminal ANSWER — returning without it strands
        //      every deferred delivery (#1270) — while Hub.IsShuttingDown routes it to
        //      the recognized-shutdown outcome that stamps no post-mortem FAILED
        //      residue (#1122). The AsyncSubject replays its terminal, so a Dispose
        //      that ran BEFORE this gate armed (a probe hub torn down in the same
        //      breath it was built) settles immediately instead of waiting out the
        //      full time-box — the old CTS shape armed a fresh, never-cancelled timer
        //      in that ordering.
        //
        // Take(1): every arm yields exactly one value and the first unsubscribes the
        // rest — the losing timer is disposed with its subscription, so nothing roots
        // this context and the subscription need not be stored. ObserveOn: the settle
        // body must not run inline on the last data source's completing thread (arm 1)
        // nor on Dispose()'s teardown stack (arm 3) — the same hop the previous
        // ContinueWith(TaskScheduler.Default) gave it.
        var initSettled = allInit.ToObservable().Materialize().Take(1).Select(_ => Unit.Default);
        var timeBox = Observable.Timer(InitializationTimeout).Select(_ => Unit.Default);
        Observable.Amb(initSettled, timeBox, watchdogDisarm)
            .Take(1)
            .ObserveOn(TaskPoolScheduler.Default)
            .Subscribe(
                _ => SettleInitializationGate(allInit),
                ex =>
                {
                    // Fluent error arm — a try/catch around Subscribe would see nothing
                    // (the fault arrives later, on another thread). None of the three
                    // arms can OnError (arm 1 is Materialize'd), so this is a
                    // scheduler-level fault: log it AND still settle, because the one
                    // unacceptable outcome is a gate that never gets its answer.
                    logger.LogError(ex,
                        "DataContext init watchdog stream faulted for {Address} — settling the gate anyway.",
                        Hub.Address);
                    SettleInitializationGate(allInit);
                });
    }

    /// <summary>
    /// The init watchdog's terminal body: discriminates shutdown / timed-out / faulted /
    /// canceled / clean off <paramref name="allInit"/>'s state at settle time, records the
    /// outcome, and always ANSWERS the gate (<see cref="IMessageHub.FailGate"/> on shutdown,
    /// <see cref="IMessageHub.OpenGate"/> otherwise). Runs exactly once, whichever watchdog
    /// arm fired first — see <see cref="OpenInitializationGate"/>.
    /// </summary>
    private void SettleInitializationGate(Task allInit)
    {
        // Recognized shutdown, NOT a failure: the hub was disposed (or its subtree
        // frozen by an ancestor's disposal) while its data sources were still
        // initializing. The disposal pipeline already answers all traffic
        // (ErrorType.ShuttingDown); recording InitializationError / erroring the data
        // streams / registering a rejection handler here would stamp fail-level FAILED
        // residue onto a hub that is already gone.
        if (Hub.IsShuttingDown)
        {
            logger.LogDebug(
                "DataContext initialization for {Address} ended by hub disposal — recognized "
                + "shutdown outcome, no failure state recorded.", Hub.Address);
            // 🚨 …but "no failure state" must never mean "no answer". Returning here used
            // to leave InitializationGateName SHUT FOREVER — shutdown is precisely the
            // outcome after which nothing can ever open it — and every delivery already
            // parked behind it (plus every one that arrives during the remaining teardown,
            // which can be the full QuiesceTimeout) was stranded. The sender's
            // hub.Observe(...) then heard nothing until an unrelated deadline expired: the
            // 30 s per-message deferral timeout, or whenever the teardown finally reached
            // messageService.Dispose(). Observed in production shape as a CreateNodeRequest
            // recorded `DEFERRED gates=[DataContextInit]` at runLevel=Quiescing that never
            // drained (issue #1270; the SkillAutocompleteTest teardown hang #1269 fixed at
            // the CALLER — the gate itself still stranded anything that parked there).
            //
            // FailGate is the terminal ANSWER, not a timeout: shutdown is a KNOWN outcome,
            // so every deferred caller gets a transient DeliveryFailure ("the address may
            // reactivate; retry") immediately. It records no InitializationError, no
            // fail-level log and no errored data streams — the post-mortem FAILED residue
            // #1122 removed stays removed.
            Hub.FailGate(InitializationGateName, ShutdownNack.RetryForTheAuthoritativeAnswer(
                Hub.Address,
                null,
                $"its DataContext initialization ended without opening "
                + $"'{InitializationGateName}', which can therefore never open"));
            return;
        }

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
    // The init watchdog's DISARM arm (issue #1122): fired by Dispose so a hub disposed
    // mid-init settles the gate NOW, through the recognized-shutdown branch that stamps no
    // FAILED residue — instead of leaving a live timer whose continuation fires minutes
    // after the hub is gone. An AsyncSubject replays its terminal to late subscribers, so a
    // Dispose that runs BEFORE OpenInitializationGate arms the watchdog still disarms it.
    // (Record with-ers copy the reference at config time; a DataContext is created per hub
    // and only the final instance ever arms/disarms, so sharing across with-copies is
    // harmless — same reasoning the CTS this replaced documented.)
    private readonly AsyncSubject<Unit> watchdogDisarm = new();
    /// <summary>Disposes every configured data source and disarms the init watchdog.</summary>
    public void Dispose()
    {
        // Disarm the init watchdog FIRST: DataContext.Dispose only runs during hub teardown
        // (Workspace.Dispose), so from here on "init did not complete" is a shutdown outcome,
        // not a timeout. Firing the arm settles the watchdog's Amb immediately; the settle
        // body observes Hub.IsShuttingDown and records nothing (#1122) while still ANSWERING
        // the gate (#1270). Idempotent: an AsyncSubject ignores notifications after its
        // terminal, so a racing second Dispose is a no-op.
        watchdogDisarm.OnNext(Unit.Default);
        watchdogDisarm.OnCompleted();

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
