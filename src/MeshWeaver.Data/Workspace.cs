using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// The shared mesh-node cache (MeshNodeStreamHandle in MeshWeaver.Mesh.Contract) and the
// MeshNode reduce-callback plumbing (MeshDataSource / SyncedQueryDataSourceExtensions in
// MeshWeaver.Graph) are the ONLY sanctioned callers of the raw single-node remote reduce.
// They open their upstream via the internal GetRemoteStreamUnchecked escape hatch so the
// public GetRemoteStream<MeshNode> path can log its discouraged-usage warning for everyone
// else without spamming it on the sanctioned hot paths.
[assembly: InternalsVisibleTo("MeshWeaver.Mesh.Contract")]
[assembly: InternalsVisibleTo("MeshWeaver.Graph")]
// Framework-internal tests of the raw remote-stream cache identity
// (ReferenceEquals on Workspace._remoteStreamCache) legitimately open the raw
// single-node reduce — they test the mechanism itself, not mesh-node access.
[assembly: InternalsVisibleTo("MeshWeaver.Query.Test")]

namespace MeshWeaver.Data;

/// <summary>
/// Default <see cref="IWorkspace"/> implementation: builds the data context from the hub's
/// configuration, caches remote synchronization streams (evicting them when their owner node
/// changes), and routes reads, writes and disposal through the owning message hub.
/// </summary>
public class Workspace : IWorkspace
{
    private readonly ILogger<Workspace> _logger;
    private readonly IDisposable? _changeFeedSubscription;

    /// <summary>Creates the workspace, builds and initializes its data context, and subscribes to the mesh change feed for remote-stream cache eviction.</summary>
    /// <param name="hub">The message hub that owns this workspace.</param>
    /// <param name="logger">Logger for workspace lifecycle and stream-cache diagnostics.</param>
    public Workspace(IMessageHub hub, ILogger<Workspace> logger)
    {
        Hub = hub;
        _logger = logger;
        logger.LogDebug("Creating data context of address {address}", Id);
        DataContext = this.CreateDataContext();
        logger.LogDebug("Started initialization of data context of address {address}", Id);
        DataContext.Initialize();

        // Evict cached remote streams when their owner node changes (delete, recreate,
        // recycle, content/type update). Without this, a Singleton workspace keeps
        // serving the original snapshot forever — including across Blazor circuit
        // refreshes, since the workspace lives on the singleton mesh hub. The next
        // GetRemoteStream after eviction creates a fresh subscription against the
        // (re-)activated owner and pulls the current persistence state.
        //
        // IMeshChangeFeed lives in MeshWeaver.Mesh.Contract which would create a
        // Data → Mesh.Contract → Layout → Data project cycle. Resolve via reflection
        // and adapt the Subscribe(Action<MeshChangeEvent>, MeshChangeKind?) signature.
        _changeFeedSubscription = TrySubscribeToChangeFeed(hub.ServiceProvider, _logger,
            evtPath => EvictForPath(evtPath));
    }

    private static IDisposable? TrySubscribeToChangeFeed(
        IServiceProvider serviceProvider, ILogger logger, Action<string> onPathChanged)
    {
        try
        {
            var feedType = Type.GetType("MeshWeaver.Mesh.Services.IMeshChangeFeed, MeshWeaver.Mesh.Contract", throwOnError: false);
            if (feedType is null) return null;
            var feed = serviceProvider.GetService(feedType);
            if (feed is null) return null;

            var eventType = Type.GetType("MeshWeaver.Mesh.Services.MeshChangeEvent, MeshWeaver.Mesh.Contract", throwOnError: false);
            if (eventType is null) return null;
            var pathProp = eventType.GetProperty("Path");
            if (pathProp is null) return null;

            // Build a strongly-typed Action<MeshChangeEvent> via a generic helper so the
            // runtime sees the exact delegate signature Subscribe expects.
            var helper = typeof(Workspace).GetMethod(nameof(SubscribeChangeFeedHelper),
                BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(eventType);
            return (IDisposable?)helper.Invoke(null, [feed, pathProp, onPathChanged]);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Workspace failed to subscribe to IMeshChangeFeed — remote stream cache will only invalidate via heartbeat resubscribe.");
            return null;
        }
    }

    private static IDisposable? SubscribeChangeFeedHelper<TEvent>(
        object feed, PropertyInfo pathProperty, Action<string> onPathChanged)
        where TEvent : class
    {
        Action<TEvent> handler = evt =>
        {
            try
            {
                if (pathProperty.GetValue(evt) is string p && !string.IsNullOrEmpty(p))
                    onPathChanged(p);
            }
            catch { /* keep change-feed alive on handler faults */ }
        };
        var subscribe = feed.GetType().GetMethod("Subscribe");
        return (IDisposable?)subscribe!.Invoke(feed, [handler, null]);
    }

    /// <summary>
    /// Drops any cached remote streams whose owner address matches the changed path.
    /// The currently-attached subscribers stay live (they continue to receive
    /// DataChanged events from the source hub for the moment); the eviction only
    /// affects the NEXT GetRemoteStream caller, which will spin up a fresh stream.
    /// </summary>
    private void EvictForPath(string path)
    {
        if (string.IsNullOrEmpty(path) || _remoteStreamCache.IsEmpty)
            return;

        // Do NOT dispose the evicted stream — existing subscribers (e.g. live Blazor
        // components) are still attached to it and need to keep receiving updates
        // until they drop on their own. The eviction only prevents NEW callers from
        // re-using the now-stale stream; the next GetRemoteStream creates a fresh one
        // against the (re-)activated owner.
        foreach (var key in _remoteStreamCache.Keys)
        {
            if (string.Equals(key.Item1.ToString(), path, StringComparison.OrdinalIgnoreCase)
                && _remoteStreamCache.TryRemove(key, out var removed))
            {
                // Keep ownership of the evicted stream so DisposeAsync still tears
                // down its `sync/` hub — dropping it here orphaned the hub (never
                // disposed → TimerQueue-pinned forever). Only a materialised stream
                // has a hub to dispose.
                if (removed.IsValueCreated)
                    _evictedRemoteStreams.Add(removed.Value);
                _logger.LogDebug(
                    "Evicted remote stream cache for {Address} after change event.",
                    key.Item1);
            }
        }
    }

    /// <summary>
    /// Opens the initialization gate after all handlers are registered.
    /// Called via SyncBuildupActions to ensure proper ordering.
    /// </summary>
    internal void OpenInitializationGate()
    {
        DataContext.OpenInitializationGate();
    }


    /// <inheritdoc />
    public IReadOnlyCollection<Type> MappedTypes => DataContext.MappedTypes.ToArray();


    /// <inheritdoc />
    public IObservable<IEnumerable<TType>>? GetRemoteStream<TType>(Address address)
    {
        ThrowIfMeshNode(typeof(TType));
        return GetRemoteStream(
            address,
            new CollectionReference(Hub.TypeRegistry.GetOrAddType(typeof(TType), typeof(TType).Name))
            )?.Select(x => x.Value!.Instances.Values.OfType<TType>());
    }

    // 🚨 GetRemoteStream<MeshNode> is DISCOURAGED — the single-node remote reduce does not
    // converge well (divergent mirror streams, writes invisible to readers). The single
    // canonical API for a mesh node by path is workspace.GetMeshNodeStream(path) /
    // hub.GetMeshNodeStream(path), which routes every reader and writer through the shared
    // IMeshNodeStreamCache. We THROW so every callsite is caught and migrated — the
    // single-node remote reduce does not converge, so any direct use is a latent bug.
    // MeshWeaver.Data cannot reference the MeshNode type (it lives downstream in
    // MeshWeaver.Mesh.Contract), so detect it by name. The cache + reduce-callback plumbing
    // are the ONLY sanctioned openers and bypass this guard via the internal
    // GetRemoteStreamUnchecked overloads below.
    private static void ThrowIfMeshNode(Type reducedType)
    {
        if (reducedType.Name == "MeshNode")
            throw new InvalidOperationException(
                "GetRemoteStream<MeshNode> is forbidden — the single-node remote reduce does not converge. "
                + "Use workspace.GetMeshNodeStream(path) / hub.GetMeshNodeStream(path), which routes through the "
                + "shared mesh-node cache (IMeshNodeStreamCache). Framework internals open the raw stream via the "
                + "sanctioned GetRemoteStreamUnchecked escape hatch.");
    }

    /// <inheritdoc />
    public IObservable<T[]?>? GetStream<T>()
    {
        var collection = DataContext.GetTypeSource(typeof(T));
        if (collection == null)
            return null;
        // Hub already past Started → SynchronizationStream..ctor would throw
        // ObjectDisposedException synchronously and the exception would
        // propagate as a DeliveryFailure for any layout-area handler currently
        // composing menu items / etc. against this workspace. Match the existing
        // "return null" contract for unknown collections; callers (e.g.
        // NodeMenuItemsExtensions.GetMenuContext) already handle null with
        // `?? Observable.Return(empty)`.
        if (Hub.RunLevel > MessageHubRunLevel.Started)
            return null;
        return GetStream(typeof(T))
            .Synchronize()
            .Select(x => x.Value?.Collections.SingleOrDefault().Value?.Instances.Values.Cast<T>().ToArray());
    }

    /// <inheritdoc />
    public ISynchronizationStream<TReduced> GetRemoteStream<TReduced>(
        Address id,
        WorkspaceReference<TReduced> reference
    )
    {
        ThrowIfMeshNode(typeof(TReduced));
        return (ISynchronizationStream<TReduced>)
            GetSynchronizationStreamMethod
                .MakeGenericMethod(typeof(TReduced), reference.GetType())
                .Invoke(this, [id, reference])!;
    }


    // Points at the UNCHECKED implementation: the public dynamic-dispatch overload above
    // has already run WarnIfMeshNode, so the reflective hop must NOT re-enter the guarded
    // public path (which would double-warn). Sanctioned internal callers reach the same
    // unchecked body directly.
    private static readonly MethodInfo GetSynchronizationStreamMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.GetRemoteStreamUnchecked<object, WorkspaceReference<object>>(null!, null!)
        );


    /// <inheritdoc />
    public ISynchronizationStream<TReduced> GetRemoteStream<TReduced, TReference>(
        Address owner,
        TReference reference
    )
        where TReference : WorkspaceReference
    {
        ThrowIfMeshNode(typeof(TReduced));
        return GetRemoteStreamUnchecked<TReduced, TReference>(owner, reference);
    }

    // 🚨 The single sanctioned escape hatch behind the GetRemoteStream<MeshNode> guard.
    // internal (+ InternalsVisibleTo) so the shared mesh-node cache and the MeshNode
    // reduce-callback plumbing can open the raw remote reduce; never throws for MeshNode.
    internal ISynchronizationStream<TReduced> GetRemoteStreamUnchecked<TReduced, TReference>(
        Address owner,
        TReference reference
    )
        where TReference : WorkspaceReference =>
        Hub.Address.Equals(owner)
            ? throw new ArgumentException("Owner cannot be the same as the subscriber.")
            : GetExternalClientSynchronizationStream<TReduced, TReference>(owner, reference);

    /// <summary>
    /// Gets a remote stream with hub impersonation. The subscribing hub's address
    /// becomes the identity on the SubscribeRequest, ensuring hub-to-hub subscriptions
    /// use the hub's identity instead of any ambient user context.
    /// </summary>
    public ISynchronizationStream<EntityStore> GetRemoteStreamAsHub(
        Address owner,
        WorkspaceReference<EntityStore> reference
    ) =>
        Hub.Address.Equals(owner)
            ? throw new ArgumentException("Owner cannot be the same as the subscriber.")
            : (ISynchronizationStream<EntityStore>)this.CreateExternalClient<EntityStore, WorkspaceReference<EntityStore>>(owner, reference, impersonateAsHub: true);


    // 🚨 Lazy<T> wraps the factory because check-then-act ConcurrentDictionary
    // races would otherwise spawn duplicate upstream subscriptions: two
    // concurrent callers each pass TryGetValue (miss), each call
    // CreateExternalClient (which opens a SubscribeRequest to the owning hub
    // — a real side effect), and the second `_remoteStreamCache[key] = …`
    // overwrites the first. The orphaned stream remains subscribed and
    // continues consuming, doubling the emissions seen on the wire.
    // Lazy<T>(LazyThreadSafetyMode.ExecutionAndPublication) guarantees the
    // factory body runs at most once per key, regardless of contention.
    // Symptom this fixes: streaming-text test sequence
    // `[0, 19, 22, 19, 22, 46]` — every patch delivered twice via the
    // orphaned stream.
    private readonly ConcurrentDictionary<(Address, WorkspaceReference), Lazy<ISynchronizationStream>> _remoteStreamCache = new();

    // Streams that EvictForPath removed from the cache but did NOT dispose (their
    // live subscribers keep them attached). The workspace still OWNS their lifetime —
    // each carries a per-stream `sync/` hub whose 5s stale-callback scanner roots it
    // in the global TimerQueue, so an evicted-and-never-disposed stream leaks its hub
    // forever (the RunLevel=1 MeshHub_IsCollected failure). Disposed in DisposeAsync
    // alongside the still-cached streams, re-establishing the workspace-rooted
    // disposal that eviction severed.
    private readonly ConcurrentBag<ISynchronizationStream> _evictedRemoteStreams = new();

    private ISynchronizationStream<TReduced> GetExternalClientSynchronizationStream<
        TReduced,
        TReference
    >(Address address, TReference reference)
        where TReference : WorkspaceReference
    {
        var key = (address, (WorkspaceReference)reference);

        while (true)
        {
            // GetOrAdd with a Lazy<T> factory: the factory may run multiple
            // times to produce candidate Lazy objects, but only ONE wins the
            // dictionary slot and ALL callers see THAT one. The inner Lazy
            // (ExecutionAndPublication) then runs its expensive
            // CreateExternalClient body exactly once. Net: one stream per
            // key, never two competing live subscriptions.
            var lazy = _remoteStreamCache.GetOrAdd(key,
                _ => new Lazy<ISynchronizationStream>(
                    () => (ISynchronizationStream)this.CreateExternalClient<TReduced, TReference>(address, reference),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            var stream = lazy.Value;

            // Check if cached stream is still alive. Hub.RunLevel alone is not
            // sufficient because hub shutdown is async: right after stream.Dispose()
            // is called, RunLevel is still Running even though disposal was triggered.
            // Cast to the concrete type to access IsDisposing (not on interface).
            if (stream.Hub?.RunLevel <= MessageHubRunLevel.Started
                && stream.Hub is not MessageHub { IsDisposing: true })
                return (ISynchronizationStream<TReduced>)stream;

            // Dead — remove (if still ours) and retry. The TryRemove guards
            // against the case where another thread already replaced the
            // entry: only the original Lazy is removed.
            ((ICollection<KeyValuePair<(Address, WorkspaceReference), Lazy<ISynchronizationStream>>>)_remoteStreamCache)
                .Remove(new KeyValuePair<(Address, WorkspaceReference), Lazy<ISynchronizationStream>>(key, lazy));
        }
    }







    /// <inheritdoc />
    public void Update(IReadOnlyCollection<object> instances, UpdateOptions updateOptions, Activity? activity, IMessageDelivery request) =>
        RequestChange(
            new DataChangeRequest()
            {
                Updates = instances.ToImmutableList(),
                Options = updateOptions,
                ChangedBy = null
            }, activity, request
        );



    /// <inheritdoc />
    public void Delete(IReadOnlyCollection<object> instances, Activity? activity, IMessageDelivery request) =>
        RequestChange(
            new DataChangeRequest { Deletions = instances.ToImmutableList(), ChangedBy = null }, activity, request
        );

    /// <inheritdoc />
    public ISynchronizationStream<TReduced> GetStream<TReduced>(
        WorkspaceReference<TReduced> reference,
        Func<StreamConfiguration<TReduced>, StreamConfiguration<TReduced>>? configuration
        )
    {
        return (ISynchronizationStream<TReduced>?)ReduceManager.ReduceStream(
            this,
            reference,
            configuration
        ) ?? throw new InvalidOperationException("Failed to create stream");
    }

    /// <inheritdoc />
    public ISynchronizationStream<EntityStore> GetStream(params Type[] types)
        => (ISynchronizationStream<EntityStore>?)
            ReduceManager.ReduceStream<EntityStore>(
    this,
    new CollectionsReference(types
        .Select(t =>
            DataContext.TypeRegistry.TryGetCollectionName(t, out var name)
                ? name
                : throw new ArgumentException($"Type {t.FullName} is unknown.")
        ).ToArray()!),
    x => x) ?? throw new InvalidOperationException("Failed to create stream");

    /// <inheritdoc />
    public ReduceManager<EntityStore> ReduceManager => DataContext.ReduceManager;

    /// <inheritdoc />
    public IMessageHub Hub { get; }
    /// <summary>The workspace identity, equal to the owning hub's address.</summary>
    public object Id => Hub.Address;


    /// <inheritdoc />
    public DataContext DataContext { get; }

    /// <inheritdoc />
    public void RequestChange(DataChangeRequest change, Activity? activity, IMessageDelivery? request)
    {
        this.Change(change, activity, request);
    }

    private bool isDisposing;

    /// <summary>
    /// Disposes the workspace: drains registered sync and async disposables, disposes cached and
    /// evicted remote streams (tearing down their per-stream sync hubs), disposes the data context,
    /// and unsubscribes from the change feed. Idempotent.
    /// </summary>
    /// <returns>A task that completes when disposal finishes.</returns>
    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Workspace {WorkspaceId} starting DisposeAsync, Thread: {ThreadId}",
            Id, Thread.CurrentThread.ManagedThreadId);

        if (isDisposing)
        {
            _logger.LogDebug("Workspace {WorkspaceId} already disposing, returning", Id);
            return;
        }
        isDisposing = true;

        _logger.LogDebug("Workspace {WorkspaceId} disposing {AsyncCount} async disposables", Id, asyncDisposables.Count);
        while (asyncDisposables.TryTake(out var d))
        {
            try
            {
                await d.DisposeAsync();
                _logger.LogTrace("Workspace {WorkspaceId} disposed async disposable {DisposableType}", Id, d.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Workspace {WorkspaceId} error disposing async disposable {DisposableType}", Id, d.GetType().Name);
            }
        }

        _logger.LogDebug("Workspace {WorkspaceId} disposing {SyncCount} sync disposables", Id, disposables.Count);
        while (disposables.TryTake(out var d))
        {
            try
            {
                d.Dispose();
                _logger.LogTrace("Workspace {WorkspaceId} disposed sync disposable {DisposableType}", Id, d.GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Workspace {WorkspaceId} error disposing sync disposable {DisposableType}", Id, d.GetType().Name);
            }
        }

        // Dispose any cached remote streams that haven't been removed yet. Each
        // SynchronizationStream registers its own SubscribeRequest hub.Observe
        // callback for disposal here; without this loop the parent hub's
        // responseSubjects entry for each open SubscribeRequest leaks past the
        // test base's quiescing-budget leak check.
        if (!_remoteStreamCache.IsEmpty)
        {
            _logger.LogDebug("Workspace {WorkspaceId} disposing {RemoteStreamCount} cached remote streams",
                Id, _remoteStreamCache.Count);
            foreach (var key in _remoteStreamCache.Keys)
            {
                if (_remoteStreamCache.TryRemove(key, out var cached))
                {
                    // Skip if the Lazy was never materialised — no stream
                    // was actually created, nothing to dispose.
                    if (!cached.IsValueCreated) continue;
                    try { cached.Value.Dispose(); }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Workspace {WorkspaceId} error disposing remote stream {Key}",
                            Id, key);
                    }
                }
            }
        }

        // Streams evicted by the change feed (removed from _remoteStreamCache without
        // disposal) are still workspace-owned — dispose them here so their `sync/`
        // hubs (and the TimerQueue-rooting stale-callback scanner) are torn down.
        // Idempotent with subscriber-driven disposal: SynchronizationStream.Dispose
        // is safe to call twice.
        while (_evictedRemoteStreams.TryTake(out var evicted))
        {
            try { evicted.Dispose(); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Workspace {WorkspaceId} error disposing evicted remote stream", Id);
            }
        }

        _logger.LogDebug("Workspace {WorkspaceId} disposing DataContext", Id);
        try
        {
            DataContext.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workspace {WorkspaceId} error disposing DataContext", Id);
        }

        try { _changeFeedSubscription?.Dispose(); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Workspace {WorkspaceId} failed to dispose change-feed subscription", Id);
        }

        _logger.LogInformation("Workspace {WorkspaceId} DisposeAsync completed", Id);
    }
    private readonly ConcurrentBag<IDisposable> disposables = new();
    private readonly ConcurrentBag<IAsyncDisposable> asyncDisposables = new();

    /// <inheritdoc />
    public void AddDisposable(IDisposable disposable)
    {
        disposables.Add(disposable);
    }

    /// <inheritdoc />
    public void AddDisposable(IAsyncDisposable disposable)
    {
        asyncDisposables.Add(disposable);
    }

    /// <inheritdoc />
    public ISynchronizationStream<EntityStore>? GetStream(StreamIdentity identity)
    {
        var ds = DataContext.GetDataSourceForId(identity.Owner);
        return ds?.GetStreamForPartition(identity.Partition);
    }


    /// <summary>
    /// Handles a <see cref="DataChangeResponse"/>: marks the delivery processed when the change
    /// committed, otherwise ignores it.
    /// </summary>
    /// <param name="response">The data change response delivery to handle.</param>
    /// <returns>The processed or ignored delivery.</returns>
    protected IMessageDelivery HandleCommitResponse(IMessageDelivery<DataChangeResponse> response)
    {
        if (response.Message.Status == DataChangeStatus.Committed)
            return response.Processed();
        // TODO V10: Here we have to put logic to revert the state if commit has failed. (26.02.2024, Roland Bürgi)
        return response.Ignored();
    }

    void IWorkspace.SubscribeToClient(
        IMessageDelivery<SubscribeRequest> delivery
    )
    {
        var referenceType = delivery.Message.Reference.GetType();
        var genericWorkspaceType = referenceType;
        while (!genericWorkspaceType!.IsGenericType || genericWorkspaceType.GetGenericTypeDefinition() != typeof(WorkspaceReference<>))
        {
            genericWorkspaceType = genericWorkspaceType.BaseType;
        }

        var reducedType = genericWorkspaceType.GetGenericArguments().First();
        SubscribeToClientMethod
            .MakeGenericMethod(reducedType, referenceType)
            .Invoke(this, [delivery]);
    }


    private static readonly MethodInfo SubscribeToClientMethod =
        ReflectionHelper.GetMethodGeneric<Workspace>(x =>
            x.SubscribeToClient<object, WorkspaceReference<object>>(null!)
        );

    private void SubscribeToClient<TReduced, TReference>(IMessageDelivery<SubscribeRequest> delivery)
        where TReference : WorkspaceReference<TReduced>
    {
        this.CreateSynchronizationStream<TReduced, TReference>(delivery);
    }


}
