using System.Collections.Concurrent;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// TypeSource for MeshNode that bridges a routing-supplied own-node observable
/// into the workspace and dispatches workspace writes:
/// <list type="bullet">
///   <item><b>Adds (create) and Deletes</b> go straight to <see cref="IStorageAdapter"/>
///   — insta write, no queue. These are infrequent and ordering matters
///   (descendants must see parent state immediately).</item>
///   <item><b>Updates</b> are dispatched through the per-node hub's actor inbox
///   as <see cref="SaveMeshNodeRequest"/> messages — the handler in
///   <see cref="MeshDataSourceExtensions"/> calls <c>SaveNode</c>. Updates from
///   editing surfaces can fire in rapid succession; the inbox serialises them
///   per node without blocking the workspace pipeline.</item>
/// </list>
/// </summary>
public record MeshNodeTypeSource : TypeSourceWithType<MeshNode, MeshNodeTypeSource>
{
    private readonly IStorageAdapter? _persistenceCore;
    private readonly string _hubPath;  // e.g., "graph/org1"
    private readonly IWorkspace _workspace;
    private readonly ILogger? _logger;
    private readonly IObservable<MeshNode?>? _ownNodeStream;
    private InstanceCollection _lastSaved = new();

    // Pending create / delete buffer. UpdateImpl enqueues here; a debounce
    // timer drains via FlushPendingWrites every DebounceInterval. The dict
    // collapses rapid retargets of the same path into the latest version,
    // so we don't write the same row twice when the workspace pipeline
    // fires UpdateImpl multiple times in quick succession.
    private static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(200);
    private readonly ConcurrentDictionary<string, MeshNode> _pendingSaves = new();
    private readonly ConcurrentBag<string> _pendingDeletes = new();
    private Timer? _debounceTimer;
    private readonly object _timerLock = new();
    // Set true by FlushOnDispose (under _timerLock). Once the hub is tearing down,
    // ResetDebounceTimer must NOT schedule a new Timer: a fresh one-shot Timer is
    // rooted by the process-wide TimerQueue and its callback captures `this` →
    // Workspace → MessageHub, pinning the whole disposed hub graph (confirmed via
    // ClrMD GC-root analysis). A data change racing the async final flush would
    // otherwise re-arm the timer right after FlushOnDispose disposed it.
    private bool _disposed;
    private readonly CompositeDisposable _pendingFlushSubscriptions = new();

    // Paths just deleted via IDataChangeNotifier — short-window block list so a
    // workspace UpdateImpl that fires AFTER storage.Delete (per-node hub starting
    // up to handle a recursive delete sees the node in its initial instances
    // snapshot) doesn't resurrect the row. Kept as a plain set: the
    // RecentlyDeletedTtl window is short, the volume is bounded by deletes in
    // flight, and a stale entry only blocks one save that would otherwise be a
    // no-op anyway.
    private static readonly TimeSpan RecentlyDeletedTtl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentlyDeleted = new();

    internal MeshNodeTypeSource(
        IWorkspace workspace,
        object dataSource,
        IStorageAdapter? persistenceCore,
        string hubPath,
        IObservable<MeshNode?>? ownNodeStream = null)
        : base(workspace, dataSource)
    {
        _workspace = workspace;
        _persistenceCore = persistenceCore;
        _hubPath = hubPath;
        // DistinctUntilChanged drops re-emissions that already match the local
        // state (same Version + content) — the routing layer (catalog stream /
        // change feed) can re-publish the same node multiple times after a
        // local write echoes through persistence; we don't want each echo to
        // re-run the workspace pipeline. Replay(1).RefCount() so late
        // subscribers (OwnNodeCache, future consumers) see the latest
        // authoritative snapshot, not just future emissions.
        _ownNodeStream = ownNodeStream?
            .DistinctUntilChanged()
            .Replay(1).RefCount();
        _logger = workspace.Hub.ServiceProvider.GetService<ILogger<MeshNodeTypeSource>>();
        _logger?.LogDebug("MeshNodeTypeSource: Created for hubPath={HubPath} (routingSuppliedStream={Supplied})",
            hubPath, ownNodeStream != null);

        TypeDefinition = workspace.Hub.TypeRegistry.WithKeyFunction(
            TypeDefinition.CollectionName,
            new KeyFunction(o => ((MeshNode)o).Id, typeof(string)));

        // Race guard: when a node is deleted via HandleDeleteNodeRequest's
        // direct storage.Delete (recursive subtree fan-out, not via the
        // workspace), MeshNodeTypeSource's debounce-driven save would
        // otherwise resurrect it. The own-node hub's workspace initialises
        // from storage when the per-node hub starts up to handle the
        // delete — UpdateImpl sees the node as an "add" and queues a save
        // that fires ~200 ms later, AFTER storage.Delete has already
        // succeeded. The DeleteNode handler fires
        // `IDataChangeNotifier.NotifyChange(Deleted)` per path; subscribing
        // and dropping the pending save reconciles the two writers.
        // Storage-level change events were removed. Delete reconciliation
        // for this debounce buffer used to subscribe to a storage Changes
        // feed; that feed is gone. The per-node hub's HandleDeleteNodeRequest
        // is now the only path that mutates the per-node hub's cache.IsDeleted
        // — the debounce sampler's IsDeleted gate above is sufficient to
        // prevent the resurrect-after-delete write race on the OWNING hub.

        // 🚨 SYNC teardown FIRST: stop + dispose the debounce timer and block any
        // re-arm. This runs in the hub's synchronous dispose phase, before the
        // async FlushOnDispose and before the workspace can fire another UpdateImpl,
        // so the TimerQueue-rooted one-shot timer never outlives disposal. Without
        // it the timer's callback captures `this` → Workspace → MessageHub and pins
        // the whole disposed hub graph in the process-wide TimerQueue (ClrMD-confirmed
        // GC-root: TimerQueue → TimerQueueTimer → TimerCallback → MeshNodeTypeSource
        // → Workspace → MessageHub). The async FlushOnDispose re-arm guard alone was
        // too late — UpdateImpl re-armed the timer before the async phase ran.
        workspace.Hub.RegisterForDisposal(_ =>
        {
            lock (_timerLock)
            {
                _disposed = true;
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }
        });

        // Hub-teardown hook — the final flush of any pending writes so a per-node hub
        // disposing mid-write doesn't lose data. Registered as a REACTIVE dispose action:
        // it RETURNS the flush IObservable (it does not bury a Subscribe inside a void
        // Action), so the hub composes it with the other dispose actions and subscribes
        // the chain on teardown. The flush composes IStorageAdapter.Write/Delete — the
        // storage adapter is mesh-scoped (outlives this per-node hub) and runs its
        // genuinely-async I/O on the mesh IO pool — so the writes land on the pool, not
        // on the hub. The hub never awaits; nothing here is a Task.
        workspace.Hub.RegisterForDisposal(_ =>
        {
            lock (_timerLock)
            {
                _disposed = true; // block any further ResetDebounceTimer re-arm
                _debounceTimer?.Dispose();
                _debounceTimer = null;
            }
            return FlushPendingWrites()
                .Timeout(TimeSpan.FromSeconds(10))
                .DefaultIfEmpty(System.Reactive.Unit.Default)
                .Catch<System.Reactive.Unit, Exception>(ex =>
                {
                    _logger?.LogWarning(ex,
                        "MeshNodeTypeSource: final flush failed for {HubPath}", _hubPath);
                    return Observable.Return(System.Reactive.Unit.Default);
                })
                .Finally(() => _pendingFlushSubscriptions.Dispose());
        });
    }

    protected override InstanceCollection UpdateImpl(InstanceCollection instances)
    {
        instances = MergePartialUpdates(instances);

        // Race guard: an empty change can race the async Initialize emission
        // (the framework's stream Subscribe loop may invoke Update with an empty
        // InstanceCollection between stream creation and Initialize completion;
        // see Doc/Architecture/InitializationGates.md). When that happens with
        // _lastSaved already populated, the previous behaviour overwrote
        // _lastSaved = empty and every subsequent ReduceToMeshNode returned null
        // — the OrleansHostedHubRoutingTest workspace-propagation symptom.
        if (instances.Instances.Count == 0 && _lastSaved.Instances.Count > 0)
        {
            _logger?.LogDebug("MeshNodeTypeSource.UpdateImpl: no-op empty change for {HubPath} (preserving {Count} loaded entries)",
                _hubPath, _lastSaved.Instances.Count);
            return _lastSaved;
        }

        // Open MeshNode init gate when node becomes Active or Transient
        {
            var ownNode = instances.Instances.Values.OfType<MeshNode>()
                .FirstOrDefault(n => n.Path == _hubPath);
            if (ownNode is { State: MeshNodeState.Active or MeshNodeState.Transient })
            {
                _logger?.LogDebug("MeshNodeTypeSource: Opening gate for {HubPath} — node {State} via update", _hubPath, ownNode.State);
                _workspace.Hub.OpenGate(MeshNodeExtensions.MeshNodeInitGateName);
            }
        }

        var adds = instances.Instances
            .Where(x => !_lastSaved.Instances.ContainsKey(x.Key))
            .Select(x => (MeshNode)x.Value)
            .ToArray();

        // Compare content IGNORING Version — a version-only difference is not a real
        // content change. The owner re-stamps Version on every update (below), so without
        // this guard each re-fire of the workspace pipeline would look like a fresh change
        // (a new version per dispatch) and spin an infinite re-stamp loop.
        var updates = instances.Instances
            .Where(x => _lastSaved.Instances.TryGetValue(x.Key, out var existing)
                        && existing is MeshNode ex && x.Value is MeshNode nv
                        && !ex.Equals(nv with { Version = ex.Version }))
            .Select(x => (MeshNode)x.Value)
            .ToArray();

        var deletes = _lastSaved.Instances
            .Where(x => !instances.Instances.ContainsKey(x.Key))
            .Select(x => (MeshNode)x.Value)
            .ToArray();

        _logger?.LogDebug("MeshNodeTypeSource.UpdateImpl: adds={Adds}, updates={Updates}, deletes={Deletes}",
            adds.Length, updates.Length, deletes.Length);

        var hubVersion = _workspace.Hub.Version;

        // Creates: enqueue for the debounce flush. Dict semantics collapse a
        // burst of UpdateImpl emissions for the same path into a single
        // SaveNode call — important when the workspace pipeline re-fires on
        // each MeshNodeReference reducer notification.
        //
        // Recently-deleted guard: a per-node hub starting up to handle a
        // recursive DeleteNodeRequest sees its own node in the workspace's
        // initial instances snapshot. UpdateImpl then sees an "add" and would
        // resurrect the row 200 ms later. The IDataChangeNotifier delete
        // arrives BEFORE that UpdateImpl, so the path is already on the
        // recently-deleted list — drop the add.
        var nowUtc = DateTimeOffset.UtcNow;
        PruneRecentlyDeleted(nowUtc);
        foreach (var node in adds)
        {
            if (!string.IsNullOrEmpty(node.Path)
                && _recentlyDeleted.TryGetValue(node.Path, out var deletedAt)
                && nowUtc - deletedAt < RecentlyDeletedTtl)
            {
                _logger?.LogDebug(
                    "MeshNodeTypeSource[{HubPath}]: skip save for recently-deleted {Path}",
                    _hubPath, node.Path);
                continue;
            }
            var nodeWithVersion = node with { Version = hubVersion };
            _pendingSaves[node.Path] = nodeWithVersion;
        }

        // 🚨 Stamp the owning hub's monotonic version onto every UPDATED node too.
        // The owner is the single version clock (Host.Version == _workspace.Hub.Version,
        // which initialises from the persisted node and increments once per dispatch).
        // Adds are stamped above; updates were NOT, so an updated node kept its incoming
        // (client-carried, deliberately-constant) version. The emitted frame then did not
        // advance the version, so the change feed / subscriber monotonicity guard treated
        // it as nothing-new and the reconciled update never reached subscribers' mirrors —
        // the read-your-writes-after-update bug (create worked because adds were stamped).
        // Re-emit the stamped nodes so propagation advances; the persistence subscriber
        // then saves the bumped version.
        if (updates.Length > 0)
        {
            var stampedUpdates = updates
                .Select(n => n with { Version = hubVersion })
                .ToArray();
            instances = instances with
            {
                Instances = instances.Instances.SetItems(
                    stampedUpdates.Select(n => new KeyValuePair<object, object>(n.Id, n)))
            };
        }

        // Updates do NOT dispatch saves here — the persistence subscriber on
        // the workspace's MeshNode stream (registered by SubscribeToOwnDeletion
        // in MeshDataSource) handles updates with Sample(200ms) debouncing.
        // The diff bookkeeping above keeps _lastSaved current so create/delete
        // detection on subsequent calls is accurate.

        // Deletes: same debounce queue. Path identity matches the saves dict,
        // so a same-tick add-then-delete cancels the save (paths can't be in
        // both buckets and survive).
        foreach (var node in deletes)
        {
            if (string.IsNullOrEmpty(node.Path)) continue;
            _pendingDeletes.Add(node.Path);
        }

        ResetDebounceTimer();

        _lastSaved = instances;
        return instances;
    }

    private void PruneRecentlyDeleted(DateTimeOffset nowUtc)
    {
        foreach (var kv in _recentlyDeleted)
        {
            if (nowUtc - kv.Value > RecentlyDeletedTtl)
                _recentlyDeleted.TryRemove(kv.Key, out _);
        }
    }

    private void ResetDebounceTimer()
    {
        if (_persistenceCore is null) return;
        lock (_timerLock)
        {
            if (_disposed) return; // hub tearing down — don't re-arm a TimerQueue-rooted timer
            // Hub past Started ⇒ shutting down. A flush-echo UpdateImpl during Quiescing
            // was re-arming the debounce timer AFTER the dispose hooks ran, leaving a
            // one-shot TimerQueueTimer whose callback pins MeshNodeTypeSource → Workspace
            // → MessageHub for the whole disposed hub graph (ClrMD-confirmed). Gating on
            // the live RunLevel stops the re-arm at the source, independent of dispose-
            // action ordering.
            if (_workspace.Hub.RunLevel > MessageHubRunLevel.Started) return;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ =>
            {
                // Subscribe drives the flush — observable returns Unit when all
                // ops have settled. Disposable retained so FlushOnDispose can
                // wait on in-flight subscriptions during hub teardown.
                // A faulted flush is unpersisted data — must never be silent.
                var sub = FlushPendingWrites().Subscribe(
                    _ => { },
                    ex => _logger?.LogWarning(ex,
                        "Debounced flush of pending writes failed for {HubPath}", _hubPath));
                _pendingFlushSubscriptions.Add(sub);
            }, null, DebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Drains <see cref="_pendingSaves"/> and <see cref="_pendingDeletes"/> into
    /// a composed IObservable&lt;Unit&gt; — each save/delete becomes one step
    /// in a Concat chain that completes only after every op has acked. The
    /// debounce timer subscribes for fire-and-forget; the reactive dispose action
    /// registered in the ctor awaits the same observable on hub teardown so no
    /// in-flight write is lost.
    /// </summary>
    private IObservable<System.Reactive.Unit> FlushPendingWrites()
    {
        if (_persistenceCore is null)
            return Observable.Return(System.Reactive.Unit.Default);

        var saves = new List<MeshNode>();
        foreach (var key in _pendingSaves.Keys.ToArray())
            if (_pendingSaves.TryRemove(key, out var node))
                saves.Add(node);

        var deletes = new List<string>();
        while (_pendingDeletes.TryTake(out var path))
            deletes.Add(path);

        if (saves.Count == 0 && deletes.Count == 0)
            return Observable.Return(System.Reactive.Unit.Default);

        _logger?.LogDebug("MeshNodeTypeSource: Flushing {Saves} saves, {Deletes} deletes for {HubPath}",
            saves.Count, deletes.Count, _hubPath);

        var options = _workspace.Hub.JsonSerializerOptions;
        var saveOps = saves.Select(node =>
            _persistenceCore.Write(node, options)
                .Select(saved =>
                {
                    _logger?.LogDebug("MeshNodeTypeSource: Saved {Path} (version={Version})",
                        saved?.Path, saved?.Version);
                    return System.Reactive.Unit.Default;
                })
                .Catch<System.Reactive.Unit, Exception>(ex =>
                {
                    _logger?.LogError(ex, "MeshNodeTypeSource: SAVE FAILED for {Path} (version={Version})",
                        node.Path, node.Version);
                    return Observable.Return(System.Reactive.Unit.Default);
                }));

        var deleteOps = deletes.Select(path =>
            _persistenceCore.Delete(path)
                .Select(deleted =>
                {
                    _logger?.LogDebug("MeshNodeTypeSource: Deleted {Path}", deleted);
                    return System.Reactive.Unit.Default;
                })
                .Catch<System.Reactive.Unit, Exception>(ex =>
                {
                    _logger?.LogError(ex, "MeshNodeTypeSource: DELETE FAILED for {Path}", path);
                    return Observable.Return(System.Reactive.Unit.Default);
                }));

        return saveOps.Concat(deleteOps).Concat()
            .DefaultIfEmpty(System.Reactive.Unit.Default)
            .LastAsync();
    }

    /// <summary>
    /// Hub-teardown adapter that runs <see cref="FlushPendingWrites"/> one last
    /// time and waits for completion. Without this, a per-node hub disposing
    /// mid-write loses the in-flight save — the next test reads the file
    /// before it landed on disk and reports "node not found".
    /// </summary>
    private InstanceCollection MergePartialUpdates(InstanceCollection instances)
    {
        var mergedInstances = new Dictionary<object, object>(instances.Instances);
        var anyMerged = false;

        foreach (var kvp in instances.Instances)
        {
            if (kvp.Value is not MeshNode incomingNode)
                continue;

            if (!_lastSaved.Instances.TryGetValue(kvp.Key, out var existingObj) ||
                existingObj is not MeshNode existingNode)
                continue;

            var isPartialUpdate = incomingNode.Content != null &&
                                  string.IsNullOrEmpty(incomingNode.Name) &&
                                  string.IsNullOrEmpty(incomingNode.Category) &&
                                  string.IsNullOrEmpty(incomingNode.Icon);

            if (!isPartialUpdate)
                continue;

            var mergedNode = existingNode with
            {
                NodeType = incomingNode.NodeType ?? existingNode.NodeType,
                Content = incomingNode.Content ?? existingNode.Content
            };

            mergedInstances[kvp.Key] = mergedNode;
            anyMerged = true;

            _logger?.LogDebug("MeshNodeTypeSource: Merged partial update for {Path}", mergedNode.Path);
        }

        return anyMerged
            ? new InstanceCollection(mergedInstances.Values, TypeDefinition.GetKey)
            : instances;
    }

    /// <summary>
    /// Seeds the workspace with the own MeshNode and follows subsequent emissions.
    /// <para>
    /// The routing layer (Orleans <c>MessageHubGrain</c>, Monolith
    /// <c>MonolithRoutingService</c>) attaches an own-node observable via
    /// <see cref="OwnNodeStreamExtensions.WithOwnNodeStream"/> at hub
    /// instantiation; that stream is the source of truth — its first emission
    /// seeds the workspace and subsequent emissions push live updates into the
    /// MeshNodeReference reducer without a separate change-feed subscription.
    /// </para>
    /// <para>
    /// When no stream is supplied (fixtures that bypass routing), falls back
    /// to a one-shot persistence read so tests that construct a per-node hub
    /// directly continue to work.
    /// </para>
    /// </summary>
    protected override IObservable<InstanceCollection> Initialize(
        WorkspaceReference<InstanceCollection> reference,
        CancellationToken cancellationToken)
    {
        // Prefer the routing-supplied stream when available (live updates,
        // catalog stream). Fall back to a one-shot persistence read for test
        // fixtures that construct this TypeSource directly without going
        // through MeshDataSource's MonolithRoutingService wiring.
        if (_ownNodeStream is not null)
        {
            return _ownNodeStream
                .Where(n => n != null)
                .Select(rawNode => BuildInstanceCollection(rawNode));
        }

        if (_persistenceCore is not null)
        {
            return _persistenceCore.Read(_hubPath, _workspace.Hub.JsonSerializerOptions)
                .Select(rawNode => BuildInstanceCollection(rawNode));
        }

        // No stream and no persistence — emit empty and open the gate so
        // queued CreateNodeRequest / GetDataResponse traffic isn't held forever.
        _workspace.Hub.OpenGate(MeshNodeExtensions.MeshNodeInitGateName);
        return Observable.Return(_lastSaved);
    }

    private InstanceCollection BuildInstanceCollection(MeshNode? rawNode)
    {
        var ownNode = rawNode != null ? ResolveJsonElementContent(rawNode) : null;

        // 🚨 LEAVE BOTH CLOCKS ALONE on load — never touch Hub.Version, never re-stamp the node.
        // Hub.Version is already strictly monotonic on its own (++ per message in
        // MessageHub.HandleMessageAsync) and is the SHARED clock for this hub: it also stamps the
        // hub's LAYOUT-AREA stream Fulls (Host.Version), which advance per render independently of
        // node writes. BuildInstanceCollection runs on EVERY ownNode emission (not just first load),
        // so the old SetInitialVersion(node.Version) re-stamped the clock BACKWARD to a low static/doc
        // node version on every catalog push, dropping the live layout Fulls under the stale-Full
        // monotonicity guard → "cannot find pinned doc" wedge (atioz 2026-06-18). Removing that reset
        // IS the wedge fix. Do NOT bump the loaded node's own version either: a load is a READ, and a
        // read that inflates Version breaks read-your-version semantics (MeshNodeVersionSyncTest) and
        // would re-emit on every catalog push. Recovery/reload re-application is driven by the WRITE
        // path stamping a higher Hub.Version on the node when it actually changes — not by load.

        _workspace.Hub.OpenGate(MeshNodeExtensions.MeshNodeInitGateName);

        var allNodes = new List<MeshNode>();
        if (ownNode != null && !string.IsNullOrEmpty(ownNode.Path))
            allNodes.Add(ownNode);

        _lastSaved = new InstanceCollection(allNodes, node => ((MeshNode)node).Id);
        return _lastSaved;
    }

    private MeshNode ResolveJsonElementContent(MeshNode node)
    {
        if (node.Content is not JsonElement je || je.ValueKind != JsonValueKind.Object)
            return node;

        if (!je.TryGetProperty("$type", out var typeProp))
            return node;

        var typeName = typeProp.GetString();
        if (string.IsNullOrEmpty(typeName))
            return node;

        var registry = _workspace.Hub.TypeRegistry;

        if (!registry.TryGetType(typeName, out var typeDef) || typeDef?.Type == null)
        {
            var shortName = typeName.Contains('.') ? typeName.Split('.').Last() : null;
            if (shortName is null
                || !registry.TryGetType(shortName, out typeDef) || typeDef?.Type == null)
            {
                // 🚨 Bad-data tolerance contract: degrade to JsonElement but LOUDLY.
                // This is the OWNING hub failing to type its OWN node's content — the
                // discriminator is unresolvable on the one registry that is canonical
                // for this node (mesh root chain + this NodeType's content types), so
                // the node will render empty and refuse content edits everywhere.
                // Silent degradation here is exactly how the atioz 2026-06-12
                // '$type: MarkdownConfiguration' rows went undiagnosed.
                _logger?.LogWarning(
                    "MeshNodeTypeSource[{HubPath}]: content discriminator '$type': '{TypeName}' " +
                    "is not a registered type on the owning hub — content stays an untyped " +
                    "JsonElement (renders empty, not editable). The row needs repair: rewrite " +
                    "the content with the NodeType's declared content type.",
                    _hubPath, typeName);
                return node;
            }
        }

        try
        {
            var raw = je.GetRawText();
            var deserialized = JsonSerializer.Deserialize(raw, typeDef.Type, _workspace.Hub.JsonSerializerOptions);
            if (deserialized != null)
            {
                _logger?.LogDebug("MeshNodeTypeSource: Resolved JsonElement content to {Type} for {Path}",
                    typeDef.Type.Name, node.Path);
                return node with { Content = deserialized };
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MeshNodeTypeSource: Failed to resolve JsonElement content for {Path}", node.Path);
        }

        return node;
    }
}
