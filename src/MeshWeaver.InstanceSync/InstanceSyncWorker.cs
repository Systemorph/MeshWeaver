using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Sockets;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Hosting.Persistence.Http;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.InstanceSync;

/// <summary>
/// The sync engine for ONE sync source (<c>{space}/_Sync/{sourceId}</c>) — the client side of
/// the bi-directional replication; the remote instance stays passive (just its MCP surface).
///
/// <para><b>Push</b>: the coordinator forwards every local change under the space; the worker
/// coalesces them into the durable <see cref="InstanceSyncConfig.PendingChanges"/> manifest
/// (written to the config node, so accumulation survives restarts AND an unreachable remote)
/// and drains the manifest to the remote whenever it is reachable. An unreachable remote flips
/// the source to <see cref="InstanceSyncStatus.Offline"/> and starts the reconnect probe
/// (backoff up to <see cref="InstanceSyncOptions.RetryMax"/>) — that probe IS the documented
/// offline-accumulation feature ("changes accumulate until the other instance can be reached
/// again"), not a recovery watchdog for a defect.</para>
///
/// <para><b>Pull</b>: a periodic reconciliation sweep lists the remote space (search hits carry
/// version + lastModified), fetches nodes that changed since the last sweep, and applies the
/// newer ones locally under the system identity (infrastructure write, like
/// <c>StaticRepoImporter</c>).</para>
///
/// <para><b>Echo/loop prevention</b>, two independent layers: (1) a pull-apply registers the
/// local path in a consume-once suppression registry BEFORE writing, so the resulting change-feed
/// event is swallowed instead of re-entering the manifest; (2) every push/pull first compares
/// content (<see cref="InstanceSyncService.ContentEquals"/>) and drops value-equal writes, so
/// any echo that slips through converges after one hop instead of ping-ponging.</para>
///
/// <para>🚨 Reactive end-to-end; the only asynchrony lives inside <see cref="IRemoteMeshClient"/>
/// (IoPool-bounded). Drains are serialised by a Subject → Sample → Concat pipeline (never a
/// lock/semaphore); config/manifest writes go through <c>stream.Update</c> on the owning hub.</para>
/// </summary>
public sealed class InstanceSyncWorker : IDisposable
{
    private readonly IMessageHub hub;
    private readonly InstanceSyncService service;
    private readonly IRemoteMeshClientFactory clientFactory;
    private readonly InstanceSyncOptions options;
    private readonly ILogger? logger;

    /// <summary>The space this worker replicates.</summary>
    public string SpacePath { get; }

    /// <summary>The sync-source id under <c>{space}/_Sync</c>.</summary>
    public string SourceId { get; }

    private string ConfigPath => InstanceSyncService.ConfigPath(SpacePath, SourceId);

    private readonly CompositeDisposable disposables = new();
    private readonly Subject<Unit> drainRequests = new();

    // Consume-once echo suppression: paths this worker just wrote locally from a pull-apply.
    // The very next change-feed event for the path is the echo of that write — swallow exactly
    // one. Instance state on a mesh-scoped worker (never static).
    private readonly ConcurrentDictionary<string, byte> appliedInbound = new();

    // Last remote (version, lastModified) seen per remote path — the pull sweep re-fetches a
    // node only when its stamp moved, so steady-state sweeps are one listing round-trip.
    private readonly ConcurrentDictionary<string, (long Version, DateTimeOffset LastModified)> lastSeenRemote = new();

    // The remote client, recreated whenever the URL/token change or a connect failure poisons
    // the cached connect promise (McpRemoteMeshClient replays a failed handshake forever).
    // Plain lock for a synchronous field swap only — never held across an await/subscribe.
    private readonly object clientLock = new();
    private IRemoteMeshClient? client;
    private (string Url, string Token)? clientKey;

    private readonly SerialDisposable retryProbe = new();
    private TimeSpan retryDelay;
    private volatile InstanceSyncConfig? lastKnownConfig;
    private volatile bool disposed;

    /// <summary>Creates the worker for one sync source; call <see cref="Start"/> to activate it.</summary>
    public InstanceSyncWorker(
        IMessageHub hub,
        InstanceSyncService service,
        IRemoteMeshClientFactory clientFactory,
        InstanceSyncOptions options,
        string spacePath,
        string sourceId,
        ILogger? logger = null)
    {
        this.hub = hub;
        this.service = service;
        this.clientFactory = clientFactory;
        this.options = options;
        this.logger = logger;
        SpacePath = spacePath;
        SourceId = sourceId;
        retryDelay = options.RetryInitial;
    }

    /// <summary>
    /// Activates the worker: the serialised drain pipeline, the pull-sweep loop, and an initial
    /// drain kick (which also runs the initial full replication when it hasn't happened yet).
    /// </summary>
    public void Start()
    {
        disposables.Add(retryProbe);

        // Serialised drain pipeline: bursts coalesce (Sample — unlike Throttle it cannot starve
        // under a sustained change stream), passes never overlap (Concat), failures are handled
        // inside RunDrain so the pipeline itself never terminates.
        disposables.Add(drainRequests
            .Sample(options.DrainDebounce)
            .Select(_ => RunDrain()
                .Catch<Unit, Exception>(ex =>
                {
                    logger?.LogWarning(ex, "Instance sync drain failed for {Config}", ConfigPath);
                    return Observable.Return(Unit.Default);
                }))
            .Concat()
            .Subscribe(
                _ => { },
                ex => logger?.LogError(ex, "Instance sync drain pipeline terminated for {Config}", ConfigPath)));

        // Pull-sweep loop: the next tick is scheduled only after the previous sweep completed
        // (Defer + Repeat), so sweeps never overlap and a slow remote never queues up.
        disposables.Add(PullLoop().Subscribe(
            _ => { },
            ex => logger?.LogError(ex, "Instance sync pull loop terminated for {Config}", ConfigPath)));

        RequestDrain();
    }

    /// <summary>Signals the drain pipeline (idempotent; coalesced by the Sample window).</summary>
    public void RequestDrain()
    {
        if (!disposed)
            drainRequests.OnNext(Unit.Default);
    }

    /// <summary>Called by the coordinator when the config node was created/edited — re-reads the
    /// config on the next drain (which also picks up URL/token changes via the client cache).</summary>
    public void OnConfigChanged() => RequestDrain();

    /// <summary>
    /// Called by the coordinator for every mesh change. Filters to syncable paths under the
    /// space, swallows the one echo of a pull-apply, appends to the durable manifest, and
    /// requests a drain.
    /// </summary>
    public void OnLocalChange(MeshChangeEvent evt)
    {
        if (disposed) return;
        if (!InstanceSyncService.IsSyncablePath(evt.Path, SpacePath)) return;
        if (appliedInbound.TryRemove(evt.Path, out _)) return;
        if (lastKnownConfig is { Direction: InstanceSyncDirection.PullOnly }) return;

        // System scope: feed callbacks carry no ambient AccessContext (fails closed otherwise).
        service.AsSystem(() => service.AppendPending(
                ConfigPath, new PendingChange(evt.Path, evt.Kind, evt.Version, evt.Timestamp)))
            .Subscribe(
                _ => RequestDrain(),
                ex => logger?.LogWarning(ex, "Failed to record pending change {Path} for {Config}", evt.Path, ConfigPath));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Drain (push direction)
    // ══════════════════════════════════════════════════════════════════════════

    private IObservable<Unit> RunDrain() =>
        service.AsSystem(() => service.ReadConfigAuthoritative(SpacePath, SourceId)).SelectMany(cfg =>
        {
            lastKnownConfig = cfg;
            if (cfg is null || disposed) return Observable.Return(Unit.Default);
            if (!cfg.Active) return SetStatus(cfg, InstanceSyncStatus.Paused);
            if (!cfg.IsConfigured) return SetStatus(cfg, InstanceSyncStatus.NotConfigured);
            if (cfg.Direction == InstanceSyncDirection.PullOnly)
            {
                // Pull-only sources push nothing: settle the status and drop anything a
                // config-read race appended to the manifest before the direction was known.
                if (cfg.PendingChanges.IsEmpty && cfg.Status == InstanceSyncStatus.Syncing)
                    return Observable.Return(Unit.Default);
                return UpdateConfigAsSystem(c => c with
                {
                    Status = InstanceSyncStatus.Syncing,
                    LastError = null,
                    PendingChanges = [],
                }).Select(_ => Unit.Default);
            }

            var remote = GetClient(cfg);
            var initial = cfg.InitialSyncAt is null
                ? RunInitialReplication(remote, cfg)
                : Observable.Return(Unit.Default);
            return initial
                .SelectMany(_ => DrainPending(remote, cfg))
                .Catch<Unit, Exception>(ex => OnSyncFailure(ex));
        });

    /// <summary>
    /// The initial full replication: pushes the space root first (creating the Space on the
    /// remote provisions its partition), then the remaining subtree with bounded concurrency,
    /// then stamps <see cref="InstanceSyncConfig.InitialSyncAt"/>.
    /// </summary>
    private IObservable<Unit> RunInitialReplication(IRemoteMeshClient remote, InstanceSyncConfig cfg)
    {
        var remoteSpace = RemoteSpaceOf(cfg);
        logger?.LogInformation("Initial replication of {Space} → {Url} ({RemoteSpace}) starting",
            SpacePath, cfg.RemoteUrl, remoteSpace);
        return SetStatus(cfg, InstanceSyncStatus.Initializing)
            .SelectMany(_ => service.AsSystem(() => service.SnapshotSpaceNodes(SpacePath)))
            .SelectMany(nodes =>
            {
                var root = nodes.Where(n => string.Equals(n.Path, SpacePath, StringComparison.Ordinal)).ToList();
                var rest = nodes.Where(n => !string.Equals(n.Path, SpacePath, StringComparison.Ordinal)).ToList();
                var pushRoot = root.Select(n => PushNode(remote, cfg, n)).Concat().DefaultIfEmpty(Unit.Default);
                var pushRest = rest.Select(n => PushNode(remote, cfg, n)).Merge(options.PushConcurrency).DefaultIfEmpty(Unit.Default);
                return pushRoot.LastAsync().SelectMany(_ => pushRest.LastAsync())
                    .Do(_ => logger?.LogInformation(
                        "Initial replication of {Space} → {Url} pushed {Count} node(s)",
                        SpacePath, cfg.RemoteUrl, nodes.Count));
            })
            .SelectMany(_ => UpdateConfigAsSystem(c => c with
            {
                InitialSyncAt = DateTimeOffset.UtcNow,
                LastSyncedAt = DateTimeOffset.UtcNow,
                Status = InstanceSyncStatus.Syncing,
                LastError = null,
            }))
            .Select(_ => Unit.Default);
    }

    /// <summary>Config write from worker context — always under the system identity, with the
    /// impersonation scope opened at THIS segment's subscribe so the capture-at-call never
    /// depends on an AsyncLocal surviving earlier pool hops.</summary>
    private IObservable<MeshNode> UpdateConfigAsSystem(Func<InstanceSyncConfig, InstanceSyncConfig> update) =>
        service.AsSystem(() => service.UpdateConfig(ConfigPath, update));

    /// <summary>
    /// Drains the manifest sequentially. Per-entry application failures keep the entry pending
    /// (visible via <see cref="InstanceSyncStatus.Error"/> + LastError — never silently dropped);
    /// a connectivity failure aborts the pass and is classified by <see cref="OnSyncFailure"/>.
    /// </summary>
    private IObservable<Unit> DrainPending(IRemoteMeshClient remote, InstanceSyncConfig cfg)
    {
        var pending = cfg.PendingChanges;
        if (pending.IsEmpty)
            return SetStatus(cfg, InstanceSyncStatus.Syncing);

        return pending
            .Select(p => DrainOne(remote, cfg, p).Select(ok => (Change: p, Ok: ok, Error: (string?)null))
                .Catch<(PendingChange Change, bool Ok, string? Error), Exception>(ex =>
                    IsConnectivityError(ex)
                        ? Observable.Throw<(PendingChange Change, bool Ok, string? Error)>(ex)
                        : Observable.Return((Change: p, Ok: false, Error: (string?)ex.Message))))
            .Concat()
            .ToList()
            .SelectMany(results =>
            {
                var drained = results.Where(r => r.Ok).Select(r => r.Change).ToList();
                var firstError = results.FirstOrDefault(r => !r.Ok).Error;
                if (firstError is not null)
                    logger?.LogWarning("Instance sync {Config}: {Failed} change(s) rejected by remote: {Error}",
                        ConfigPath, results.Count(r => !r.Ok), firstError);
                ResetRetry();
                return service.AsSystem(() => service.RemoveDrained(ConfigPath, drained))
                    .SelectMany(_ => UpdateConfigAsSystem(c => c with
                    {
                        Status = firstError is null ? InstanceSyncStatus.Syncing : InstanceSyncStatus.Error,
                        LastSyncedAt = drained.Count > 0 ? DateTimeOffset.UtcNow : c.LastSyncedAt,
                        LastError = firstError,
                    }))
                    .Select(_ => Unit.Default);
            });
    }

    private IObservable<bool> DrainOne(IRemoteMeshClient remote, InstanceSyncConfig cfg, PendingChange change)
    {
        var remotePath = InstanceSyncService.RemapPath(change.Path, SpacePath, RemoteSpaceOf(cfg));
        if (change.Kind == MeshChangeKind.Deleted)
            return DeleteRemote(remote, remotePath);

        // Push the node's CURRENT content; a node deleted since the change was observed
        // degrades to a delete.
        return hub.GetMeshNode(change.Path, TimeSpan.FromSeconds(15))
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .SelectMany(local => local is null
                ? DeleteRemote(remote, remotePath)
                : PushNode(remote, cfg, local).Select(_ => true));
    }

    private static IObservable<bool> DeleteRemote(IRemoteMeshClient remote, string remotePath) =>
        remote.Get(remotePath).SelectMany(existing => existing is null
            ? Observable.Return(true)
            : remote.Delete(remotePath).Select(_ => true));

    /// <summary>
    /// Upsert with two guards: value-equal remote content is left untouched (convergence), and a
    /// STRICTLY-NEWER remote node is NOT overwritten — newest-writer-wins, symmetric with the pull
    /// side (<see cref="PullOne"/>). This is what makes the two instances CONVERGE: without it, an
    /// older local push clobbered a newer remote, and since the pull only applies a strictly-newer
    /// remote, the sides then diverged with no self-heal. On the skip, the remote's newer version is
    /// brought local by the next pull sweep. Ties favour the push direction (matching PullOne).
    /// </summary>
    private IObservable<Unit> PushNode(IRemoteMeshClient remote, InstanceSyncConfig cfg, MeshNode local)
    {
        var remotePath = InstanceSyncService.RemapPath(local.Path, SpacePath, RemoteSpaceOf(cfg));
        var payload = InstanceSyncService.RebaseNode(local, remotePath);
        return remote.Get(remotePath).SelectMany(existing =>
        {
            if (existing is not null && service.ContentEquals(payload, existing))
                return Observable.Return(Unit.Default);
            if (existing is not null && existing.LastModified > local.LastModified)
                return Observable.Return(Unit.Default);   // remote is newer → let the pull bring it local
            return existing is null ? remote.Create(payload) : remote.Update(payload);
        });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Pull sweep (remote → local)
    // ══════════════════════════════════════════════════════════════════════════

    private IObservable<Unit> PullLoop() =>
        Observable.Defer(() => Observable.Timer(options.PullInterval)
                .SelectMany(_ => RunPullSweep().Catch<Unit, Exception>(ex =>
                {
                    logger?.LogWarning(ex, "Instance sync pull sweep failed for {Config}", ConfigPath);
                    return Observable.Return(Unit.Default);
                })))
            .Repeat(); // next tick scheduled only after the previous sweep completed — no overlap

    private IObservable<Unit> RunPullSweep() =>
        service.AsSystem(() => service.ReadConfigAuthoritative(SpacePath, SourceId)).SelectMany(cfg =>
        {
            lastKnownConfig = cfg;
            // Bidirectional pulls only after the initial push established the remote baseline;
            // pull-only sources have no push phase and sweep immediately.
            if (cfg is null || disposed || !cfg.Active || !cfg.IsConfigured
                || cfg.Direction == InstanceSyncDirection.PushOnly
                || (cfg.InitialSyncAt is null && cfg.Direction == InstanceSyncDirection.Bidirectional))
                return Observable.Return(Unit.Default);

            var remote = GetClient(cfg);
            var remoteSpace = RemoteSpaceOf(cfg);
            return ListRemote(remote, remoteSpace)
                .SelectMany(hits => hits
                    .Where(h => InstanceSyncService.IsSyncablePath(h.Path, remoteSpace))
                    .Where(HasRemoteStampMoved)
                    .Select(h => PullOne(remote, cfg, h))
                    .Merge(options.PushConcurrency)
                    .ToList())
                .SelectMany(applied =>
                {
                    ResetRetry();
                    var pulled = applied.Count(x => x);
                    return pulled == 0
                        ? Observable.Return(Unit.Default)
                        : UpdateConfigAsSystem(c => c with
                        {
                            LastSyncedAt = DateTimeOffset.UtcNow,
                            Status = c.Status == InstanceSyncStatus.Offline ? InstanceSyncStatus.Syncing : c.Status,
                            LastError = null,
                        }).Select(_ => Unit.Default);
                })
                .Catch<Unit, Exception>(ex => OnSyncFailure(ex));
        });

    /// <summary>
    /// Lists the remote space subtree (root + descendants). When the flat descendants listing
    /// is truncated at the search cap, falls back to a per-namespace recursive walk so large
    /// spaces are still fully enumerated; any level that STILL truncates is logged loudly —
    /// never a silent cap.
    /// </summary>
    private IObservable<IReadOnlyList<RemoteSearchHit>> ListRemote(IRemoteMeshClient remote, string remoteSpace)
    {
        var root = remote.Get(remoteSpace)
            .Select(n => n is null
                ? []
                : new[] { new RemoteSearchHit(remoteSpace, n.NodeType, n.Version, n.LastModified) }.ToList());
        var descendants = remote.Search($"path:{remoteSpace} scope:descendants", 200)
            .SelectMany(result => result.Truncated
                ? WalkRemoteNamespace(remote, remoteSpace)
                : Observable.Return(result.Hits));
        return root.CombineLatest(descendants,
            (r, d) => (IReadOnlyList<RemoteSearchHit>)r.Concat(d).ToList());
    }

    private IObservable<IReadOnlyList<RemoteSearchHit>> WalkRemoteNamespace(IRemoteMeshClient remote, string ns) =>
        remote.Search($"path:{ns} scope:children", 200).SelectMany(result =>
        {
            if (result.Truncated)
                logger?.LogWarning(
                    "Instance sync {Config}: remote namespace {Namespace} exceeds the search cap even per-level — "
                    + "some remote nodes were not swept this pass", ConfigPath, ns);
            var children = result.Hits;
            if (children.Count == 0)
                return Observable.Return((IReadOnlyList<RemoteSearchHit>)children);
            return children
                .Select(h => WalkRemoteNamespace(remote, h.Path))
                .Merge(options.PushConcurrency)
                .ToList()
                .Select(nested => (IReadOnlyList<RemoteSearchHit>)children.Concat(nested.SelectMany(x => x)).ToList());
        });

    private bool HasRemoteStampMoved(RemoteSearchHit hit) =>
        !lastSeenRemote.TryGetValue(hit.Path, out var seen)
        || seen.Version != hit.Version
        || seen.LastModified != hit.LastModified;

    /// <summary>
    /// Applies one remote change locally: newest-writer-wins by LastModified, value-equal
    /// content is dropped (convergence guard), and the local write registers the path in the
    /// consume-once suppression registry FIRST so its own change-feed echo never re-enters the
    /// manifest. Emits whether a local write happened.
    /// </summary>
    private IObservable<bool> PullOne(IRemoteMeshClient remote, InstanceSyncConfig cfg, RemoteSearchHit hit) =>
        remote.Get(hit.Path).SelectMany(remoteNode =>
        {
            if (remoteNode is null)
            {
                lastSeenRemote.TryRemove(hit.Path, out _);
                return Observable.Return(false);
            }
            lastSeenRemote[hit.Path] = (remoteNode.Version, remoteNode.LastModified);
            var localPath = InstanceSyncService.RemapPath(hit.Path, RemoteSpaceOf(cfg), SpacePath);
            return hub.GetMeshNode(localPath, TimeSpan.FromSeconds(15))
                .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
                .SelectMany(local =>
                {
                    if (local is not null && service.ContentEquals(local, remoteNode))
                        return Observable.Return(false);
                    // Newest writer wins; ties keep the local side (the push direction owns it).
                    if (local is not null && local.LastModified >= remoteNode.LastModified)
                        return Observable.Return(false);
                    var payload = InstanceSyncService.RebaseNode(remoteNode, localPath);
                    appliedInbound[localPath] = 1;
                    return ApplyLocal(payload)
                        .Do(_ => logger?.LogDebug("Instance sync pulled {Path} from {Url}", localPath, cfg.RemoteUrl))
                        .Select(_ => true)
                        .Do(_ => { }, ex => appliedInbound.TryRemove(localPath, out _));
                });
        });

    /// <summary>Applies a pulled node under the system identity — an infrastructure write, the
    /// same identity model as <c>StaticRepoImporter</c>. The suppression entry is registered by
    /// the caller before subscribing.</summary>
    private IObservable<MeshNode> ApplyLocal(MeshNode payload)
    {
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => hub.Observe<CreateOrUpdateNodeResponse>(new CreateOrUpdateNodeRequest(payload)).FirstAsync())
            .SelectMany(d => d.Message.Success
                ? Observable.Return(d.Message.Node!)
                : Observable.Throw<MeshNode>(new InvalidOperationException(
                    $"Applying pulled node {payload.Path} failed: {d.Message.Error}")));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Failure classification + reconnect probe
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Connectivity failures flip the source Offline, drop the (poisoned) client so the next
    /// attempt reconnects fresh, and schedule the reconnect probe with backoff — the manifest
    /// keeps accumulating meanwhile. Anything else is a real error: surfaced on the node
    /// (Status=Error + LastError), never swallowed.
    /// </summary>
    private IObservable<Unit> OnSyncFailure(Exception ex)
    {
        // A failure surfacing after disposal (an in-flight remote call cancelled by shutdown)
        // must not stamp the node or schedule a probe — the worker is already gone.
        if (disposed)
            return Observable.Return(Unit.Default);
        if (IsConnectivityError(ex))
        {
            logger?.LogWarning("Instance sync {Config}: remote unreachable ({Error}) — accumulating; next probe in {Delay}",
                ConfigPath, ex.Message, retryDelay);
            DropClient();
            var delay = retryDelay;
            retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, options.RetryMax.Ticks));
            retryProbe.Disposable = Observable.Timer(delay).Subscribe(_ => RequestDrain());
            return UpdateConfigAsSystem(c => c with
            {
                Status = InstanceSyncStatus.Offline,
                LastError = ex.Message,
            }).Select(_ => Unit.Default);
        }

        logger?.LogWarning(ex, "Instance sync {Config} failed", ConfigPath);
        return UpdateConfigAsSystem(c => c with
        {
            Status = InstanceSyncStatus.Error,
            LastError = ex.Message,
        }).Select(_ => Unit.Default);
    }

    private void ResetRetry()
    {
        retryDelay = options.RetryInitial;
        retryProbe.Disposable = Disposable.Empty;
    }

    /// <summary>Transport-level failures (remote down / DNS / timeout) anywhere in the chain.</summary>
    internal static bool IsConnectivityError(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or SocketException or TimeoutException
                or TaskCanceledException or OperationCanceledException
                or System.IO.IOException)
                return true;
            if (e is AggregateException agg && agg.InnerExceptions.Any(i => IsConnectivityError(i)))
                return true;
        }
        return false;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Client cache + misc
    // ══════════════════════════════════════════════════════════════════════════

    private string RemoteSpaceOf(InstanceSyncConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.RemoteSpace) ? SpacePath : cfg.RemoteSpace.Trim().Trim('/');

    private IRemoteMeshClient GetClient(InstanceSyncConfig cfg)
    {
        var key = (Url: cfg.RemoteUrl!.Trim(), Token: cfg.RemoteToken!.Trim());
        lock (clientLock)
        {
            if (client is not null && clientKey == key) return client;
            DisposeClientLocked();
            client = clientFactory.Create(key.Url, key.Token);
            clientKey = key;
            return client;
        }
    }

    private void DropClient()
    {
        lock (clientLock)
            DisposeClientLocked();
    }

    private void DisposeClientLocked()
    {
        if (client is IAsyncDisposable disposable)
            _ = disposable.DisposeAsync(); // best-effort; McpRemoteMeshClient's dispose never faults
        client = null;
        clientKey = null;
    }

    private IObservable<Unit> SetStatus(InstanceSyncConfig cfg, InstanceSyncStatus status)
    {
        if (cfg.Status == status && cfg.LastError is null)
            return Observable.Return(Unit.Default);
        return UpdateConfigAsSystem(c => c with { Status = status, LastError = null })
            .Select(_ => Unit.Default);
    }

    /// <summary>Stops the worker: drains no more, probes no more, drops the remote client.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        drainRequests.OnCompleted();
        drainRequests.Dispose();
        disposables.Dispose();
        DropClient();
    }
}
