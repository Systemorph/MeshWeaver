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
    private readonly MeshDataSourceExtensions.OwnNodeCache? _ownNodeCache;
    private InstanceCollection _lastSaved = new();

    // Highest own-node Version this hub has adopted — from the activation seed, from a routing
    // emission, AND from a local write committed through UpdateImpl. Own-node emissions below it
    // are stale snapshots and are dropped (see AcceptOwnNodeEmission).
    //
    // 🚨 UpdateImpl MUST feed this too, not just the Initialize chain. The durable activation
    // seed is an async read on a real backend: it can land AFTER the routing stream seeded and
    // after a local write already advanced the in-RAM node. A floor that only tracked the
    // Initialize chain would then accept that older durable emission and BuildInstanceCollection
    // would reset _lastSaved onto it — clobbering the newer in-RAM commit. Raising the floor on
    // every committed change makes the gate "never move this hub's own node backward", whatever
    // the source.
    private long _ownNodeVersionFloor;

    // 0 until this hub holds own-node state at all — set by RaiseOwnVersionFloor, so it covers
    // BOTH the Initialize chain and a UpdateImpl commit that raced ahead of it. Gates only the
    // "durable seed replaces on strictly-newer" rule in AcceptOwnNodeEmission; a plain int is
    // enough because it is one-way (0 → 1) and never read for mutual exclusion.
    private int _ownNodeAdopted;

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

    /// <summary>
    /// Severable indirection between the debounce <see cref="Timer"/>'s callback and this source.
    /// The callback captures the CELL, never <c>this</c>; teardown nulls <see cref="Target"/>, so a
    /// timer the TimerQueue is still holding after <c>Dispose</c> can no longer reach the hub graph.
    /// See the note in <c>ResetDebounceTimer</c>.
    /// </summary>
    private sealed class FlushCell
    {
        public MeshNodeTypeSource? Target;
    }

    private readonly FlushCell _flushCell = new();

    // Paths just deleted via IDataChangeNotifier — short-window block list so a
    // workspace UpdateImpl that fires AFTER storage.Delete (per-node hub starting
    // up to handle a recursive delete sees the node in its initial instances
    // snapshot) doesn't resurrect the row. Kept as a plain set: the
    // RecentlyDeletedTtl window is short, the volume is bounded by deletes in
    // flight, and a stale entry only blocks one save that would otherwise be a
    // no-op anyway.
    private static readonly TimeSpan RecentlyDeletedTtl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentlyDeleted = new();

    // Mesh-scoped "delete wins" tombstone (shared across hub instances). The per-hub
    // _recentlyDeleted above only guards a delete-then-add WITHIN this hub instance; this
    // registry carries a delete recorded by the owning hub across to a DIFFERENT hub instance
    // that activates afterward (with a stale Replay(1) own-node snapshot) and would otherwise
    // resurrect the row via the activation-save below. See RecentlyDeletedRegistry.
    private readonly RecentlyDeletedRegistry? _recentlyDeletedRegistry;

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
        _ownNodeCache = workspace.Hub.ServiceProvider
            .GetService<MeshDataSourceExtensions.OwnNodeCache>();
        _recentlyDeletedRegistry = workspace.Hub.ServiceProvider
            .GetService<RecentlyDeletedRegistry>();
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
        _flushCell.Target = this;

        workspace.Hub.RegisterForDisposal(_ =>
        {
            lock (_timerLock)
            {
                _disposed = true;
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                // Sever the cell too: Timer.Dispose does not evict a one-shot timer from the
                // TimerQueue before its due time, so the queue can still hold the callback's
                // state for up to DebounceInterval. Nulling the target makes that residue
                // unable to reach this source (and through it the whole hub graph).
                _flushCell.Target = null;
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
                _flushCell.Target = null; // see the sync hook above
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

    /// <summary>
    /// Diffs the incoming instance collection against the last-saved snapshot and
    /// dispatches the resulting persistence work: creates and deletes are queued onto
    /// the debounce flush, updates are version-stamped and re-emitted (the workspace's
    /// own-node persistence sampler saves them). Also opens the MeshNode init gate when
    /// the own node becomes Active/Transient. Returns the (possibly re-stamped) collection.
    /// </summary>
    /// <param name="instances">The instance collection to persist/update.</param>
    /// <returns>The instance collection to store as the new authoritative snapshot.</returns>
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
        // Pair each updated node with the version it PREVIOUSLY carried (_lastSaved) so the
        // re-stamp below can advance forward-only from the node's own version (#325) — a fresh
        // Hub.Version alone regresses it after a recycle.
        var updates = instances.Instances
            .Where(x => _lastSaved.Instances.TryGetValue(x.Key, out var existing)
                        && existing is MeshNode ex && x.Value is MeshNode nv
                        && !ex.Equals(nv with { Version = ex.Version }))
            .Select(x => (Node: (MeshNode)x.Value,
                          PreviousVersion: ((MeshNode)_lastSaved.Instances[x.Key]).Version))
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
            var perHubRecentlyDeleted = !string.IsNullOrEmpty(node.Path)
                && _recentlyDeleted.TryGetValue(node.Path, out var deletedAt)
                && nowUtc - deletedAt < RecentlyDeletedTtl;
            // Also honour the MESH-scoped tombstone: this per-node hub may have activated
            // AFTER the delete (fresh instance, empty per-hub set) and be about to resurrect
            // the row from a stale Replay(1) own-node snapshot. The owning hub recorded the
            // delete in the shared registry, so drop the add here. See RecentlyDeletedRegistry.
            if (perHubRecentlyDeleted
                || (_recentlyDeletedRegistry?.IsRecentlyDeleted(node.Path) ?? false))
            {
                _logger?.LogDebug(
                    "MeshNodeTypeSource[{HubPath}]: skip save for recently-deleted {Path}",
                    _hubPath, node.Path);
                continue;
            }
            // 🚨 #325: the SAME forward-only floor the updates branch below applies. An "add" is
            // add-relative-to-THIS-source-instance, NOT globally new: after an idle-recycle the
            // reactivated hub's _lastSaved starts empty, so its own already-durable node is
            // classified here as a create. A plain `Version = hubVersion` then stamps it with the
            // fresh per-activation hub clock (which resets toward 0) and the debounce flush writes
            // that REGRESSED version over the durable row — the write-rollback of #325, this time
            // through the create path. MeshNode.NextVersion floors at the node's own carried
            // version, so a genuinely new node (Version 0) is unaffected (max(hubVersion, 1) ==
            // hubVersion for any live clock) while a re-added durable node can never go backward.
            // Doc/Architecture/MeshNodeVersioning.md: the floor applies at EVERY place the owner
            // mints a node Version — this is the persistence re-stamp it names.
            var nodeWithVersion = node with { Version = MeshNode.NextVersion(hubVersion, node.Version) };
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
            // 🚨 #325: advance forward-only from each node's OWN previous version, never straight
            // off hubVersion. hubVersion resets to 0 on reactivation, so a plain `Version =
            // hubVersion` re-stamp regresses the node's version after a recycle — the authoritative
            // `get`/read then returns a version BELOW the writes it just confirmed (the write-
            // rollback / "v113 read back as v3" of #325). MeshNode.NextVersion keeps it strictly
            // increasing across activations WITHOUT touching Hub.Version (so layout Fulls, which
            // ride Hub.Version, are unaffected — the 2026-06-18 wedge cannot recur).
            var stampedUpdates = updates
                .Select(u => u.Node with
                {
                    Version = MeshNode.NextVersion(hubVersion, u.PreviousVersion)
                })
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

        // Own-node monotonicity: a committed change is the newest state this hub holds, so it
        // raises the floor that AcceptOwnNodeEmission gates the (possibly late, possibly stale)
        // activation-seed / routing emissions against.
        foreach (var value in instances.Instances.Values)
            if (value is MeshNode n && string.Equals(n.Path, _hubPath, StringComparison.OrdinalIgnoreCase))
            {
                RaiseOwnVersionFloor(n.Version);
                break;
            }

        _lastSaved = instances;
        return instances;
    }

    /// <summary>Records that this hub now holds own-node state at <paramref name="version"/>:
    /// monotonically (max) raises <see cref="_ownNodeVersionFloor"/> and marks the node adopted.
    /// Lock-free — the workspace pipeline and the Initialize chain both call it, from different
    /// threads.</summary>
    private void RaiseOwnVersionFloor(long version)
    {
        _ownNodeAdopted = 1;
        var current = Volatile.Read(ref _ownNodeVersionFloor);
        while (version > current)
        {
            var prior = Interlocked.CompareExchange(ref _ownNodeVersionFloor, version, current);
            if (prior == current)
                return;
            current = prior;
        }
    }

    private void PruneRecentlyDeleted(DateTimeOffset nowUtc)
    {
        foreach (var kv in _recentlyDeleted)
        {
            if (nowUtc - kv.Value > RecentlyDeletedTtl)
                _recentlyDeleted.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>
    /// The debounce timer's actual work, reached only through <see cref="FlushCell"/>. Subscribe
    /// drives the flush — the observable returns Unit when all ops have settled; the disposable is
    /// retained so FlushOnDispose can wait on in-flight subscriptions during hub teardown. A
    /// faulted flush is unpersisted data — never silent.
    /// </summary>
    private void RunDebouncedFlush()
    {
        var sub = FlushPendingWrites().Subscribe(
            _ => { },
            ex => _logger?.LogWarning(ex,
                "Debounced flush of pending writes failed for {HubPath}", _hubPath));
        _pendingFlushSubscriptions.Add(sub);
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
            // 🚨 The callback reaches this source through a SEVERABLE CELL, never by capturing
            // `this`. Disposing a one-shot Timer does NOT immediately remove it from the
            // process-wide TimerQueue — the queue can keep the TimerQueueTimer (and therefore
            // its TimerCallback's captured state) until the due time passes. With `this`
            // captured, that residue is a live non-stack GC root
            // `TimerQueue → TimerQueueTimer → TimerCallback → MeshNodeTypeSource → Workspace
            // → MessageHub` for up to DebounceInterval after teardown — which is exactly what
            // MeshHubDisposalLeakTest samples, and it cannot tell a self-clearing TimerQueue
            // residue from a permanent static root. Measured: with `this` captured the ClrMD
            // probe finds 9 disposed-but-pinned hubs immediately after teardown and 0 once the
            // debounce interval has elapsed; through the cell it finds 0 immediately.
            // Same severable-indirection idiom as the console-capture fix pinned by
            // ConsoleCaptureExecutionContextLeakTest.
            var cell = _flushCell;
            _debounceTimer = new Timer(static state =>
            {
                // Null once the source is torn down (FlushOnDispose severs the cell), so a
                // queued-but-disposed timer that still fires is a no-op instead of a resurrection.
                if (state is FlushCell { Target: { } src }) src.RunDebouncedFlush();
            }, cell, DebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Resolves the freshest known state for a queued save: the entry from <see cref="_lastSaved"/>
    /// (this source's authoritative post-diff collection) when it is still present and carries a
    /// Version at-or-above the queued snapshot, else the queued snapshot itself. Never lets a stale
    /// queued node overwrite newer state — see <see cref="FlushPendingWrites"/>.
    /// </summary>
    private MeshNode CurrentStateFor(MeshNode queued)
    {
        if (_lastSaved.Instances.TryGetValue(queued.Id, out var live)
            && live is MeshNode current
            && current.Version >= queued.Version
            && string.Equals(current.Path, queued.Path, StringComparison.OrdinalIgnoreCase))
            return current;
        return queued;
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

        // 🚨 Flush the node's CURRENT state, never the enqueue-time snapshot. _pendingSaves is
        // populated by the ADD branch of UpdateImpl only; later UPDATES to the same node
        // deliberately bypass this queue (the own-node persistence sampler saves those), so a
        // queued entry is NEVER refreshed. ResetDebounceTimer re-arms the one-shot 200 ms timer on
        // EVERY UpdateImpl, so under sustained churn a create-time entry can sit here across many
        // subsequent acked writes and then be drained by FlushOnDispose at hub teardown — writing
        // the CREATE-time node over a durable row that has since advanced. That is a durable
        // rollback of acked writes (observed: a node at Version 12 / ApiKey "sk-v6" rolled back to
        // Version 2 / ApiKey "sk-v0" by an idle-recycle) and IStorageAdapter.Write has no
        // monotonic guard to catch it. _lastSaved is this source's authoritative post-diff
        // collection, so re-resolving the path against it makes the flush write what the node
        // actually IS at flush time; the queued snapshot is only the fallback for a node that has
        // since left the collection.
        var saves = new List<MeshNode>();
        foreach (var key in _pendingSaves.Keys.ToArray())
            if (_pendingSaves.TryRemove(key, out var node))
                saves.Add(CurrentStateFor(node));

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
    /// Seeds the workspace with the own MeshNode — from DURABLE STORAGE — and follows the
    /// routing-supplied stream for subsequent live updates.
    /// <para>
    /// The routing layer (Orleans <c>MessageHubGrain</c>, Monolith
    /// <c>MonolithRoutingService</c>) attaches an own-node observable via
    /// <see cref="OwnNodeStreamExtensions.WithOwnNodeStream"/> at hub instantiation. That
    /// stream carries LIVE updates (and, on Orleans, the enriched node whose non-serialisable
    /// <c>HubConfiguration</c> delegate storage cannot hold) — but it is NOT durable state:
    /// both of its legs are caches. <c>PathResolutionService</c> memoizes the resolved
    /// <c>AddressResolution</c> (including its MeshNode snapshot), invalidated only by the
    /// per-silo change feed; <c>MeshNodeStreamCache</c> replays its last seen value. A
    /// reactivation that adopted such a snapshot as its live own-node state loaded an
    /// arbitrarily old node — which the persistence sampler then wrote back OVER newer durable
    /// data. Observed in production shape as <c>Version=12 / ApiKey=sk-v6</c> →
    /// <c>Version=2 / ApiKey=sk-v0</c>: six acknowledged writes destroyed.
    /// </para>
    /// <para>
    /// So the seed is the durable row (<c>Doc/Architecture/MeshNodeVersioning.md</c>: "the node
    /// loads its persisted Version verbatim on activation"), MERGED with — not replaced by —
    /// the routing stream, and every emission passes <see cref="AcceptOwnNodeEmission"/>, which
    /// drops any node whose <see cref="MeshNode.Version"/> regresses below the highest already
    /// held. Merge (rather than Concat) is deliberate: a slow or faulted storage read can never
    /// delay or block activation — the routing stream still seeds, exactly as before — while a
    /// stale routing emission loses to the durable one on version.
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
        if (_ownNodeStream is not null)
            // Tag the source and filter DOWNSTREAM of the merge: Observable.Merge serialises
            // notifications through its gate, so the version bookkeeping and
            // BuildInstanceCollection stay single-threaded — a Where placed on each leg
            // instead would run on that leg's own thread, outside the gate.
            return Observable.Merge(
                    DurableSeed().Select(n => (Node: n, Durable: true)),
                    _ownNodeStream.Select(n => (Node: n, Durable: false)))
                .Where(e => AcceptOwnNodeEmission(e.Node, e.Durable))
                .Select(e => BuildInstanceCollection(e.Node));

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

    /// <summary>
    /// One-shot read of this node's DURABLE row — the authoritative activation seed.
    ///
    /// <para><b>This is a raw <see cref="IStorageAdapter.Read"/>, NOT
    /// <c>MeshNodeStreamExtensions.GetMeshNode</c>.</b> It reads the row directly out of the
    /// store; there is no owner round-trip, no routing, and therefore no
    /// <c>ReadTimeoutBehavior</c> in play. The distinction matters because the two APIs sit on
    /// opposite sides of the very hub this seed is bringing up: a mesh read of our OWN path
    /// during our own activation would be circular.</para>
    ///
    /// <para><b>It degrades; it never throws — deliberately.</b> This runs on the activation
    /// path of EVERY per-node hub. An OnError here propagates into the routing layer's
    /// activation subscription (<c>MessageHubGrain.OnActivateAsync</c> →
    /// <c>_hubReadyRaw.OnError</c> + <c>DeactivateOnIdle</c>), so a storage hiccup would stop
    /// being a read error and start being a PARKED node type — the outage class the new strict
    /// <c>GetMeshNode</c> timeout deliberately kept the compile path away from via
    /// <c>ReadTimeoutBehavior.EmitNull</c>. Same reasoning, same choice: an absent row (a
    /// brand-new node whose <c>CreateNodeRequest</c> is still in flight behind the init gate, a
    /// transient node, a read-only partition) emits <c>null</c> and is dropped, and a FAULT is
    /// logged at Warning with the exception and then dropped. Either way the routing stream
    /// still seeds and the hub comes up exactly as it did before this change.</para>
    ///
    /// <para><b>No wall-clock bound, on purpose.</b> The merge (not concat) in
    /// <c>Initialize</c> already makes a stalled read harmless: it cannot delay or block
    /// activation, it just never contributes. A <c>Timeout</c> would therefore change nothing
    /// about liveness — it would only be able to make things worse, because a cold-partition
    /// read under a boot storm legitimately takes tens of seconds and a budget short enough to
    /// be a useful signal would start SKIPPING the seed on exactly the slow activations that
    /// need it most. The stall does not go unguarded: <c>MonotonicWriteGuardStorageAdapter</c>
    /// is the independent backstop — a hub that never got its seed and adopts a stale snapshot
    /// still cannot roll the store back. That is why the fix is two changes and not one.</para>
    /// </summary>
    private IObservable<MeshNode?> DurableSeed()
    {
        if (_persistenceCore is null)
            return Observable.Empty<MeshNode?>();

        return _persistenceCore.Read(_hubPath, _workspace.Hub.JsonSerializerOptions)
            .Take(1)
            .Do(seed => _logger?.LogDebug(
                "MeshNodeTypeSource[{HubPath}]: durable activation seed {Outcome} (version={Version})",
                _hubPath, seed is null ? "found no row" : "read", seed?.Version))
            .Catch<MeshNode?, Exception>(ex =>
            {
                _logger?.LogWarning(ex,
                    "MeshNodeTypeSource[{HubPath}]: durable activation seed read FAILED — this hub falls back to "
                    + "the routing-supplied own-node stream alone, which is cache-backed and may be stale. The "
                    + "storage-level monotonic write guard remains the backstop against a rollback.",
                    _hubPath);
                return Observable.Empty<MeshNode?>();
            });
    }

    /// <summary>
    /// Own-node monotonicity gate: accepts an emission only when its
    /// <see cref="MeshNode.Version"/> is at or above the highest this hub has already
    /// adopted. Nulls (no node at this path yet) are dropped.
    ///
    /// <para><b>Strict regressions only.</b> An EQUAL version passes — a never-mutated node
    /// sits at its seed version forever, and content can legitimately change without a
    /// version-minting write path having run. Only a strictly-lower version is a stale
    /// snapshot, because every mint goes through <see cref="MeshNode.NextVersion"/>, which
    /// floors at <c>current.Version + 1</c>.</para>
    ///
    /// <para><b>Named escape hatch: delete-then-recreate.</b> A same-path recreate legitimately
    /// restarts at <c>Version = 1</c>. The mesh-scoped tombstone (<see cref="RecentlyDeletedRegistry"/>)
    /// is the framework's existing record of exactly that, so a tombstoned path resets the floor
    /// instead of dropping the emission. This mirrors the forward-only own-node refresh the
    /// change-notification handler in <c>MeshDataSourceExtensions.SubscribeToOwnDeletion</c>
    /// already applies ("a persisted snapshot may only REPLACE the in-RAM commit when it is
    /// STRICTLY NEWER").</para>
    ///
    /// <para><b>The durable seed replaces only when STRICTLY newer.</b> The routing-supplied node
    /// is ENRICHED — on Orleans it carries the <c>HubConfiguration</c> delegate and whatever else
    /// <c>ResolveHubConfiguration</c> computed, none of which survives a round-trip through
    /// storage. The durable read is asynchronous on a real backend, so it can land AFTER the
    /// routing stream already seeded at the SAME version; adopting it then would silently swap the
    /// enriched node for the raw row for no gain. So a durable emission is taken when nothing has
    /// been adopted yet, or when it genuinely carries a higher version — the same "strictly newer"
    /// rule the persisted-change refresh in <c>SubscribeToOwnDeletion</c> uses.</para>
    /// </summary>
    /// <param name="node">The emitted own-node candidate.</param>
    /// <param name="isDurableSeed">True for the activation read straight from storage.</param>
    private bool AcceptOwnNodeEmission(MeshNode? node, bool isDurableSeed)
    {
        if (node is null)
            return false;

        var floor = Volatile.Read(ref _ownNodeVersionFloor);

        if (isDurableSeed && _ownNodeAdopted != 0 && node.Version <= floor)
            return false;
        if (node.Version < floor)
        {
            if (_recentlyDeletedRegistry?.IsRecentlyDeleted(node.Path) ?? false)
            {
                _logger?.LogDebug(
                    "MeshNodeTypeSource[{HubPath}]: own-node emission version {Version} is below the floor "
                    + "{Floor}, but the path carries a delete tombstone — treating it as a recreate and "
                    + "resetting the floor.",
                    _hubPath, node.Version, floor);
                _ownNodeAdopted = 1;
                Interlocked.Exchange(ref _ownNodeVersionFloor, node.Version);
                return true;
            }

            _logger?.LogWarning(
                "MeshNodeTypeSource[{HubPath}]: DROPPED a stale own-node emission (Version={Version}) below the "
                + "version this hub already holds (Version={Floor}). The routing-supplied own-node stream is "
                + "cache-backed (path-resolution memo / mesh-node stream replay) and can replay a snapshot from "
                + "before a recycle; adopting it would let the persistence sampler write it back over newer "
                + "durable data.",
                _hubPath, node.Version, floor);
            return false;
        }

        RaiseOwnVersionFloor(node.Version);
        return true;
    }

    private InstanceCollection BuildInstanceCollection(MeshNode? rawNode)
    {
        var ownNode = rawNode != null ? ResolveJsonElementContent(rawNode) : null;

        // 🚨 Stamp this instance as ALREADY PERSISTED before it enters the workspace:
        // every state flowing through here arrived from persistence (the fallback
        // one-shot read) or from the routing-supplied own-node stream (fed by the
        // storage/catalog change feed) — both durable by construction. The persistence
        // sampler (MeshDataSourceExtensions.SubscribeToOwnDeletion) skips emissions
        // reference-identical to this snapshot so a pure load never writes storage
        // back (the initial-load echo that rewrote every FS file on activation and
        // persisted content-narrowing losses). Set BEFORE the collection is returned
        // so the stamp is in place when the workspace emits it to the sampler.
        if (_ownNodeCache != null)
            _ownNodeCache.PersistedSnapshot = ownNode;

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
            // Deserialize returned null (no exception) — content stays an untyped JsonElement.
            _logger?.LogWarning(
                "MeshNodeTypeSource: deserialized {Type} content to NULL for {Path} — content stays "
                + "an untyped JsonElement (every 'Content is {TypeName}' consumer will fail).",
                typeDef.Type.Name, node.Path, typeDef.Type.Name);
        }
        catch (Exception ex)
        {
            // 🚨 Surfaced at Warning (NOT Debug): a content-deserialization FAILURE leaves the node
            // as an untyped JsonElement, so every downstream `Content is {Type}` check fails — the
            // node "renders empty" and a reactive wait for the typed shape "times out" with no
            // visible cause (the secretly-errors-as-a-timeout class). The cause is real (a stale or
            // wrong assembly under type/version collision, or a schema mismatch), so it must be
            // visible in production logs, not hidden at Debug. Permanent on purpose.
            _logger?.LogWarning(ex,
                "MeshNodeTypeSource: FAILED to deserialize JsonElement content to {Type} for {Path} — "
                + "content stays an untyped JsonElement; downstream 'Content is {TypeName}' will fail.",
                typeDef.Type.Name, node.Path, typeDef.Type.Name);
        }

        return node;
    }
}
