using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Default <see cref="IMeshNodeStreamCache"/> — a per-path stream cache over
/// <c>workspace.GetMeshNodeStream(path)</c>. Holds ONE shared
/// <see cref="MeshNodeStreamHandle"/> per path in a concurrent dictionary.
/// Every consumer — the routing path, every per-instance hub of a NodeType,
/// <c>NodeTypeEnrichmentHelpers</c>, compile-activity hubs writing terminal
/// state, path-resolution lookups — goes through that ONE handle. Reads
/// (<c>GetStream</c>) and writes (<c>Update</c>) share the same
/// underlying stream, so an update is always visible to every reader.
///
/// <para>The handle is opened on the mesh hub's workspace under
/// <see cref="AccessService.ImpersonateAsSystem"/> — that's correct for the
/// system-internal infrastructure subscription. Per-user enforcement lives
/// at the <c>GetStream</c> seam: the cache evaluates the current user's
/// effective permissions on the path LOCALLY via the security service
/// (<c>hub.GetEffectivePermissions</c> → <c>PermissionEvaluator</c>), whose
/// scope walk picks up the access defined on the partition / main node (so
/// "who can read the main node can read all its satellites"); only when the
/// result carries <see cref="Permission.Read"/> does the gated observable
/// forward the upstream emissions. No round-trip to the leaf path's own hub —
/// a satellite / cell sub-path with no hub of its own no longer wedges the
/// subscription. Per-(path,user) results are cached for <see cref="AccessTtl"/>.</para>
///
/// <para><b>Never go around the cache.</b> An ad-hoc
/// <c>workspace.GetRemoteStream(...)</c> from some other hub is a SEPARATE
/// stream instance; updating it is "lost" — never seen by the readers of the
/// cached stream (this was the bug behind compile state never landing on a
/// NodeType's MeshNode). Non-owning hubs MUST use <c>Update</c>.</para>
///
/// <para><b>No side-effects on emission.</b> The cache does not kick
/// compilation — opening the stream activates the per-NodeType hub via the
/// <c>SubscribeRequest</c>, and that hub's OWN compile watcher
/// (<c>NodeTypeCompilationHelpers.InstallCompileWatcher</c>) flips
/// <c>CompilationStatus = Pending</c> on its OWN stream only on explicit
/// user-driven <c>RequestedReleaseAt</c> flips.</para>
/// </summary>
internal sealed class MeshNodeStreamCache : IMeshNodeStreamCache, IDisposable
{
    // 0 = live, 1 = disposed. Dispose fires from BOTH the cacheHub disposal
    // hook (silo goes down → mesh hub disposes its hosted cache hub) AND the
    // DI container tearing down this singleton. Interlocked guard makes the
    // teardown idempotent so the second caller is a no-op.
    private int _disposed;

    /// <summary>One cache entry: the updatable handle plus the shared,
    /// replay-cached read view over it. The Shared observable is the raw
    /// system-side stream; per-user access gating is applied in
    /// <c>GetStream</c> before each subscriber consumes it.</summary>
    private sealed record Entry(MeshNodeStreamHandle Handle, IObservable<MeshNode> Shared, IDisposable HydrationSub);

    /// <summary>
    /// Validity window for a cached <c>(path,user) → Permission</c> result. A
    /// hit within this window short-circuits the local permission evaluation; a
    /// miss evaluates <c>hub.GetEffectivePermissions</c> and caches the result.
    /// Trade-off: permission revocations propagate after at most <c>AccessTtl</c>
    /// — short enough for interactive UX, long enough that <c>GetStream</c> hot
    /// paths don't re-walk the scope hierarchy every emission. The value matches
    /// the canonical AccessControl cache TTL used elsewhere in the codebase (30s).
    /// </summary>
    private static readonly TimeSpan AccessTtl = TimeSpan.FromSeconds(30);

    private readonly IMessageHub meshHub;
    private readonly IMessageHub cacheHub;
    private readonly ILogger<MeshNodeStreamCache> logger;

    // 🚨 Lazy<Entry> wraps the factory because ConcurrentDictionary.GetOrAdd
    // is NOT threadsafe for compound operations: under contention the factory
    // delegate runs more than once, the losing values are discarded, but any
    // side effects (here: opening an upstream SubscribeRequest +
    // ReplaySubject.Connect()) have already fired. The losing Entry's
    // subscription is orphaned and silently keeps consuming from the source.
    // Lazy<T>(ThreadSafety.ExecutionAndPublication) guarantees the factory
    // runs at most once per key, even when multiple GetOrAdd calls race.
    private readonly ConcurrentDictionary<string, Lazy<Entry>> _streams = new();
    private readonly ConcurrentDictionary<(string Path, string UserId), AccessEntry> _access = new();

    // 🚨 STORM BREAKER / negative cache. A read whose owner answers NotFound /
    // DeliveryFailure (the node does not exist) caches that FAILURE here with an
    // exponential-backoff window. While the window is OPEN, GetStreamRaw fast-fails
    // by replaying the cached error WITHOUT re-opening an upstream SubscribeRequest,
    // so a re-subscribing caller — a short-lived exec hub that bypasses the per-path
    // Lazy<Entry> dedup, or anything that re-reads an absent optional node on a loop —
    // can NOT hammer RoutingGrain with the same NotFound. That resubscribe-storm
    // starved the portal/<user> action block until unrelated SubscribeRequests went
    // stale >30s and the circuit FROZE (2026-06-09: AgentChatClient.Initialize re-read
    // {user}/_Provider/_Selection — absent for pre-existing users — every streaming
    // rebuild). The PRIMARY fix is to read optional nodes via GetQuery (empty-on-absent,
    // never NotFound); this breaker is the framework backstop so NO point-access can
    // storm, present or future.
    //
    // Self-healing, NEVER a watchdog: the window simply EXPIRES — the next NATURAL read
    // after it elapses re-probes the owner exactly once. There is NO timer that
    // re-subscribes on its own (an auto-resubscribe watchdog is exactly what caused the
    // 2026-06-08 prod outage; this only ever evicts, never re-subscribes). A successful
    // read clears the entry immediately. Consecutive failures grow the window
    // (StormBaseCooldown · 2^(n-1), capped at StormMaxCooldown); crossing
    // StormFailThreshold logs ONE "[STORM-BREAKER] suppressing" warning so the storm is
    // visible in App Insights without the per-failure log flood.
    private readonly ConcurrentDictionary<string, NegativeEntry> _negative = new();
    private sealed record NegativeEntry(Exception Error, int FailCount, DateTimeOffset OpenUntil);
    private static readonly TimeSpan StormBaseCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StormMaxCooldown = TimeSpan.FromMinutes(5);
    private const int StormFailThreshold = 5;

    // In-flight cross-hub write subscriptions that have OUTLIVED their queue slot. The
    // per-path Update queue advances on a bounded signal (QueueAdvanceBound) so a lost
    // owner response can't starve retries, but each write's subscription to the owner's
    // PatchDataResponse must keep running to deliver the real terminal (value / RLS
    // denial) to its caller. Tracked here so a mid-flight cache disposal tears them down;
    // each self-removes on its own terminal so this never accumulates.
    private readonly ConcurrentDictionary<IDisposable, byte> _inflightWrites = new();

    // 🚨 How long the per-path serial Update queue waits for the CURRENT write's first
    // owner signal before letting the NEXT queued write proceed. After the RLS commit
    // switched entry.Handle.Update to UpdateRemote (which awaits the owner's
    // PatchDataResponse, up to 30s), a lost response — the owner mid-dispose handles the
    // patch but its reply never routes back — blocked the queue for the full 30s and
    // starved every retry (the ResubscribeOnOwnerDispose deadlock). This bound caps that.
    // It sits above a normal owner round-trip (ms-to-low-seconds) so a healthy write
    // still advances the queue on its real terminal, not the bound — the queue only
    // "gives up waiting" for a genuinely stuck/lost response. The caller's result is
    // unaffected: it still receives the real terminal whenever it arrives.
    private static readonly TimeSpan QueueAdvanceBound = TimeSpan.FromSeconds(5);

    // 🚨 Per-path serial Update queue. Concurrent mirror-side Updates on the
    // SAME path race their `current` snapshot — each call's lambda runs against
    // the same initial state, so each computes a patch with field overrides
    // that REPLACE rather than merge. RFC 7396 merges JSON objects key-by-key
    // (safe for ImmutableDictionary), but REPLACES JSON arrays (catastrophic
    // for ImmutableList — the owner sees only the last patch's array value,
    // every earlier append lost). Symptom: 3 rapid SubmitMessage calls land
    // only 1 entry in MeshThread.UserMessageIds at the owner.
    //
    // Fix: serialize Update calls per path on the MIRROR side via Concat
    // over a Subject. Each call appends a cold observable; Concat subscribes
    // them serially — call N+1's Handle.Update only runs after call N's
    // Update completes, so call N+1's Take(1) on the remote stream sees call
    // N's echo (the cache's shared stream was updated by the patch landing on
    // the owner). Result: each lambda computes its diff against the freshest
    // state, and no two patches carry overlapping array replacements.
    //
    // Cost: per-path Update throughput drops from "parallel posts" to "one
    // round-trip per Update". Acceptable because (a) the OWNER serialized
    // anyway, so the apparent parallelism was illusory; (b) the only paths
    // with multi-write contention are thread/inbox nodes whose single-digit-
    // per-second write rate is far below the round-trip ceiling.
    //
    // Storage: `MemoryCache` with 10-minute sliding expiration. The queue
    // is reusable per path but unbounded retention would leak Subjects for
    // every node ever written. Sliding expiry tears down the Subject (and
    // its Concat subscription) for paths quiet for 10 minutes — a fresh
    // write recreates a fresh queue, no behaviour change for the caller.
    // Eviction callback completes the Subject so the Concat chain unwinds.
    private readonly MemoryCache _updateQueues = new(new MemoryCacheOptions
    {
        // No size limit — we trim by time, not count.
    });

    private static readonly TimeSpan UpdateQueueSlidingExpiration = TimeSpan.FromMinutes(10);

    private sealed record UpdateQueueEntry(Subject<UpdateRequest> Subject, IDisposable ConcatSubscription);

    private readonly record struct UpdateRequest(
        Func<MeshNode, MeshNode> Update,
        ReplaySubject<MeshNode> Result,
        string Path,
        long Seq,
        DateTimeOffset EnteredAt,
        // Non-null ⇒ this is an OVERWRITE (ChangeType.Full of the whole node), not a field-merge
        // Update. The Update func is ignored in that case. See Overwrite/OverwriteRaw.
        MeshNode? FullNode = null);

    /// <summary>Cached effective-permission probe with expiry.</summary>
    private sealed record AccessEntry(Permission Permissions, DateTimeOffset ValidUntil);

    public MeshNodeStreamCache(IMessageHub meshHub, ILogger<MeshNodeStreamCache> logger)
    {
        this.meshHub = meshHub;
        this.logger = logger;

        // 🚨 Dedicated cache hub at a PROCESS-UNIQUE address keyed by the
        // parent mesh hub's Address Id. The `cache` address-type is declared
        // as stream-routed at static-init time in
        // MeshConfiguration.DefaultStreamRoutedAddressTypes — silo's
        // RoutingGrain sees that and dispatches via memory stream rather
        // than grain activation. The cache hub follows the Portal pattern
        // (PortalApplication.DefaultPortalConfig) and registers itself
        // with the routing service in WithInitialization so its memory-
        // stream subscription wires up before any reader subscribes.
        //
        // 🚨 Process-uniqueness is critical: if the silo's and the client's
        // cache hubs share the same address (`cache/mesh-node-cache`), both
        // subscribe to the same cluster-wide Orleans memory stream — so a
        // silo-side response to a client-initiated SubscribeRequest may be
        // delivered to the silo's cache hub instead of the client's. The
        // wrong cache hub then has no sync sub-hub for the incoming
        // DataChangedEvent's StreamId, RouteStreamMessage returns
        // request.Ignored(), and the client times out. Keying by the mesh
        // hub's Id (a per-process guid) guarantees each process's cache hub
        // gets its own memory-stream subscription. See
        // Doc/Architecture/OrleansTestRoutingPattern.md.
        var routingService = meshHub.ServiceProvider.GetRequiredService<IRoutingService>();
        var cacheAddress = new Address("cache", meshHub.Address.Id);
        cacheHub = meshHub.GetHostedHub(
            cacheAddress,
            config => config
                // 🚨 Process-wide INFRASTRUCTURE hub (its own `cache/{meshId}` address, never a user).
                // It MUST post as System, not the default User (MessageHubConfiguration.PostingIdentity
                // = User). Its hosted sync sub-hubs inherit this identity (SynchronizationStream
                // .WithPostingIdentity(Host.Configuration.PostingIdentity)); without it their background
                // UpdateStreamRequest posts are "User but no user" → fail the never-null AccessContext
                // guard ("hub=sync/… UpdateStreamRequest … no AccessContext") in a storm. This is the
                // FALLBACK identity only — genuine user reads/writes still carry the real user (the write
                // primitive + JsonSynchronizationStream's `isRealUser ? SwitchAccessContext(ambient) :
                // ImpersonateAsSystem()`), so per-user RLS is unaffected. Same infra identity storage
                // declares (DataSourceWithStorage: WithPostingIdentity(PostingIdentity.System)).
                .WithPostingIdentity(PostingIdentity.System)
                // 🚨 Cache hub is domain-type-agnostic by design: its TypeRegistry
                // knows ONLY framework types (MeshNode, MeshNodeReference inherited
                // from the parent mesh hub) and treats MeshNode.Content as
                // JsonElement. Callers that need typed Content pass their own
                // JsonSerializerOptions through the IMeshNodeStreamCache.GetStream /
                // Update overloads; the framework converts JsonElement ↔ typed
                // Content using the caller's polymorphic resolver. This decouples
                // the process-singleton cache from every domain type a tenant
                // happens to register.
                .AddData()  // IWorkspace registration so GetEntry can build the stream handle
                .WithInitialization(hub =>
                    hub.RegisterForDisposal(routingService.RegisterStream(hub))),
            HostedHubCreation.Always)!;

        // Register full teardown on the cache hub so the cache releases ALL
        // its state at the hub's Shutdown entry (before Quiescing). The cache
        // hub owns the cache's lifetime; when the silo/mesh goes down it
        // disposes this hosted cache hub, which cancels every upstream
        // SubscribeRequest AND every per-path update-queue Concat subscription
        // the cache opened — so the leak detector sees a clean response-subjects
        // set at test-class dispose. The cache is ALSO IDisposable so the DI
        // container disposes it on container teardown; the _disposed guard
        // makes whichever fires second a no-op.
        cacheHub.RegisterForDisposal(_ => Dispose());
    }

    /// <summary>
    /// Releases every subscription and rooted subject the cache holds. Fires
    /// when the silo/mesh goes down (cacheHub disposal) and on DI container
    /// teardown (IDisposable). Idempotent via the <see cref="_disposed"/> guard.
    /// </summary>
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
            return; // already torn down by the other disposal path

        // 1. Per-path hydration: cancel the upstream SubscribeRequest so the
        //    owning node hub's response-subject is released. (The Entry's
        //    Handle is a stateless factory — it owns nothing; the HydrationSub
        //    IS the live subscription.) Without this every cache.GetStream(path)
        //    leaks a long-lived SubscribeRequest into the mesh hub's
        //    responseSubjects and the leak detector flags it at dispose.
        foreach (var (path, lazyEntry) in _streams)
        {
            if (!lazyEntry.IsValueCreated) continue;
            try
            {
                lazyEntry.Value.HydrationSub.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "MeshNodeStreamCache: error disposing hydration subscription for {Path}",
                    path);
            }
        }
        _streams.Clear();

        // 2. Per-path update queues: Clear() fires every entry's
        //    post-eviction callback (ConcatSubscription.Dispose +
        //    Subject.OnCompleted) so the serial-update Concat pipelines stop
        //    keeping owner response-subjects rooted; Dispose() then stops the
        //    MemoryCache's expiration-scan timer (which otherwise pins this
        //    singleton — and through it meshHub/cacheHub — past mesh disposal).
        try { _updateQueues.Clear(); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "MeshNodeStreamCache: error clearing update queues");
        }
        try { _updateQueues.Dispose(); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "MeshNodeStreamCache: error disposing update-queue cache");
        }

        // 3. Permission probe cache — drop the cached (path,user)⇒Permission
        //    entries so nothing roots the disposed mesh's identities.
        _access.Clear();

        // 4. In-flight cross-hub write subscriptions that outlived their queue slot
        //    (awaiting a slow/lost owner PatchDataResponse). Each normally self-removes
        //    on its terminal; tear down any still pending so they don't root the disposed
        //    mesh past shutdown.
        foreach (var inflight in _inflightWrites.Keys)
        {
            try { inflight.Dispose(); } catch { /* best-effort */ }
        }
        _inflightWrites.Clear();

        // 5. Storm-breaker negative cache — drop the cached failure windows so
        //    nothing roots the disposed mesh's exceptions/identities.
        _negative.Clear();
    }

    private Entry GetEntry(string path) =>
        _streams.GetOrAdd(path, p => new Lazy<Entry>(() =>
        {
            logger.LogDebug("MeshNodeStreamCache: opening shared stream for {Path}", p);
            // 🚨 Bypass the cache when opening our OWN upstream — otherwise
            // GetMeshNodeStream(workspace, path) auto-redirects back into the
            // cache and we'd recurse forever waiting for ourselves. Use the
            // dedicated CACHE HUB's workspace so the SubscribeRequest's Sender
            // is `cache/mesh-node-cache` — a static stream-routed address the
            // silo's RoutingGrain delivers responses to via memory stream.
            var handle = cacheHub.GetWorkspace().GetMeshNodeStreamBypassCache(p);
            // Replay(1) + eager .Connect() under the sanctioned cache identity:
            // the upstream SubscribeRequest opens ONCE under
            // MeshNodeCacheIdentity.Address ("cache/mesh-node-cache"). The cache
            // is process-wide infrastructure serving every reader (routing,
            // NodeType activation, path-resolution, etc.) — none of them know
            // which user triggered the read, and the cache emission fans out
            // to subscribers who each apply their own AccessContext at
            // consumption time (this read is system-internal, not
            // user-attributable).
            //
            // The cache identity is granted ONLY Permission.Read by
            // SecurityService — it cannot Create / Update / Delete. Tests in
            // MeshWeaver.Security.Test/MeshNodeCacheIdentityTest verify that
            // writes under this identity are properly denied, so the narrow
            // grant survives future refactors.
            //
            // Why not ImpersonateAsSystem: system-security grants Permission.All
            // unconditionally, which is broader than the cache needs. The
            // dedicated identity narrows the bypass to exactly Read on the
            // cache's hydration path.
            // Why not ImpersonateAsHub(meshHub): stamps the mesh hub's own
            // address (mesh/{guid}) as the principal, but no AccessAssignment
            // grants that principal access to partition nodes — owners' RLS
            // would deny with "user 'mesh/{guid}' lacks Read permission".
            //
            // Eager Connect() (vs AutoConnect(1)) keeps the upstream alive
            // for process lifetime — no RefCount churn, identity captured
            // deterministically at cache-creation rather than at first
            // random consumer.
            // Build the cached subject explicitly so we can wrap it with
            // Subject.Synchronize — RX subjects are NOT threadsafe across
            // multiple producers (and even with one producer, race between
            // OnNext and Subscribe can be observed). Subject.Synchronize
            // gives a single per-subject gate that the framework relies on.
            var inner = new System.Reactive.Subjects.ReplaySubject<MeshNode>(1);
            var synced = System.Reactive.Subjects.Subject.Synchronize(inner);
            var accessService = meshHub.ServiceProvider.GetService<AccessService>();

            // Subscribe the upstream handle DIRECTLY to the shared subject — NO retry, NO Delay.
            // The old missing-node retry looped `Observable.Empty().Delay(200ms).Repeat()` up to
            // 30× on a transient "No node found" path-resolution lag; each Repeat re-ran the whole
            // SubscribeRequest round-trip, so under load it burned tens of seconds of dead air per
            // reader (the frozen-compile symptom: 30 × ~1.4s ≈ 42s). Reads are reactive end-to-end
            // — the handle replays the node the instant it resolves; a genuinely missing node
            // propagates OnError to subscribers immediately rather than after a polling budget.
            IDisposable hydrationSub;
            if (accessService is not null)
            {
                using (accessService.SwitchAccessContext(MeshNodeCacheIdentity.Context))
                    hydrationSub = ((IObservable<MeshNode>)handle).Subscribe(synced);
            }
            else
            {
                hydrationSub = ((IObservable<MeshNode>)handle).Subscribe(synced);
            }
            // Storm-breaker bookkeeping: a lightweight second observer on the SAME
            // ReplaySubject (opens NO additional upstream). First value ⇒ clear any
            // negative-cache entry (the node resolved); terminal error ⇒ record the
            // failure + backoff window via RecordNegative. Composed with hydrationSub
            // so both tear down together on eviction / mesh disposal.
            var bookkeeping = inner.AsObservable().Subscribe(
                _node => _negative.TryRemove(p, out _),
                ex => RecordNegative(p, ex));
            var disposal = new System.Reactive.Disposables.CompositeDisposable(hydrationSub, bookkeeping);
            // Store the disposal on the Entry so the mesh hub's pre-Quiescing
            // disposal hook (registered in the ctor) can cancel it. Without
            // this, every cache.GetStream(path) leaks a long-lived
            // SubscribeRequest into the mesh hub's responseSubjects and the
            // test base's leak detection flags it at dispose.
            return new Entry(handle, inner.AsObservable(), disposal);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>
    /// Records an upstream read failure for <paramref name="path"/> in the storm
    /// breaker's negative cache with an exponential-backoff window
    /// (<see cref="StormBaseCooldown"/> · 2^(n-1), capped at
    /// <see cref="StormMaxCooldown"/>). Crossing <see cref="StormFailThreshold"/>
    /// consecutive failures logs ONE warning. Never re-subscribes — purely records
    /// state that <see cref="GetStreamRaw"/> consults to fast-fail.
    /// </summary>
    private void RecordNegative(string path, Exception error)
    {
        var priorFails = _negative.TryGetValue(path, out var existing) ? existing.FailCount : 0;
        var failCount = priorFails + 1;
        // 2^(n-1) capped at 20 shifts (~12 days) before the Min — StormMaxCooldown
        // is the real ceiling; the cap just keeps the intermediate from overflowing.
        var backoffTicks = Math.Min(
            StormBaseCooldown.Ticks * (1L << Math.Min(failCount - 1, 20)),
            StormMaxCooldown.Ticks);
        _negative[path] = new NegativeEntry(error, failCount, DateTimeOffset.UtcNow + TimeSpan.FromTicks(backoffTicks));
        if (failCount == StormFailThreshold)
            logger.LogWarning(
                "[STORM-BREAKER] Suppressing re-probe of '{Path}' after {FailCount} consecutive access failures: {Error}. "
                + "Reads AND writes fast-fail until the backoff window elapses. A point node-access to a node that does "
                + "not exist is a defect — read optional nodes via GetQuery (empty-on-absent), not GetMeshNodeStream(exactPath); "
                + "bring a new node into being with CreateNode, not Update.",
                path, failCount, error.Message);
    }

    /// <summary>
    /// True when an owner failure means the node/hub does not exist (NotFound / activation
    /// failed) — the only failure class the storm-breaker suppresses on the WRITE path. RLS
    /// denials and transient routing errors are excluded so an existing-but-busy node is never
    /// falsely blocked from writes.
    /// </summary>
    private static bool IsMissingNodeFailure(Exception error) =>
        error.Message.Contains("No node found", StringComparison.OrdinalIgnoreCase)
        || error.Message.Contains("activation failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a per-user access-gated view of the cached shared stream. The
    /// gate is enforced by evaluating the current user's effective permissions
    /// on the path LOCALLY via the security service
    /// (<c>hub.GetEffectivePermissions</c>); the result is cached for
    /// <see cref="AccessTtl"/> per <c>(path,user)</c>. On
    /// <see cref="Permission.Read"/> ⇒ the upstream observable is returned
    /// directly; on missing Read ⇒ the observable terminates with
    /// <see cref="UnauthorizedAccessException"/>.
    ///
    /// <para>Authoritative source: the same <c>PermissionEvaluator</c> every
    /// other access decision uses. Its scope walk evaluates access defined on
    /// the partition / main node, which by design covers every satellite under
    /// it — so the gate never needs to reach the leaf path's own hub (which may
    /// not exist for a satellite / cell sub-path).</para>
    /// </summary>
    // 🚨 PRIVATE raw read — emits untyped JsonElement Content. The interface no
    // longer exposes a bare GetStream(string): callers MUST pass
    // JsonSerializerOptions (GetStream(path, options)) so Content is deserialized
    // to its domain type. A bare read fed `node.Content as MyType` → null and the
    // wedged-thread / never-dispatching-watcher bug class.
    private IObservable<MeshNode> GetStreamRaw(string path)
    {
        // STORM BREAKER: while this path's negative-cache window is open, fast-fail
        // by replaying the cached owner error WITHOUT opening an upstream
        // SubscribeRequest. See _negative. Once the window elapses, drop the errored
        // Entry so the NEXT read re-probes the owner exactly once (re-probe is driven
        // by a real read, never an auto-resubscribe). FailCount is retained so a
        // repeat failure backs off further; a success clears it (see GetEntry).
        if (_negative.TryGetValue(path, out var neg))
        {
            if (neg.OpenUntil > DateTimeOffset.UtcNow)
                return Observable.Throw<MeshNode>(neg.Error);
            if (_streams.TryRemove(path, out var staleLazy))
            {
                try { if (staleLazy.IsValueCreated) staleLazy.Value.HydrationSub.Dispose(); }
                catch (Exception ex) { logger.LogDebug(ex, "MeshNodeStreamCache: error disposing stale entry for {Path}", path); }
            }
        }

        var shared = GetEntry(path).Shared;

        var accessService = meshHub.ServiceProvider.GetService<AccessService>();
        if (accessService is null)
            return shared; // No AccessService (minimal test fixture) — pass-through.

        // RLS not installed on this mesh ⇒ no gate. The EffectivePermissionsDelegate
        // is wired up by AddRowLevelSecurity; without it GetEffectivePermissions has
        // no evaluator and the feature makes no sense.
        if (meshHub.Configuration.Get<EffectivePermissionsDelegate>() is null)
            return shared;

        // Capture the caller's identity synchronously, on the caller's thread,
        // before the cold pipeline runs. The CarryAccessContext wrap re-stamps
        // AsyncLocal on each emission so downstream Subscribe callbacks see
        // the same identity regardless of where the emission lands.
        var captured = accessService.Context ?? accessService.CircuitContext;
        // No REAL user identity ⇒ no per-user gate. Three cases pass through to the
        // cache's shared upstream (which already opened under the Read-only cache
        // identity — see GetEntry): (1) null/empty context (background / system path);
        // (2) a hub-shaped principal (sync/, mesh/, node/, activity/, portal/) that
        // LEAKED onto AsyncLocal from a workspace emission scheduler. A hub address is
        // NOT a user — evaluating its permissions yields Permission.None and
        // GateOnRead would throw "user 'sync/…' lacks Read", the symptom pinned by
        // SubscribeRequestIdentityRoutingTest.SubscribeRequest_FromSyncHubPrincipal_FallsBackToSystem.
        // This mirrors the SubscribeWith identity fallback (JsonSynchronizationStream)
        // and the SetContext leak guard: hub principals fall back to System, which the
        // cache upstream already represents. Per-user enforcement applies to genuine
        // users only; they still hit the gate below.
        if (captured is null || string.IsNullOrEmpty(captured.ObjectId)
            || AccessService.LooksLikeHubPrincipal(captured.ObjectId))
            return shared;

        // A path whose first segment is a NodeType name (e.g. "Thread",
        // "AccessAssignment") is a type-definition node, not user-partition data —
        // the per-user gate only makes sense for real partition-rooted node paths.
        // If the first segment is empty or matches a known NodeType name, skip the
        // gate entirely (pass through to the system-read shared upstream).
        if (LooksLikeNodeTypePath(path))
            return shared;

        return ProbeEffectivePermissions(path, captured)
            .SelectMany(perms => GateOnRead(perms, shared, path, captured))
            .CarryAccessContext(accessService);
    }

    private static IObservable<MeshNode> GateOnRead(
        Permission perms, IObservable<MeshNode> shared, string path, AccessContext user)
    {
        if (perms.HasFlag(Permission.Read))
            return shared;
        return Observable.Throw<MeshNode>(new UnauthorizedAccessException(
            $"User '{user.ObjectId}' lacks Read permission on '{path}'"));
    }

    /// <summary>
    /// Evaluates <paramref name="captured"/>'s effective permissions on
    /// <paramref name="path"/> LOCALLY via the security service
    /// (<c>hub.GetEffectivePermissions</c> → <c>PermissionEvaluator</c>), cached per
    /// <c>(path,user)</c> for <see cref="AccessTtl"/>. Backs the read gate
    /// (<see cref="GetStreamRaw"/>); writes are gated separately by the owning hub's
    /// <c>[RequiresPermission(Update)]</c> on <c>PatchDataRequest</c>. No hub
    /// round-trip — the scope walk resolves access defined on the partition / main
    /// node, so a satellite / cell sub-path with no hub of its own can never wedge
    /// the gate.
    /// </summary>
    private IObservable<Permission> ProbeEffectivePermissions(string path, AccessContext captured)
    {
        var key = (Path: path, UserId: captured.ObjectId);
        return Observable.Defer(() =>
        {
            // TTL cache hit ⇒ short-circuit the evaluation.
            if (_access.TryGetValue(key, out var cached)
                && cached.ValidUntil > DateTimeOffset.UtcNow)
            {
                return Observable.Return(cached.Permissions);
            }

            // Miss ⇒ evaluate the user's effective permissions LOCALLY via the
            // security service (PermissionEvaluator), NOT a GetPermissionRequest
            // round-trip to the owning per-node hub.
            //
            // 🚨 Why local, not a hub probe: GetEffectivePermissions walks the
            // path's scope hierarchy — root, partition, every ancestor, the node
            // itself — so access defined on the MAIN node (the partition / owning
            // node where AccessAssignments live) covers every satellite under it:
            // "who can read the main node can read all its satellites." Asking the
            // security service for the PATH is therefore sufficient; there is no
            // need to first resolve the main node, and no need to reach the leaf's
            // own hub.
            //
            // The old GetPermissionRequest round-trip targeted new Address(path).
            // For a satellite / cell sub-path with no hub of its own — e.g.
            // {thread}/{messageId} the GUI subscribed to but that was never
            // persisted, or a brand-new thread — routing returns NotFound and
            // nothing answers, so the probe blocked the full 15s timeout and the
            // side-panel subscribe spun forever. Local evaluation has no such
            // dependency: it resolves from the cached AccessAssignment streams and
            // emits immediately (the partition owner gets Admin via the scope walk).
            //
            // Restore the caller's captured context across the SYNCHRONOUS evaluator
            // capture so claim-based (Bearer-token) roles on AccessContext.Roles
            // resolve — PermissionEvaluator snapshots accessService.Context on the
            // calling thread before any Rx scheduler hop.
            var accessService = meshHub.ServiceProvider.GetService<AccessService>();
            using (accessService?.SwitchAccessContext(captured)
                   ?? System.Reactive.Disposables.Disposable.Empty)
            {
                return meshHub.GetEffectivePermissions(path, captured.ObjectId)
                    .Take(1)
                    .Do(perms => _access[key] =
                        new AccessEntry(perms, DateTimeOffset.UtcNow + AccessTtl));
            }
        });
    }

    // 🚨 PRIVATE raw write — does NOT deserialize JsonElement Content before the
    // lambda. The interface no longer exposes a bare Update(path, fn): callers
    // MUST pass JsonSerializerOptions (Update(path, fn, options)) so an update
    // reading `curr.Content as MyType` sees the real value instead of null (which
    // returned the node unchanged → the write silently no-opped).
    private IObservable<MeshNode> UpdateRaw(string path, Func<MeshNode, MeshNode> update)
    {
        // The underlying MeshNodeStreamHandle.Update already wraps with
        // CarryAccessContext, so writes through the cache automatically carry
        // the caller's user identity into the partition write. No additional
        // wrap needed here. See AccessContextPropagation.md.
        //
        // Per-path mirror-side serialization: see the _updateQueues field
        // comment. Concurrent in-mirror Update calls would otherwise race
        // their `current` snapshot and emit overlapping array-replacement
        // patches that the RFC 7396 owner-side merge cannot resolve (lists
        // collapse to the last writer). Serializing per path makes each
        // lambda observe its predecessor's effect.
        // STORM BREAKER (write side): if this path's owner is in a known missing-node failure
        // window, fast-fail rather than enqueue another PatchDataRequest the owner can't
        // activate. Same negative cache as the read-side GetStreamRaw breaker, so a missing-node
        // path can never storm the mesh from either direction. Only a real CreateNode brings a
        // non-existent node into being — an Update can't.
        if (_negative.TryGetValue(path, out var negWrite)
            && negWrite.OpenUntil > DateTimeOffset.UtcNow
            && IsMissingNodeFailure(negWrite.Error))
            return Observable.Throw<MeshNode>(negWrite.Error);

        var queue = GetOrCreateUpdateQueue(path);
        var result = new ReplaySubject<MeshNode>();
        var seq = System.Threading.Interlocked.Increment(ref _updateSeq);
        logger.LogDebug(
            "[UpdateQueue] ENQUEUE path={Path} seq={Seq} enteredAt={EnteredAt}",
            path, seq, DateTimeOffset.UtcNow);
        queue.OnNext(new UpdateRequest(update, result, path, seq, DateTimeOffset.UtcNow));
        return result;
    }

    private long _updateSeq;

    /// <summary>
    /// Returns the per-path Subject that the serial-Update Concat consumes.
    /// Backed by <see cref="MemoryCache"/> with sliding expiration so paths
    /// that go quiet release their Subject + Concat subscription. A fresh
    /// write after eviction transparently recreates the queue — eviction is
    /// invisible to callers.
    ///
    /// 🚨 The cached VALUE is a <see cref="Lazy{T}"/>, not the Subject
    /// directly, because <c>MemoryCacheExtensions.GetOrCreate</c>
    /// is NOT atomic — the factory can run more than once under contention,
    /// and only ONE result wins per key. Losers would orphan a Subject +
    /// Concat subscription that never gets evicted (their eviction
    /// callback is never registered with the cache). Wrapping in
    /// <c>Lazy&lt;T&gt;(ThreadSafety.ExecutionAndPublication)</c> ensures
    /// the heavy work (new Subject, build observable, Subscribe) runs at
    /// most once per key even when multiple GetOrCreate calls race. Same
    /// pattern as <see cref="_streams"/>'s <c>Lazy&lt;Entry&gt;</c>.
    /// </summary>
    private Subject<UpdateRequest> GetOrCreateUpdateQueue(string path) =>
        _updateQueues.GetOrCreate(path, entry =>
        {
            entry.SlidingExpiration = UpdateQueueSlidingExpiration;
            var lazy = new Lazy<UpdateQueueEntry>(() =>
            {
                var subject = new Subject<UpdateRequest>();
                // 🚨 onError is mandatory. Per-request failures route to req.Result
                // (Materialize() below shields the Concat), so a fault HERE means the
                // queue plumbing itself died — the Subject then has no consumer and
                // every later write for this path would enqueue into the void with the
                // caller hanging on its result. Surface loudly and evict the dead
                // entry so the next write builds a fresh queue (the same lifecycle as
                // sliding-expiry eviction — no timer, no resubscribe of the faulted
                // pipeline; the fault itself stays visible in the log).
                var sub = BuildUpdateQueueObservable(path, subject).Subscribe(
                    _ => { },
                    ex =>
                    {
                        logger.LogError(ex,
                            "[UpdateQueue] queue pipeline FAULTED path={Path} — evicting dead queue",
                            path);
                        _updateQueues.Remove(path);
                    });
                return new UpdateQueueEntry(subject, sub);
            }, LazyThreadSafetyMode.ExecutionAndPublication);
            // Eviction (sliding-expiry timeout, manual Remove, or process
            // shutdown) tears down the long-lived Concat subscription and
            // completes the Subject — otherwise the Concat keeps response-
            // subjects rooted forever. Only fires if the Lazy was actually
            // materialised; an unrealised Lazy has no subscription to leak.
            entry.RegisterPostEvictionCallback((key, value, reason, state) =>
            {
                logger.LogDebug(
                    "[UpdateQueue] EVICTED path={Path} reason={Reason}",
                    key, reason);
                if (value is Lazy<UpdateQueueEntry> { IsValueCreated: true } lz)
                {
                    try { lz.Value.ConcatSubscription.Dispose(); } catch { /* best-effort */ }
                    try { lz.Value.Subject.OnCompleted(); } catch { /* best-effort */ }
                }
            });
            return lazy;
        })!.Value.Subject;

    /// <summary>
    /// Builds the per-path Concat pipeline that processes <see cref="UpdateRequest"/>s
    /// serially. Each request applies its patch through the local <c>Handle</c>
    /// (LOCAL_EMIT) and completes immediately — it does NOT wait for the patch's echo
    /// from the owner. The owning node hub's single-threaded action block already
    /// serialises patches, so the next queued Update sees post-patch state via the
    /// local Handle. The former 3-second echo wait DEADLOCKED when the echo could never
    /// arrive (a write to a freshly-created node defers on the owner's [Initialize]
    /// gate; or the writing hub's own action block is the one that would deliver the
    /// echo) and otherwise serialised 3s per update. Per-stage timing logs:
    /// ENQUEUE → START → LOCAL_EMIT → COMPLETE; missing LOCAL_EMIT = Handle.Update hung.
    /// </summary>
    private IObservable<MeshNode> BuildUpdateQueueObservable(string path, Subject<UpdateRequest> subject) =>
        subject
            .Select(req => Observable.Defer<MeshNode>(() =>
            {
                var waitedToStart = (DateTimeOffset.UtcNow - req.EnteredAt).TotalMilliseconds;
                logger.LogDebug(
                    "[UpdateQueue] START path={Path} seq={Seq} waitedInQueue={WaitedMs}ms",
                    path, req.Seq, waitedToStart);
                var entry = GetEntry(path);
                // FullNode set ⇒ overwrite (ChangeType.Full wholesale replace); else field-merge.
                var update = req.FullNode is not null
                    ? entry.Handle.Overwrite(req.FullNode)
                    : entry.Handle.Update(req.Update);

                // 🚨 Deliver the owner's FULL terminal (value / RLS denial / completion)
                // to the caller's result on a subscription whose lifetime is INDEPENDENT
                // of the queue slot. After the RLS commit, entry.Handle.Update is
                // UpdateRemote, which AWAITS the owner's PatchDataResponse (up to 30s). The
                // RLS-denial surfacing + read-after-write that the commit added are
                // preserved untouched — the caller still gets the real terminal whenever
                // it arrives. Tracked in _inflightWrites for mid-flight cache disposal;
                // self-removes on terminal so it never accumulates.
                var inflight = new System.Reactive.Disposables.SingleAssignmentDisposable();
                _inflightWrites[inflight] = 0;
                void Settle()
                {
                    _inflightWrites.TryRemove(inflight, out _);
                    try { inflight.Dispose(); } catch { /* best-effort */ }
                }
                inflight.Disposable = update.Subscribe(
                    node =>
                    {
                        logger.LogDebug(
                            "[UpdateQueue] LOCAL_EMIT path={Path} seq={Seq} elapsedFromStart={ElapsedMs}ms",
                            path, req.Seq, (DateTimeOffset.UtcNow - req.EnteredAt).TotalMilliseconds);
                        // A successful write proves the owner is live ⇒ clear any storm-breaker
                        // window so reads/writes re-probe normally.
                        _negative.TryRemove(path, out _);
                        req.Result.OnNext(node);
                    },
                    ex =>
                    {
                        logger.LogWarning(ex,
                            "[UpdateQueue] FAILED path={Path} seq={Seq} elapsedMs={ElapsedMs}",
                            path, req.Seq, (DateTimeOffset.UtcNow - req.EnteredAt).TotalMilliseconds);
                        // Storm-breaker (write side): a terminal "node/hub does not exist" failure
                        // opens the negative-cache window so subsequent writes AND reads fast-fail
                        // instead of re-enqueueing doomed PatchDataRequests against a hub that can't
                        // activate. Only missing-node failures record (not RLS denial / transient),
                        // so a legitimately-existing node is never falsely suppressed.
                        if (IsMissingNodeFailure(ex))
                            RecordNegative(path, ex);
                        req.Result.OnError(ex);
                        Settle();
                    },
                    () =>
                    {
                        logger.LogDebug(
                            "[UpdateQueue] COMPLETE path={Path} seq={Seq} totalElapsed={ElapsedMs}ms",
                            path, req.Seq, (DateTimeOffset.UtcNow - req.EnteredAt).TotalMilliseconds);
                        req.Result.OnCompleted();
                        Settle();
                    });

                // 🚨 Advance the per-path queue on the FIRST owner signal OR
                // QueueAdvanceBound — NEVER UpdateRemote's full 30s response wait. A lost
                // owner response (owner mid-dispose handled the patch but its reply never
                // routed back) otherwise blocked this serial queue for 30s and starved
                // every retry (the ResubscribeOnOwnerDispose deadlock). Observe req.Result
                // (already fed above) instead of opening a SECOND subscription to the cold
                // update (which would post the patch twice). Complete without emitting so
                // Concat moves to the next queued write.
                return req.Result
                    .Materialize()
                    .Select(_ => System.Reactive.Unit.Default)
                    .Take(1)
                    .Timeout(QueueAdvanceBound, Observable.Return(System.Reactive.Unit.Default))
                    .Take(1)
                    .SelectMany(_ => Observable.Empty<MeshNode>());
            }))
            .Concat();

    /// <summary>
    /// Caller-typed read: every emitted MeshNode's <c>Content</c> is round-tripped
    /// through <paramref name="options"/> so the caller sees a typed domain
    /// instance (<c>ModelProviderConfiguration</c>, etc.) rather than the raw
    /// <c>JsonElement</c> the cache hub stores. See
    /// <see cref="IMeshNodeStreamCache.GetStream(string, JsonSerializerOptions)"/>.
    /// </summary>
    public IObservable<MeshNode> GetStream(string path, JsonSerializerOptions options) =>
        GetStreamRaw(path).Select(node => ConvertContentJsonElementToTyped(node, options));

    /// <summary>
    /// Caller-typed write: deserialises the current MeshNode's <c>Content</c>
    /// via <paramref name="options"/> before invoking <paramref name="update"/>,
    /// then re-serialises the lambda's returned <c>Content</c> back to a
    /// <c>JsonElement</c> (still using <paramref name="options"/> so the
    /// <c>$type</c> discriminator is written) before the framework computes the
    /// JSON-merge patch. The cache hub's own serializer stays domain-agnostic.
    /// </summary>
    public IObservable<MeshNode> Update(
        string path,
        Func<MeshNode, MeshNode> update,
        JsonSerializerOptions options)
    {
        Func<MeshNode, MeshNode> wrapped = node =>
        {
            var typed = ConvertContentJsonElementToTyped(node, options);
            var updated = update(typed);
            return ConvertContentTypedToJsonElement(updated, options);
        };

        // RLS on the write is enforced authoritatively by the OWNER: the
        // [RequiresPermission(Permission.Update)] gate on PatchDataRequest posts
        // DeliveryFailure(Unauthorized) on denial, which UpdateRemote now surfaces
        // as UnauthorizedAccessException on the Rx OnError stream (see
        // MeshNodeStreamExtensions.UpdateRemote). The previous cache-only client gate
        // here was a stopgap for when UpdateRemote emitted optimistically and swallowed
        // the denial — it only fired on a warm permission cache (cold-write miss = no
        // surfacing, a CI-flake source) and used a non-canonical message. Now redundant.
        return UpdateRaw(path, wrapped);
    }

    /// <inheritdoc />
    public IObservable<MeshNode> Overwrite(string path, MeshNode node, JsonSerializerOptions options)
    {
        // Serialise typed Content → JsonElement via the CALLER's options (writes the $type
        // discriminator) before the node reaches the domain-agnostic cache hub — same outbound
        // conversion Update does. The owner reads it back as the registered typed Content.
        var jsonNode = ConvertContentTypedToJsonElement(node, options);
        return OverwriteRaw(path, jsonNode);
    }

    // 🚨 PRIVATE raw overwrite — enqueues a FULL-node write on the per-path serial queue so it
    // serialises against concurrent Updates on the same path (same queue as UpdateRaw). The
    // queued request carries the full node; BuildUpdateQueueObservable dispatches it to
    // Handle.Overwrite (ChangeType.Full) instead of Handle.Update.
    private IObservable<MeshNode> OverwriteRaw(string path, MeshNode node)
    {
        var queue = GetOrCreateUpdateQueue(path);
        var result = new ReplaySubject<MeshNode>();
        var seq = System.Threading.Interlocked.Increment(ref _updateSeq);
        logger.LogDebug(
            "[UpdateQueue] ENQUEUE-OVERWRITE path={Path} seq={Seq} enteredAt={EnteredAt}",
            path, seq, DateTimeOffset.UtcNow);
        // Update func is a placeholder (never invoked — the FullNode branch is taken).
        queue.OnNext(new UpdateRequest(static n => n, result, path, seq, DateTimeOffset.UtcNow, node));
        return result;
    }

    private static MeshNode ConvertContentJsonElementToTyped(MeshNode node, JsonSerializerOptions options)
    {
        // Only convert when the cache emitted a raw JsonElement (the cache hub
        // doesn't know domain types, so Content lands here as JsonElement). If
        // the cache somehow already has a typed value (e.g. a same-process
        // caller already converted), pass through.
        if (node.Content is JsonElement je)
        {
            return node with { Content = je.Deserialize<object>(options) };
        }
        return node;
    }

    private static MeshNode ConvertContentTypedToJsonElement(MeshNode node, JsonSerializerOptions options)
    {
        // Already a JsonElement (or null) — nothing to do; the cache hub's
        // serializer can faithfully serialise it on the outbound patch.
        if (node.Content is null or JsonElement)
            return node;
        // Caller-typed Content — serialise via the CALLER'S options so the
        // $type discriminator is written. This makes the JSON-merge patch the
        // framework computes self-describing on the wire; the silo's own
        // type-aware serializer reads it back as the same typed value.
        return node with
        {
            Content = JsonSerializer.SerializeToElement(node.Content, options)
        };
    }

    /// <summary>
    /// Process-wide synced-query cache. Replaces the legacy
    /// <c>ConditionalWeakTable&lt;IWorkspace, SyncedQueryRegistry&gt;</c> in
    /// <c>SyncedQueryDataSourceExtensions</c> — one registry, one set of
    /// upstream subscriptions, regardless of how many workspaces ask. The
    /// SyncedQueryMeshNodes runs on the cache hub's workspace so its
    /// SubscribeRequests carry <c>MeshNodeCacheIdentity</c>; the secured
    /// query surface short-circuits to raw upstream and no per-hub
    /// AsyncLocal AccessContext leaks in.
    /// </summary>
    // Thread-safety contract for GetQuery():
    //
    //   1. CREATION is lock-free atomic-swap over an ImmutableDictionary.
    //      N concurrent threads racing for the same id each construct a
    //      fresh observable chain; exactly one CAS-winner installs into
    //      the map. Avoids the ConcurrentDictionary footgun where the
    //      value factory can be invoked multiple times concurrently and
    //      every loser's side-effects leak.
    //
    //   2. CAS-LOSERS DO NOT LEAK because AutoConnect(1) is lazy —
    //      the upstream IMeshQueryCore subscription only opens when a
    //      consumer attaches Subscribe. Losers' discarded chains have
    //      no subscribers and never connect.
    //
    //   3. SUBSCRIPTION is thread-safe via ReplaySubject's internal lock
    //      (which backs .Replay(1)). Multiple threads calling .Subscribe
    //      on the cached observable serialise through the subject's
    //      gate; each subscriber sees OnNext invocations serially within
    //      itself.
    //
    //   4. EMISSIONS are serialised through .Synchronized() after the
    //      Replay buffer — defence-in-depth so downstream observers that
    //      assume single-threaded callbacks (the common case) hold even
    //      under heavy concurrent emission load from the change feed.
    //
    //   5. EVENTUAL CONSISTENCY: the upstream stays connected for the
    //      cache singleton's lifetime (AutoConnect(1) never disconnects).
    //      Change-feed events flow into Replay(1) in real time, so new
    //      subscribers attaching at any later point see the current
    //      snapshot, not a stale Initial.
    private System.Collections.Immutable.ImmutableDictionary<object, IObservable<IEnumerable<MeshNode>>> _queries =
        System.Collections.Immutable.ImmutableDictionary<object, IObservable<IEnumerable<MeshNode>>>.Empty;

    // Memoised options-wrapped observables, keyed by (id, options). The
    // options overload wraps the raw cached stream in a content-deserialising
    // Select; without memoisation every call returns a FRESH Select instance,
    // so two infrastructure callers wrapped in ImpersonateAsSystem (which
    // short-circuit WrapWithPerUserRls straight to this upstream) would get
    // distinct references instead of the shared system-security cache entry.
    // Keyed on the caller's stable hub JsonSerializerOptions (one instance per
    // hub) so repeated GetQuery(id, options, …) calls reuse the same wrapper.
    private readonly ConcurrentDictionary<(object Id, JsonSerializerOptions Options), IObservable<IEnumerable<MeshNode>>> _optionsWrappedQueries = new();

    // Raw builder — PRIVATE. The public surface (hub/workspace.GetQuery → the
    // internal options overload below) always injects the caller hub's
    // JsonSerializerOptions, so no caller can build a synced query whose Content
    // stays an untyped JsonElement. See IMeshNodeStreamCache.GetQuery doc.
    private IObservable<IEnumerable<MeshNode>> GetQueryRaw(object id, params string[] queries)
    {
        if (queries is null || queries.Length == 0)
            throw new ArgumentException("At least one query string is required.", nameof(queries));

        while (true)
        {
            var current = _queries;
            if (current.TryGetValue(id, out var existing))
                return existing;

            // Deferred + thread-pool subscribe-on + Replay(1).RefCount: a
            // shared cached observable. The lambda inside Defer runs on the
            // first subscriber's thread (TaskPoolScheduler thanks to
            // SubscribeOn), constructs the SyncedQueryMeshNodes, and the
            // Replay(1) caches its emissions for all later subscribers.
            var stream = Observable.Defer(() =>
                {
                    var typeSource = new global::MeshWeaver.Graph.SyncedQueryMeshNodes(
                        cacheHub.GetWorkspace(), id, queries);
                    return typeSource.StreamUpdates();
                })
                .SubscribeOn(TaskPoolScheduler.Default)
                .Replay(1)
                // AutoConnect(1): connect the upstream on the FIRST subscriber
                // and keep it alive thereafter (AutoConnect doesn't track
                // disconnect). Lazy connect matters under CAS contention —
                // every CAS retry below constructs a fresh observable; with
                // AutoConnect(0) every loser's chain Connect()s eagerly and
                // leaks an upstream IMeshQueryCore subscription. With
                // AutoConnect(1) the loser's discarded chain has no
                // subscribers and never connects.
                //
                // RefCount() was the wrong primitive here — when subscriber
                // count drops to 0 between calls the Replay buffer is
                // retained but the upstream is disconnected, so a subsequent
                // Take(1) / FirstAsync after a runtime AccessAssignment
                // write sees the STALE cached snapshot. AutoConnect(1)
                // keeps the upstream connected forever once first subscribed.
                .AutoConnect(1);
                // ReplaySubject (backing Replay(1)) already serialises
                // OnNext/Subscribe internally — no .Synchronize() needed.
                // Adding it would route every emission through an additional
                // gate lock, contending with concurrent subscribers under
                // load.

            var updated = current.Add(id, stream);
            if (Interlocked.CompareExchange(ref _queries, updated, current) == current)
                return stream;
            // CAS lost — another thread won concurrently; retry the read.
        }
    }

    public IObservable<IEnumerable<MeshNode>>? GetQuery(object id)
        => _queries.TryGetValue(id, out var stream) ? stream : null;

    public IObservable<IEnumerable<MeshNode>> GetQuery(object id, JsonSerializerOptions options, params string[] queries)
    {
        var raw = GetQueryRaw(id, queries);
        if (options is null) return raw;
        // Round-trip each emitted MeshNode's Content through the caller's
        // JsonSerializerOptions so consumers see typed domain instances
        // (AccessAssignment, PartitionAccessPolicy, etc.) rather than the
        // raw JsonElement the cache hub stores. Same shape as
        // GetStream(path, options).
        //
        // Memoise by (id, options) so the wrapper is reference-stable across
        // calls — infrastructure callers (ImpersonateAsSystem) bypass the
        // per-user RLS wrap and rely on getting the SAME shared observable for
        // the same id (perf shortcut for SecurityService / NodeType compile
        // watchers). GetOrAdd's factory may run more than once under a race,
        // but each candidate wraps the same cached raw stream — losers are
        // inert (no Subscribe), so there's no upstream leak.
        return _optionsWrappedQueries.GetOrAdd((id, options), static (_, state) =>
            System.Reactive.Linq.Observable.Select(state.raw, items =>
                (IEnumerable<MeshNode>)items.Select(node => DeserializeContent(node, state.options)).ToArray()),
            (raw, options));
    }

    private static MeshNode DeserializeContent(MeshNode node, JsonSerializerOptions options)
    {
        if (node.Content is not System.Text.Json.JsonElement je || je.ValueKind != System.Text.Json.JsonValueKind.Object)
            return node;
        try
        {
            var deserialized = System.Text.Json.JsonSerializer.Deserialize<object>(je.GetRawText(), options);
            return deserialized is null ? node : node with { Content = deserialized };
        }
        catch
        {
            return node;
        }
    }

    /// <summary>
    /// Removes the cached entry for <paramref name="path"/> so the next
    /// <c>GetStream</c> call rebuilds a fresh stream. Called by
    /// <c>HandleDeleteNodeRequest</c> after the persistence delete commits —
    /// the Replay(1) cache otherwise holds the pre-delete MeshNode forever
    /// (the upstream observable doesn't emit "deleted" — the per-node hub is
    /// gone). Idempotent.
    /// </summary>
    public void Invalidate(string path)
    {
        if (_streams.TryRemove(path, out var lazyEntry))
        {
            // Dispose the upstream SubscribeRequest so it doesn't dangle in
            // mesh hub's responseSubjects after the path is deleted. Skip
            // for Lazy<Entry> that never ran its factory (nothing to dispose).
            if (lazyEntry.IsValueCreated)
            {
                try { lazyEntry.Value.HydrationSub.Dispose(); }
                catch (Exception ex)
                {
                    logger.LogDebug(ex,
                        "MeshNodeStreamCache: error disposing hydration subscription for {Path}",
                        path);
                }
            }
            logger.LogDebug("MeshNodeStreamCache: invalidated entry for {Path}", path);
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is empty or its first segment matches
    /// a known NodeType name (from <see cref="SatelliteTableMapping"/>).
    /// Used by <c>GetStream</c> to skip the access-check round-trip on
    /// non-partition-rooted paths, which previously triggered the prod
    /// 2026-05-21 regression where <c>PostgreSqlPathRoutingAdapter</c>
    /// lower-cased the segment as a schema name and blew up with
    /// <c>relation "thread.mesh_nodes" does not exist</c>.
    /// </summary>
    private static bool LooksLikeNodeTypePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return true;
        var slashIdx = path.IndexOf('/');
        var firstSegment = slashIdx < 0 ? path : path[..slashIdx];
        if (string.IsNullOrEmpty(firstSegment)) return true;
        // NodeTypeToSuffix is the canonical registry of "this is a NodeType
        // name, not a partition name". If the first segment is in here, the
        // path was never going to resolve as a partition path.
        return PartitionDefinition.IsSatelliteNodeType(firstSegment);
    }
}
