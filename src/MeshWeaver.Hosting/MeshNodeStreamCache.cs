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
///
/// <para><b>Idle release.</b> Read entries have the same sliding-expiration
/// semantics as the write-side update queues (<see cref="_updateQueues"/>): an
/// entry whose shared stream has had NO live subscriber and NO read/write hit
/// for <see cref="MeshNodeStreamCacheOptions.ReadStreamIdleExpiration"/> is
/// released — its upstream <c>SubscribeRequest</c> is closed (the owner-side
/// mirror unsubscribes, the 45s sync-stream heartbeat dies) and the NEXT read
/// transparently re-creates it. Without this, every path EVER read (GUI
/// navigation, per-URL path resolution, routing, NodeType activation, MCP
/// get/search, synced-query grain warming) kept a permanently-connected
/// JsonSynchronizationStream heartbeating for the process lifetime (~1,650
/// live streams / 37 heartbeats-per-second measured on a long-lived portal).
/// An entry with a live subscriber is NEVER released; the sweep only ever
/// CLOSES idle things — it never re-subscribes (the 2026-06-08 rule).</para>
/// </summary>
internal sealed class MeshNodeStreamCache : IMeshNodeStreamCache, IDisposable
{
    // 0 = live, 1 = disposed. Dispose fires from BOTH the cacheHub disposal
    // hook (silo goes down → mesh hub disposes its hosted cache hub) AND the
    // DI container tearing down this singleton. Interlocked guard makes the
    // teardown idempotent so the second caller is a no-op.
    private int _disposed;

    /// <summary>One cache entry: the updatable handle, the raw replay-cached read
    /// view over the hydration subject (<see cref="Replay"/> — per-user access
    /// gating is applied in <c>GetStream</c> before each subscriber consumes it),
    /// and the live upstream subscription. Also carries the idle-release state: a
    /// live-subscriber refcount plus a sliding last-activity stamp, both guarded by
    /// a plain synchronous gate (held for nanoseconds around pure field updates —
    /// never across I/O, a hub post, or anything awaitable), so subscriber
    /// attach/detach is ATOMIC against the idle sweep's evict decision: an entry
    /// with a live subscriber can never be evicted, and a reader that just pinned
    /// the entry cannot lose it. Once <c>evicted</c> flips the entry is permanently
    /// dead — <see cref="TryTouch"/>/<see cref="TryAddSubscriber"/> fail and the
    /// caller transparently re-creates a fresh entry (same lifecycle as write-queue
    /// eviction — invisible to callers).</summary>
    private sealed class Entry(MeshNodeStreamHandle handle, IObservable<MeshNode> replay, IDisposable hydrationSub)
    {
        private readonly object gate = new();
        private int subscribers;
        private long lastActiveAt = Environment.TickCount64; // monotonic ms
        private bool evicted;
        private bool faulted;

        public MeshNodeStreamHandle Handle { get; } = handle;

        /// <summary>Raw replay view over the hydration subject. Internal plumbing —
        /// consumers attach via the refcounted wrapper (<see cref="SharedView"/>),
        /// which is what makes live subscribers visible to the idle sweep.</summary>
        public IObservable<MeshNode> Replay { get; } = replay;

        /// <summary>The upstream hydration subscription (+ storm-breaker bookkeeping).</summary>
        public IDisposable HydrationSub { get; } = hydrationSub;

        /// <summary>True while the entry is usable. Does NOT refresh the idle window.</summary>
        public bool IsLive { get { lock (gate) return !evicted; } }

        /// <summary>Marks the entry's hydration as terminally errored — its replay
        /// subject holds an OnError terminal, so every future subscriber would
        /// replay the stale failure. Set by the storm-breaker bookkeeping observer;
        /// consumed by the change-feed invalidation reset, which evicts ONLY
        /// faulted entries (a healthy live entry must never be torn down by a
        /// routine post-commit Updated broadcast).</summary>
        public void MarkFaulted() { lock (gate) faulted = true; }

        /// <summary>True when the entry's hydration terminated with an error.</summary>
        public bool IsFaulted { get { lock (gate) return faulted; } }

        /// <summary>Refreshes the sliding idle window (read/write activity hit).</summary>
        public void Touch() { lock (gate) lastActiveAt = Environment.TickCount64; }

        /// <summary>Atomically "still live + refresh the sliding window". False ⇒ the
        /// entry was evicted; the caller must drop it and re-create. A successful pin
        /// makes idle eviction impossible for a full idle window.</summary>
        public bool TryTouch()
        {
            lock (gate)
            {
                if (evicted) return false;
                lastActiveAt = Environment.TickCount64;
                return true;
            }
        }

        /// <summary>Registers a live subscriber (blocks idle eviction outright) and
        /// refreshes the window. False ⇒ evicted; caller re-resolves a fresh entry.</summary>
        public bool TryAddSubscriber()
        {
            lock (gate)
            {
                if (evicted) return false;
                subscribers++;
                lastActiveAt = Environment.TickCount64;
                return true;
            }
        }

        /// <summary>Releases a live subscriber. Also refreshes the window, so "idle"
        /// is measured from the LAST unsubscribe, not the last data emission.</summary>
        public void RemoveSubscriber()
        {
            lock (gate)
            {
                subscribers--;
                lastActiveAt = Environment.TickCount64;
            }
        }

        /// <summary>Cheap advisory pre-check the sweep runs before detaching the path's
        /// upstreams. The authoritative decision is <see cref="TryMarkIdleEvicted"/>.</summary>
        public bool IsIdleCandidate(TimeSpan idleWindow)
        {
            lock (gate)
                return !evicted && subscribers == 0
                    && Environment.TickCount64 - lastActiveAt >= (long)idleWindow.TotalMilliseconds;
        }

        /// <summary>Authoritative evict decision: marks the entry evicted iff it has
        /// ZERO live subscribers AND stayed untouched for the full idle window —
        /// atomically against <see cref="TryTouch"/>/<see cref="TryAddSubscriber"/>.</summary>
        public bool TryMarkIdleEvicted(TimeSpan idleWindow)
        {
            lock (gate)
            {
                if (evicted || subscribers > 0
                    || Environment.TickCount64 - lastActiveAt < (long)idleWindow.TotalMilliseconds)
                    return false;
                evicted = true;
                return true;
            }
        }

        /// <summary>Unconditional evict mark for the forced paths (node delete /
        /// storm-breaker stale entry / cache disposal). Idempotent.</summary>
        public bool MarkEvicted()
        {
            lock (gate)
            {
                if (evicted) return false;
                evicted = true;
                return true;
            }
        }
    }

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

    // 🚨 Idle release of read entries — the read-side counterpart of the write
    // cache's (_updateQueues) sliding expiration. Window/interval come from
    // MeshNodeStreamCacheOptions (10 min / 1 min by default; tests inject short
    // windows). The sweep subscription only ever CLOSES idle entries; re-opening
    // is always driven by the next natural read (never a timer — 2026-06-08 rule).
    private readonly TimeSpan readStreamIdleExpiration;
    private readonly TimeSpan readStreamSweepInterval;
    private readonly IDisposable idleSweep;

    // 🚨 Invalidation signal: the SAME IMeshChangeFeed broadcast every write path
    // already publishes — post-commit storage writes (Created/Updated/Deleted) AND
    // the recycle operation (MeshOperations.RecycleCore publishes an Updated event
    // before posting DisposeRequest; in Orleans the PathCacheInvalidatorGrain
    // relays it cross-silo into each process's local feed). On each event the
    // cache RESETS its failure state for that exact path: the storm-breaker
    // negative entry (error, fail count, backoff window) is dropped and a
    // terminally-FAULTED read entry is evicted, so the next natural read gets a
    // completely fresh resolution attempt. Without this, a recycle after a
    // compile-error era could never heal the path: the breaker's grown backoff
    // window (up to StormMaxCooldown) kept fast-failing every read/write and the
    // faulted entry replayed the stale error — only a pod restart cleared it
    // (memex-cloud 2026-07-19, AgenticEngineering/Install). EVICTION/RESET ONLY —
    // this never re-subscribes anything (2026-06-08 rule); re-probing is always
    // the next natural read.
    private readonly IDisposable? changeFeedReset;

    // Diagnostic/test seam: one event per released read entry (idle sweep,
    // Invalidate, storm-breaker stale removal). Subject.Synchronize because
    // releases fire from the sweep timer thread and hub threads concurrently.
    private readonly ISubject<ReadStreamEviction> readStreamEvictions =
        Subject.Synchronize(new Subject<ReadStreamEviction>());

    /// <summary>Emits one event per released read entry. Diagnostic/test seam —
    /// deterministic tests await the eviction of a specific path instead of polling.</summary>
    internal IObservable<ReadStreamEviction> ReadStreamEvictions => readStreamEvictions.AsObservable();

    /// <summary>True when a live (non-evicted) read entry exists for <paramref name="path"/>.
    /// Test seam — does NOT touch the entry's idle window.</summary>
    internal bool IsReadStreamLive(string path) =>
        _streams.TryGetValue(path, out var lazy) && lazy.IsValueCreated && lazy.Value.IsLive;

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
    // read clears the entry immediately, and so does a change-feed invalidation for the
    // path (recycle broadcast / post-commit write — see ResetFailureState: a real write
    // on the path is authoritative proof the cached failure is stale, so the window
    // closes and the counters reset instead of a healthy-again node fast-failing for
    // the remainder of a grown backoff window). Consecutive failures grow the window
    // (StormBaseCooldown · 2^(n-1), capped at StormMaxCooldown); crossing
    // StormFailThreshold logs ONE "[STORM-BREAKER] suppressing" warning so the storm is
    // visible in Grafana/Loki without the per-failure log flood.
    private readonly ConcurrentDictionary<string, NegativeEntry> _negative = new();
    // Reprobing: set true by the FIRST read past OpenUntil — that read drops the stale errored
    // entry and lets a fresh probe hydrate. Subsequent reads while that probe is IN FLIGHT must
    // NOT re-evict it (repeatedly tearing a still-hydrating entry down before it can resolve is a
    // churn/storm that never lets the negative entry clear). Reset whenever RecordNegative writes
    // a fresh window; FailCount is carried across the reprobe so a genuinely-missing node still
    // backs off further when the fresh probe re-errors.
    private sealed record NegativeEntry(Exception Error, int FailCount, DateTimeOffset OpenUntil,
        bool Reprobing = false);
    private static readonly TimeSpan StormBaseCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StormMaxCooldown = TimeSpan.FromMinutes(5);
    private const int StormFailThreshold = 5;

    // 🚨 TRANSIENT-FAULT BREAKER. IsTransientOwnerFailure faults (reactivation reject /
    // hub-request timeout) are deliberately NEVER negative-cached — the just-idle-collected
    // grain reactivates and the very next probe lands on the fresh activation, so suppression
    // would break the "navigate to an idle page" case. But that clean-cache policy assumed the
    // fault is a ONE-OFF. A PERSISTENTLY faulting activation (2026-07-21: a poisoned
    // AgenticEngineering init replayed the same cached SubscribeRequest timeout into every
    // fresh activation) turns the policy into an unbounded instant-re-probe loop: every read
    // re-opens an upstream SubscribeRequest ~3/sec, each cycle allocates, and the silo leaked
    // 4→22 GiB in ~12 minutes while its action blocks starved — one broken hub degrading the
    // whole portal. This breaker keeps BOTH properties: the first TransientGraceFailures
    // consecutive transient faults stay exactly as before (clean cache, instant re-probe — the
    // idle-page case never sees it), but a streak beyond the grace is empirical proof the
    // "transient" claim is false, so re-probes back off exponentially
    // (TransientBaseCooldown · 2^(n-grace-1), capped at TransientMaxCooldown — a short cap,
    // because a recycle CAN heal these). Cleared instantly by a real resolution, a change-feed
    // invalidation (recycle / post-commit write), or a quiet period (a streak with no fault for
    // TransientStreakExpiry is stale — occasional blips over a long session never accumulate).
    // Reads only: writes already bound their owner wait (QueueAdvanceBound) and a write is
    // often the recycle that heals the path.
    private sealed record TransientStreak(Exception Error, int FailCount, DateTimeOffset OpenUntil,
        DateTimeOffset LastFailAt, bool Reprobing = false);
    private readonly ConcurrentDictionary<string, TransientStreak> _transientStreaks = new();
    private static readonly TimeSpan TransientBaseCooldown = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TransientMaxCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TransientStreakExpiry = TimeSpan.FromMinutes(5);
    private const int TransientGraceFailures = 3;

    // In-flight cross-hub write subscriptions that have OUTLIVED their queue slot. The
    // per-path Update queue advances on a bounded signal (QueueAdvanceBound) so a lost
    // owner response can't starve retries, but each write's subscription to the owner's
    // PatchDataResponse must keep running to deliver the real terminal (value / RLS
    // denial) to its caller. Tracked here so a mid-flight cache disposal tears them down;
    // each self-removes on its own terminal so this never accumulates.
    private readonly ConcurrentDictionary<IDisposable, byte> _inflightWrites = new();

    // 🚨 How long the per-path serial Update queue waits for the CURRENT write's first
    // owner signal before letting the NEXT queued write proceed. entry.Handle.Update is
    // UpdateRemote, which now bounds its OWN owner-response wait (UpdateResponseWaitBound,
    // ~2s) and falls back to an optimistic emit — so a healthy or busy-owner write always
    // produces a terminal quickly and the queue advances on that. This bound stays as a
    // backstop for the pathological case where the post itself stalls (a lost response —
    // the owner mid-dispose handled the patch but its reply never routed back — used to
    // block the queue for the full 30s and starve every retry: the ResubscribeOnOwnerDispose
    // deadlock). It sits above a normal owner round-trip (ms-to-low-seconds), so the queue
    // only "gives up waiting" for a genuinely stuck post. The caller's result is unaffected:
    // it receives the real terminal (value / fail-fast RLS denial / optimistic fallback).
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

    public MeshNodeStreamCache(
        IMessageHub meshHub,
        ILogger<MeshNodeStreamCache> logger,
        MeshNodeStreamCacheOptions? options = null)
    {
        this.meshHub = meshHub;
        this.logger = logger;
        var opts = options ?? new MeshNodeStreamCacheOptions();
        readStreamIdleExpiration = opts.ReadStreamIdleExpiration;
        readStreamSweepInterval = opts.ReadStreamSweepInterval;

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

        // 🚨 Idle sweep for the per-path READ cache. Periodic and EVICTION-ONLY: each
        // tick closes entries that have been subscriber-free AND untouched for the
        // full idle window; it NEVER re-subscribes anything (re-opening is always
        // the next natural read — the same lifecycle as _updateQueues' sliding
        // expiry, which this mirrors on the read side). Disposed FIRST in Dispose()
        // so no pass starts after teardown begins.
        idleSweep = Observable.Interval(readStreamSweepInterval)
            .Subscribe(_ => ReleaseIdleReadStreams());

        // Failure-state reset on the EXISTING invalidation broadcast (see the
        // changeFeedReset field doc). Optional service: minimal test fixtures
        // without AddMeshCatalog's feed registration simply have no reset seam.
        changeFeedReset = meshHub.ServiceProvider.GetService<IMeshChangeFeed>()
            ?.Subscribe(OnMeshChange);
    }

    /// <summary>
    /// Change-feed handler: a published change event for a path is authoritative
    /// proof of a real write / recycle on that path, so any cached FAILURE state
    /// for it is stale by definition — reset it (see <see cref="ResetFailureState"/>).
    /// Runs synchronously on the publisher's thread; pure dictionary ops plus
    /// disposing an already-terminated Rx subscription — no I/O, no hub post.
    /// </summary>
    private void OnMeshChange(MeshChangeEvent change)
    {
        if (System.Threading.Volatile.Read(ref _disposed) != 0)
            return;
        if (string.IsNullOrEmpty(change.Path))
            return;
        try
        {
            ResetFailureState(change.Path);
        }
        catch (Exception ex)
        {
            // The feed's Subject.OnNext runs handlers synchronously on the
            // PUBLISHER's thread (a post-commit storage write / the recycle
            // operation) and an unhandled throw here would fault that pipeline
            // and starve the feed's remaining subscribers. The reset is state
            // hygiene — surface the fault loudly, never break the writer.
            logger.LogError(ex,
                "MeshNodeStreamCache: failure-state reset faulted for {Path}", change.Path);
        }
    }

    /// <summary>
    /// Gives <paramref name="path"/> a completely fresh resolution attempt:
    /// <list type="number">
    ///   <item>Drops the storm-breaker negative entry — cached error, fail count
    ///     AND backoff window (closed; counters back to zero).</item>
    ///   <item>Evicts the read entry IFF its hydration terminated with an error
    ///     (<see cref="Entry.IsFaulted"/>) — its Replay(1) would otherwise keep
    ///     replaying the stale terminal error to every future subscriber even
    ///     with the breaker cleared. A healthy live entry is left untouched: the
    ///     owner's sync stream already delivers routine updates, and tearing the
    ///     shared handle down on every post-commit broadcast would sever live
    ///     GUI subscribers.</item>
    /// </list>
    /// Same discipline as the storm-breaker's own re-probe eviction (guard #2 in
    /// <see cref="GetStreamRaw"/>): dispose ONLY the Rx hydration — a terminally
    /// errored upstream has already torn down its own keep-alive, and posting
    /// UnsubscribeRequest at a mid-recycle owner would race its re-activation.
    /// </summary>
    internal void ResetFailureState(string path)
    {
        if (_negative.TryRemove(path, out var cleared))
        {
            // One line when a genuinely-suppressed storm is lifted (mirrors the
            // single "[STORM-BREAKER] suppressing" warning); routine clears stay at Debug.
            if (cleared.FailCount >= StormFailThreshold)
                logger.LogInformation(
                    "[STORM-BREAKER] Cleared '{Path}' on change-feed invalidation after {FailCount} recorded failures "
                    + "(window was open until {OpenUntil:O}) — next read re-probes fresh.",
                    path, cleared.FailCount, cleared.OpenUntil);
            else
                logger.LogDebug(
                    "MeshNodeStreamCache: cleared negative entry for {Path} on change-feed invalidation (failCount={FailCount})",
                    path, cleared.FailCount);
        }

        if (_transientStreaks.TryRemove(path, out var clearedStreak))
        {
            if (clearedStreak.FailCount > TransientGraceFailures)
                logger.LogInformation(
                    "[TRANSIENT-BREAKER] Cleared '{Path}' on change-feed invalidation after {FailCount} consecutive "
                    + "transient owner faults — next read re-probes fresh.",
                    path, clearedStreak.FailCount);
            else
                logger.LogDebug(
                    "MeshNodeStreamCache: cleared transient-fault streak for {Path} on change-feed invalidation (failCount={FailCount})",
                    path, clearedStreak.FailCount);
        }

        if (_streams.TryGetValue(path, out var lazy) && lazy.IsValueCreated && lazy.Value.IsFaulted
            && _streams.TryRemove(new KeyValuePair<string, Lazy<Entry>>(path, lazy)))
        {
            try
            {
                var stale = lazy.Value;
                stale.MarkEvicted();
                stale.HydrationSub.Dispose();
                readStreamEvictions.OnNext(new ReadStreamEviction(path, false, "invalidate"));
                logger.LogDebug(
                    "MeshNodeStreamCache: evicted faulted read entry for {Path} on change-feed invalidation", path);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "MeshNodeStreamCache: error disposing faulted entry for {Path}", path);
            }
        }
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

        // 0. Idle sweep FIRST — no new sweep pass may start once teardown begins
        //    (an in-flight pass is harmless: entry teardown is idempotent and the
        //    sync hubs are hosted by cacheHub, whose disposal reaps any stragglers).
        try { idleSweep.Dispose(); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "MeshNodeStreamCache: error disposing idle sweep");
        }
        try { changeFeedReset?.Dispose(); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "MeshNodeStreamCache: error disposing change-feed reset subscription");
        }
        try { readStreamEvictions.OnCompleted(); }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "MeshNodeStreamCache: error completing eviction seam");
        }

        // 1. Per-path hydration: cancel the upstream SubscribeRequest so the
        //    owning node hub's response-subject is released. (The Entry's
        //    Handle is a stateless factory — it owns nothing; the HydrationSub
        //    IS the live subscription.) Without this every cache.GetStream(path)
        //    leaks a long-lived SubscribeRequest into the mesh hub's
        //    responseSubjects and the leak detector flags it at dispose. The
        //    upstream sync streams themselves are disposed with the cacheHub's
        //    workspace (they are cached there); MarkEvicted makes any straggling
        //    wrapper reference fail over instead of attaching to a dead subject.
        foreach (var (path, lazyEntry) in _streams)
        {
            if (!lazyEntry.IsValueCreated) continue;
            try
            {
                lazyEntry.Value.MarkEvicted();
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
        _transientStreaks.Clear();
    }

    /// <summary>
    /// Resolves the live entry for <paramref name="path"/>, creating it when absent —
    /// the same Lazy-in-ConcurrentDictionary dedup as before (factory side effects run
    /// at most once per key), now with a pin-or-recreate loop on top: <c>TryTouch</c>
    /// atomically verifies the entry is not evicted AND refreshes its sliding idle
    /// window, so a successfully returned entry cannot be idle-evicted for a full
    /// window — the caller's subsequent subscribe/write can never race the sweep. An
    /// evicted entry (idle sweep / Invalidate / storm breaker) is unlinked pair-exact
    /// (never a newer entry) and transparently re-created, exactly like a write after
    /// update-queue eviction.
    /// </summary>
    private Entry GetEntry(string path)
    {
        while (true)
        {
            var lazy = _streams.GetOrAdd(path, p => new Lazy<Entry>(
                () => CreateEntry(p), LazyThreadSafetyMode.ExecutionAndPublication));
            var entry = lazy.Value;
            if (entry.TryTouch())
                return entry;
            _streams.TryRemove(new KeyValuePair<string, Lazy<Entry>>(path, lazy));
        }
    }

    private Entry CreateEntry(string p)
    {
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
            // Eager hydration (vs AutoConnect(1)) keeps the upstream alive for
            // the ENTRY's lifetime — no per-consumer RefCount churn on the
            // upstream, identity captured deterministically at entry-creation
            // rather than at first random consumer. The entry's lifetime is
            // itself bounded: the idle sweep releases it (closing this
            // subscription AND the underlying sync stream) once the path has
            // had no subscriber and no read/write hit for the idle window —
            // the next read transparently re-runs this factory.
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
            // ReplaySubject (opens NO additional upstream). A node that actually
            // RESOLVED (non-null) ⇒ clear any negative-cache entry; terminal error ⇒
            // record the failure + grow the backoff via RecordNegative. Composed with
            // hydrationSub so both tear down together on eviction / mesh disposal.
            //
            // 🚨 Clear ONLY on a non-null resolution. A missing-node read can emit a
            // transient null/empty snapshot BEFORE its NotFound OnError; counting that
            // null as "resolved" wiped the negative entry every re-probe, so the next
            // OnError's FailCount reset to 1 and the backoff never grew past the 2 s
            // base — the path re-probed every ~2–4 s forever instead of decaying to the
            // 5 min cap (the atioz `_Activity` / missing-node NotFound resubscribe storm
            // that saturated the action block and tripped the liveness probe). A real
            // resolution is a non-null node; nothing else clears the breaker.
            //
            // 🚨 Record ONLY a genuine missing-node failure — never a TRANSIENT owner miss.
            // A per-node grain that Orleans idle-collected answers the next read's
            // SubscribeRequest with a transient reactivation reject ("Forwarding failed …
            // to invalid activation. Rejecting now.") or a 60 s request timeout. Poisoning
            // the negative cache with THAT made the storm-breaker replay the raw Orleans
            // reject to every reader for the backoff window AND refuse to re-probe the grain
            // that already reactivated — so navigating to a just-idle page crashed until a
            // manual reload outlasted the window. The node exists; it is momentarily
            // unreachable. Forward the error to THIS subscriber (its caller / the area's
            // transient classifier retries) but leave the cache clean so the very next read
            // re-probes the fresh activation. Symmetric with the write path (see the
            // [UpdateQueue] FAILED branch), which already guards RecordNegative the same way.
            // Store the disposal on the Entry so the mesh hub's pre-Quiescing
            // disposal hook (registered in the ctor) can cancel it — and so the
            // idle sweep can close it when the path goes quiet. Without this,
            // every cache.GetStream(path) leaks a long-lived SubscribeRequest
            // into the mesh hub's responseSubjects and the test base's leak
            // detection flags it at dispose. The bookkeeping observer is attached
            // AFTER the Entry exists so a terminal error can flag the entry as
            // FAULTED (its ReplaySubject then holds an OnError terminal that
            // every later subscriber would replay) — the change-feed invalidation
            // reset (ResetFailureState) evicts exactly those entries. ReplaySubject
            // replays a terminal to late subscribers, so an error landing between
            // the hydration subscribe above and this attach is still observed.
            var disposal = new System.Reactive.Disposables.CompositeDisposable(hydrationSub);
            var entry = new Entry(handle, inner.AsObservable(), disposal);
            var bookkeeping = inner.AsObservable().Subscribe(
                node =>
                {
                    if (node is not null)
                    {
                        _negative.TryRemove(p, out _);
                        _transientStreaks.TryRemove(p, out _);
                    }
                },
                ex =>
                {
                    entry.MarkFaulted();
                    if (IsMissingNodeFailure(ex)) RecordNegative(p, ex);
                    // A transient owner failure stays out of the negative cache (the node
                    // exists — see IsTransientOwnerFailure), but a STREAK of them is the
                    // poisoned-activation loop; record it so re-probes back off past the grace.
                    else if (IsTransientOwnerFailure(ex)) RecordTransient(p, ex);
                });
            disposal.Add(bookkeeping);
            return entry;
        }
    }

    /// <summary>
    /// The per-path shared read view handed to every consumer: a COLD wrapper that, at
    /// subscription time, pins the CURRENT live entry — registering on its subscriber
    /// refcount so the idle sweep can never release a stream that is actually being
    /// observed — and relays the entry's Replay(1) hydration. Unsubscribing releases
    /// the refcount and restarts the idle window. A wrapper reference held across an
    /// idle release transparently re-opens the path on its next subscription: the
    /// subscribe IS a read, exactly like a write after update-queue eviction.
    /// </summary>
    private IObservable<MeshNode> SharedView(string path) =>
        Observable.Create<MeshNode>(observer =>
        {
            while (true)
            {
                var entry = GetEntry(path); // pins: live entry, idle window refreshed
                // Between GetEntry's pin and this refcount registration the sweep
                // cannot idle-evict (the pin just reset the window), but a FORCED
                // eviction (node delete / storm breaker) can still land — loop and
                // re-resolve; a fresh entry cannot be evicted before we register.
                if (!entry.TryAddSubscriber())
                    continue;
                IDisposable sub;
                try
                {
                    sub = entry.Replay.Subscribe(observer);
                }
                catch
                {
                    entry.RemoveSubscriber();
                    throw;
                }
                return System.Reactive.Disposables.Disposable.Create(() =>
                {
                    sub.Dispose();
                    // Refcount release ALSO restarts the idle window — "idle" is
                    // measured from the last unsubscribe, never mid-subscription.
                    entry.RemoveSubscriber();
                });
            }
        });

    /// <summary>
    /// One idle-sweep pass over the read cache: releases every entry whose shared
    /// stream has had NO live subscriber and NO read/write hit for the full idle
    /// window. Release = close the hydration subscription AND dispose the path's
    /// upstream sync streams (posting <c>UnsubscribeRequest</c> to the owner — the
    /// owner-side mirror unsubscribes and the 45s heartbeat dies), then drop the
    /// entry so the NEXT read transparently re-opens. 🚨 CLOSES ONLY — never
    /// re-subscribes (2026-06-08 rule; same discipline as the storm breaker).
    ///
    /// <para>The detach-then-mark protocol guarantees a mid-release concurrent read
    /// can neither adopt a doomed upstream nor lose a live subscription:</para>
    /// <list type="number">
    ///   <item>Advisory idle pre-check (<see cref="Entry.IsIdleCandidate"/>).</item>
    ///   <item>DETACH the path's upstream sync streams from the cacheHub workspace —
    ///     from this instant a concurrent read/write builds a FRESH upstream and can
    ///     no longer adopt a detached instance; the current entry's hydration stays
    ///     attached and live.</item>
    ///   <item>Authoritative <see cref="Entry.TryMarkIdleEvicted"/> under the entry
    ///     gate. LOST (a reader pinned/subscribed since the pre-check) ⇒ re-park the
    ///     detached streams (they stay live for the entry) and skip — the entry
    ///     survives untouched.</item>
    ///   <item>WON ⇒ nothing is attached (the refcount was zero for the whole
    ///     window): unlink the entry pair-exact, dispose the hydration subscription
    ///     FIRST (so upstream teardown can never surface into the storm-breaker
    ///     negative cache), then dispose the detached upstreams.</item>
    /// </list>
    /// </summary>
    private void ReleaseIdleReadStreams()
    {
        if (System.Threading.Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            foreach (var (path, lazy) in _streams)
            {
                if (!lazy.IsValueCreated)
                    continue;
                var entry = lazy.Value;
                if (!entry.IsIdleCandidate(readStreamIdleExpiration))
                    continue;

                var detached = entry.Handle.DetachUpstreams();
                if (!entry.TryMarkIdleEvicted(readStreamIdleExpiration))
                {
                    // Lost the race — a consumer pinned the entry between the
                    // pre-check and the mark. Hand the upstreams back; the entry
                    // keeps serving them. (A later write may open a fresh upstream
                    // alongside — the same benign divergence a change-feed evict
                    // produces; both are reaped on the eventual release.)
                    entry.Handle.ReparkUpstreams(detached);
                    continue;
                }

                _streams.TryRemove(new KeyValuePair<string, Lazy<Entry>>(path, lazy));
                var upstreamReleased = TearDownEntry(path, entry, detached);
                logger.LogDebug(
                    "MeshNodeStreamCache: released idle read stream for {Path} (upstreams disposed: {Count})",
                    path, detached.Count);
                readStreamEvictions.OnNext(new ReadStreamEviction(path, upstreamReleased, "idle"));
            }
        }
        catch (Exception ex)
        {
            // Never let a sweep fault kill the interval subscription silently — the
            // leak would quietly return. Log loudly; the next tick runs a fresh pass.
            logger.LogError(ex, "MeshNodeStreamCache: idle read-stream sweep failed");
        }
    }

    /// <summary>
    /// Tears down an already-evicted, unlinked entry: disposes the hydration +
    /// storm-breaker bookkeeping FIRST (so upstream disposal can never surface a
    /// teardown error into the negative cache and poison the next natural read),
    /// then disposes the detached upstream sync streams — closing the
    /// SubscribeRequest at the owner and stopping the client-side heartbeat.
    /// Returns true when at least one upstream sync stream was disposed.
    /// </summary>
    private bool TearDownEntry(string path, Entry entry, IReadOnlyList<ISynchronizationStream> detached)
    {
        try
        {
            entry.HydrationSub.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex,
                "MeshNodeStreamCache: error disposing hydration subscription for {Path}", path);
        }
        var released = false;
        foreach (var stream in detached)
        {
            try
            {
                stream.Dispose();
                released = true;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex,
                    "MeshNodeStreamCache: error disposing upstream sync stream for {Path}", path);
            }
        }
        return released;
    }

    /// <summary>
    /// Records an upstream read failure for <paramref name="path"/> in the storm
    /// breaker's negative cache with an exponential-backoff window
    /// (<see cref="StormBaseCooldown"/> · 2^(n-1), capped at
    /// <see cref="StormMaxCooldown"/>). Crossing <see cref="StormFailThreshold"/>
    /// consecutive failures logs ONE warning. Never re-subscribes — purely records
    /// state that <see cref="GetStreamRaw"/> consults to fast-fail.
    /// Internal as a test seam: the recycle-reset tests seed the grown failure
    /// history (a compile-error era's worth of consecutive activation failures)
    /// deterministically instead of waiting out real backoff windows.
    /// </summary>
    internal void RecordNegative(string path, Exception error)
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
    /// Records a TRANSIENT owner fault (<see cref="IsTransientOwnerFailure"/>) for
    /// <paramref name="path"/> in the transient-fault breaker. The first
    /// <see cref="TransientGraceFailures"/> consecutive faults open NO window (the ordinary
    /// just-idle reactivation miss keeps its instant re-probe); past the grace, each further
    /// fault opens an exponential-backoff window (<see cref="TransientBaseCooldown"/> ·
    /// 2^(n-grace-1), capped at <see cref="TransientMaxCooldown"/>) that
    /// <see cref="GetStreamRaw"/> fast-fails inside — breaking the poisoned-activation
    /// re-probe loop. A streak whose last fault is older than
    /// <see cref="TransientStreakExpiry"/> restarts from 1 (a blip an hour ago is not
    /// evidence about now). Crossing the grace logs ONE warning. Never re-subscribes —
    /// purely records state, mirroring <see cref="RecordNegative"/>.
    /// Internal as a test seam.
    /// </summary>
    internal void RecordTransient(string path, Exception error) =>
        RecordTransient(path, error, DateTimeOffset.UtcNow);

    /// <summary>Clock-injectable seam so the streak-expiry rule is testable without real waits.</summary>
    internal void RecordTransient(string path, Exception error, DateTimeOffset now)
    {
        var priorFails =
            _transientStreaks.TryGetValue(path, out var existing) && now - existing.LastFailAt < TransientStreakExpiry
                ? existing.FailCount
                : 0;
        var failCount = priorFails + 1;
        var openUntil = now;
        if (failCount > TransientGraceFailures)
        {
            var backoffTicks = Math.Min(
                TransientBaseCooldown.Ticks * (1L << Math.Min(failCount - TransientGraceFailures - 1, 20)),
                TransientMaxCooldown.Ticks);
            openUntil = now + TimeSpan.FromTicks(backoffTicks);
        }
        _transientStreaks[path] = new TransientStreak(error, failCount, openUntil, now);
        if (failCount == TransientGraceFailures + 1)
            logger.LogWarning(
                "[TRANSIENT-BREAKER] '{Path}' has faulted {FailCount} consecutive times with a transient-classified "
                + "owner failure: {Error}. The fault is empirically NOT transient (a poisoned activation / wedged owner) — "
                + "re-probes now back off exponentially (cap {Cap}s) instead of hammering the owner. A recycle or a "
                + "successful resolution clears the streak immediately.",
                path, failCount, error.Message, TransientMaxCooldown.TotalSeconds);
    }

    /// <summary>
    /// True when an owner failure means the node/hub does not exist (NotFound / activation
    /// failed) — the only failure class the storm-breaker suppresses. RLS denials and
    /// <see cref="IsTransientOwnerFailure">transient routing errors</see> are excluded so an
    /// existing-but-busy (or a just-idle-collected, mid-reactivation) node is never falsely
    /// blocked from reads OR writes.
    ///
    /// <para>🚨 Transient takes PRECEDENCE. A grain that idle-collected and is mid-
    /// <c>DeactivateOnIdle</c> answers the next delivery with an Orleans forwarding-reject
    /// ("Forwarding failed … to invalid activation. Rejecting now.") or, if reactivation is
    /// slow, the 60&#160;s hub-request timeout ("… target hub was not found"). Without the
    /// transient guard first, that transient miss would be POISONED into the negative cache:
    /// the storm-breaker would then replay the raw Orleans reject to every reader for the whole
    /// backoff window and refuse to re-probe the grain that already reactivated — the exact
    /// "navigating to an idle page intermittently crashes; a manual reload fixes it" bug (the
    /// reload just outlasts the 2&#160;s window and the re-probe lands on the fresh activation).
    /// The <c>&amp;&amp; !IsTransientOwnerFailure</c> guard also protects the "activation failed"
    /// substring below — a transient activation miss must never be read as a permanent absence.
    /// The node exists; it is transiently unreachable — never suppress it.</para>
    /// </summary>
    internal static bool IsMissingNodeFailure(Exception error) =>
        !IsTransientOwnerFailure(error)
        && (error.Message.Contains("No node found", StringComparison.OrdinalIgnoreCase)
            || error.Message.Contains("activation failed", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when an owner failure is TRANSIENT — the node exists but is momentarily
    /// unreachable, so a later read/write is likely to succeed and the storm-breaker must
    /// NEVER record it as a negative (missing-node) entry. Chiefly the Orleans grain-
    /// reactivation window: a per-node grain that Orleans idle-collected answers the next
    /// delivery with an <c>OrleansMessageRejectionException</c> ("… to invalid activation.
    /// Rejecting now.", surfaced through routing as a <c>DeliveryFailure{ErrorType.Failed}</c>
    /// whose message carries "Forwarding failed" / "invalid activation" / "Rejecting now"),
    /// or — when reactivation outruns the request budget — a <see cref="TimeoutException"/>
    /// ("No response received in hub …" / "target hub was not found" / "undeliverable").
    /// Each self-heals: the grain reactivates and the very next probe lands on the fresh
    /// instance, so the error is forwarded to the current subscriber (whose caller retries)
    /// but the cache stays clean. Mirrors <c>RoutingGrain.IsTransientFailure</c> and
    /// <c>AreaErrorClassifier.IsTransientHubFailure</c> so all three layers agree on which
    /// failures are worth a retry rather than a suppress.
    /// </summary>
    internal static bool IsTransientOwnerFailure(Exception? error)
    {
        for (var e = error; e != null; e = e.InnerException)
        {
            if (e is TimeoutException) return true;
            // OrleansMessageRejectionException lives in Orleans.Core, which this project does
            // not (and must not) reference — match by type name so the classifier stays free
            // of the Orleans dependency while still catching the raw grain-reject that reaches
            // the cache from the silo-side routing grain.
            if (e.GetType().Name == "OrleansMessageRejectionException") return true;
            var msg = e.Message ?? string.Empty;
            // The routing grain surfaces the exhausted-forward reject as a DeliveryFailure whose
            // message is "Delivery to '{path}' failed: Forwarding failed: tried to forward … to
            // invalid activation. Rejecting now." — a TRANSIENT reactivation miss, not a real
            // missing node. The 60 s hub-request timeout banner adds the "… hub was not found" /
            // "undeliverable" / "No response received in hub" forms.
            if (msg.Contains("invalid activation", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("Rejecting now", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("Forwarding failed", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("target hub was not found", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("No response received in hub", StringComparison.OrdinalIgnoreCase)) return true;
            if (msg.Contains("undeliverable", StringComparison.OrdinalIgnoreCase)) return true;
            // MessageService.ScheduleNotify's shutdown-drop NACK (ErrorType.ShuttingDown):
            // a delivery raced the target hub's DisposeRequest (recycle / delete / restart).
            // Transient by design — the address may reactivate (recycle), and if the node is
            // genuinely gone the NEXT probe gets the authoritative routing NotFound.
            if (msg.Contains("is shutting down", StringComparison.OrdinalIgnoreCase)) return true;
            // A silo that momentarily lost its Postgres connection (host reboot, network
            // blackout, admin restart/failover, pool exhaustion) surfaces the owner READ as
            // an Npgsql/Postgres fault. This is a node-EXISTS-but-DB-unreachable miss — the
            // node is fine, the connection is not, so a later read succeeds and the transient
            // breaker's ≤60s re-probe self-heals it. Without this the fault fell through to
            // the un-broker'd "faulted forever" bucket and permanently wedged EVERY
            // re-activation until a manual recycle (memex AgenticEngineering, 2026-07-23:
            // Azure emergency host repair rebooted the node → PG unreachable ~2 min → course
            // dead until an admin recycle). See IsTransientDatabaseFailure.
            if (IsTransientDatabaseFailure(msg)) return true;
        }
        return false;
    }

    /// <summary>
    /// Canonical markers of a TRANSIENT database-connectivity fault — the connection
    /// dropped or the endpoint was momentarily unreachable, not a query/schema error
    /// (a real error such as <c>42P01</c> undefined_table must NOT match — it is a
    /// legitimate "no such relation", handled elsewhere as an empty read).
    /// <para>Matched by MESSAGE, not exception type, on purpose: across the Orleans grain
    /// boundary the typed <c>NpgsqlException</c>/<c>PostgresException</c> is flattened to
    /// its message string (the inner <see cref="TimeoutException"/> object does not survive
    /// serialization), so the same string is what reaches the cache whether the read faulted
    /// in-process (monolith) or cross-silo (Orleans portal). Kept in sync with the mirrored
    /// classifiers <c>AreaErrorClassifier.IsTransientHubFailure</c>,
    /// <c>RoutingGrain.IsTransientFailure</c> and
    /// <c>SynchronizationStream.IsTransientHubTimeout</c>.</para>
    /// </summary>
    internal static readonly string[] TransientDatabaseFailureMarkers =
    [
        "Failed to connect",                                    // Npgsql: endpoint unreachable
        "Timeout during connection attempt",                    // Npgsql: connect timed out
        "error connecting",                                     // Npgsql connect wrapper
        "connection reset",                                     // socket reset by peer
        "existing connection was forcibly closed",              // Windows socket reset
        "server closed the connection unexpectedly",            // backend gone mid-query
        "connection pool has been exhausted",                   // pool starvation during a blackout
        "terminating connection due to administrator command",  // 57P01 admin_shutdown / failover
        "the database system is starting up",                   // 57P03 — post-restart warmup
        "the database system is shutting down",                 // 57P03
        "the database system is in recovery mode",              // crash recovery
        "no connection could be made",                          // socket refused
        "connection refused",                                   // socket refused
    ];

    /// <summary>
    /// True when <paramref name="message"/> is a transient database-connectivity fault
    /// (see <see cref="TransientDatabaseFailureMarkers"/>) rather than a real query/schema
    /// error — so the storm/transient breaker treats it as a retryable miss that self-heals
    /// instead of caching it as a permanent owner failure.
    /// </summary>
    internal static bool IsTransientDatabaseFailure(string? message) =>
        !string.IsNullOrEmpty(message)
        && Array.Exists(TransientDatabaseFailureMarkers,
            m => message.Contains(m, StringComparison.OrdinalIgnoreCase));

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
        // Entry so the NEXT read re-probes the owner EXACTLY ONCE (re-probe is driven
        // by a real read, never an auto-resubscribe). FailCount is retained so a
        // repeat failure backs off further; a success clears it (see GetEntry).
        //
        // 🚨 Two guards keep the re-probe from turning into a subscribe/unsubscribe storm
        // that wedges the owner hub (the DevLogin self-provision hang):
        //   1. Reprobing: only the FIRST read past the window evicts + reopens; while the
        //      fresh probe is IN FLIGHT, later reads fall through to SharedView and share
        //      it (the atomic TryUpdate lets exactly one reader win). Without this, every
        //      ~100ms read re-evicted the still-hydrating fresh probe before it resolved,
        //      so the negative entry never cleared.
        //   2. The stale-entry eviction disposes ONLY the Rx hydration + marks the entry
        //      evicted — it does NOT synchronously dispose the upstream sync stream. A
        //      terminally-errored upstream ("No node found") has ALREADY torn down its own
        //      keep-alive/heartbeat, so it leaks nothing; but posting UnsubscribeRequest to
        //      the owner mid-reprobe RACES the fresh re-subscribe and wedges the owner's
        //      action block (its snapshot DataChangedEvent then never lands → the read
        //      never resolves). The idle sweep reaps genuinely-idle LIVE upstreams (the
        //      actual leak); an errored/superseded upstream is reaped by the change-feed
        //      parking + workspace disposal, exactly as before the idle-release change.
        if (_negative.TryGetValue(path, out var neg))
        {
            if (neg.OpenUntil > DateTimeOffset.UtcNow)
                return Observable.Throw<MeshNode>(neg.Error);
            if (!neg.Reprobing
                && _negative.TryUpdate(path, neg with { Reprobing = true }, neg)
                && _streams.TryRemove(path, out var staleLazy) && staleLazy.IsValueCreated)
            {
                try
                {
                    // Drop the errored entry's Rx hydration and mark it evicted so any
                    // straggling SharedView wrapper transparently re-resolves. Deliberately
                    // NOT DetachUpstreams()/TearDownEntry() here — see guard #2 above.
                    var stale = staleLazy.Value;
                    stale.MarkEvicted();
                    stale.HydrationSub.Dispose();
                    readStreamEvictions.OnNext(new ReadStreamEviction(path, false, "storm-breaker"));
                }
                catch (Exception ex) { logger.LogDebug(ex, "MeshNodeStreamCache: error disposing stale entry for {Path}", path); }
            }
        }

        // TRANSIENT-FAULT BREAKER: same fast-fail + single-flight re-probe discipline as the
        // negative cache above, but for a persistent STREAK of "transient" owner faults (see
        // _transientStreaks). Within the grace no entry has an open window, so this block is a
        // no-op for the ordinary just-idle reactivation miss.
        if (_transientStreaks.TryGetValue(path, out var streak))
        {
            if (streak.OpenUntil > DateTimeOffset.UtcNow)
                return Observable.Throw<MeshNode>(streak.Error);
            if (streak.FailCount > TransientGraceFailures
                && !streak.Reprobing
                && _transientStreaks.TryUpdate(path, streak with { Reprobing = true }, streak)
                && _streams.TryRemove(path, out var staleTransientLazy) && staleTransientLazy.IsValueCreated)
            {
                try
                {
                    var stale = staleTransientLazy.Value;
                    stale.MarkEvicted();
                    stale.HydrationSub.Dispose();
                    readStreamEvictions.OnNext(new ReadStreamEviction(path, false, "transient-breaker"));
                }
                catch (Exception ex) { logger.LogDebug(ex, "MeshNodeStreamCache: error disposing stale entry for {Path}", path); }
            }
        }

        var shared = SharedView(path);

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
        // DISPOSED-CACHE GUARD (write side): a late write after this cache's Dispose() — the
        // canonical case is an agent round whose ThreadExecution.PushToResponseMessage is still
        // streaming when its mesh's cacheHub tore down (State captured by TeardownStragglerCapturer:
        // UpdateRaw → _updateQueues.TryGetValue → MemoryCache.CheckDisposed) — must NOT touch the
        // disposed _updateQueues MemoryCache, whose TryGetValue throws a synchronous
        // ObjectDisposedException on the caller's ThreadPool continuation. Unobserved, that reaches
        // AppDomain.UnhandledException and xUnit escalates it to a "Catastrophic failure" that reds
        // an otherwise-green shard. Dispose() sets _disposed=1 BEFORE it disposes _updateQueues, so
        // this flag check (same Volatile idiom as ReleaseIdleReadStreams) fully covers the straggler.
        // Return the same graceful Observable.Throw terminal the negative-cache breaker below uses —
        // observed by the caller's Subscribe (a benign teardown write), never an unobserved throw.
        if (System.Threading.Volatile.Read(ref _disposed) != 0)
            return Observable.Throw<MeshNode>(new ObjectDisposedException(nameof(MeshNodeStreamCache)));

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

                // 🚨 Deliver the write's terminal to the caller's result on a subscription
                // whose lifetime is INDEPENDENT of the queue slot. entry.Handle.Update is
                // UpdateRemote, which now BOUNDS its wait for the owner's PatchDataResponse
                // (UpdateResponseWaitBound, ~2s): it emits as soon as the activity is STARTED
                // (the patch is accepted) and fails fast on a denial/rejection, but on a
                // busy-owner timeout it falls back to the optimistic snapshot rather than
                // wait for the round to finish — killing the old 30s "Response did not arrive
                // in time" GUI stall. RLS is still enforced authoritatively by the owner and a
                // genuine denial still surfaces fail-fast on the caller's OnError. Tracked in
                // _inflightWrites for mid-flight cache disposal; self-removes on terminal so
                // it never accumulates.
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
                        // Write activity refreshes the read entry's idle window — GetEntry
                        // pinned it at write START; touching again on each terminal keeps
                        // an in-flight write's upstream out of the idle sweep's reach.
                        entry.Touch();
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
                        entry.Touch();
                        req.Result.OnError(ex);
                        Settle();
                    },
                    () =>
                    {
                        logger.LogDebug(
                            "[UpdateQueue] COMPLETE path={Path} seq={Seq} totalElapsed={ElapsedMs}ms",
                            path, req.Seq, (DateTimeOffset.UtcNow - req.EnteredAt).TotalMilliseconds);
                        entry.Touch();
                        req.Result.OnCompleted();
                        Settle();
                    });

                // 🚨 Advance the per-path queue on the FIRST signal OR QueueAdvanceBound.
                // UpdateRemote now bounds its owner-response wait (~2s) and falls back to an
                // optimistic emit, so req.Result fires promptly (on the ack, a fail-fast
                // error, or the bound) — the queue advances without the old 30s stall. The
                // QueueAdvanceBound stays as a backstop for a write whose post itself stalls.
                // Observe req.Result (already fed above) instead of opening a SECOND
                // subscription to the cold update (which would post the patch twice). Complete
                // without emitting so Concat moves to the next queued write.
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
        GetStreamRaw(path).Select(node => ConvertContentJsonElementToTyped(node, options, logger));

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
            var typed = ConvertContentJsonElementToTyped(node, options, logger);
            var updated = update(typed);
            return ConvertContentTypedToJsonElement(updated, options);
        };

        // RLS on the write is enforced authoritatively by the OWNER: the
        // [RequiresPermission(Permission.Update)] gate on PatchDataRequest posts
        // DeliveryFailure(Unauthorized) on denial, which UpdateRemote surfaces fail-fast
        // as UnauthorizedAccessException on the caller's Rx OnError (the denial is posted
        // by the pipeline gate ahead of the owner's action block, so it returns within
        // UpdateRemote's short response bound even while a round is running). The previous
        // cache-only client gate here was a stopgap for when UpdateRemote emitted purely
        // optimistically and swallowed the denial — redundant now that UpdateRemote bounds
        // (not eliminates) its wait and re-surfaces the denial.
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
        // Disposed-cache guard — see UpdateRaw. A late overwrite after Dispose() must not hit the
        // disposed _updateQueues MemoryCache; return a graceful observed terminal, never a throw.
        if (System.Threading.Volatile.Read(ref _disposed) != 0)
            return Observable.Throw<MeshNode>(new ObjectDisposedException(nameof(MeshNodeStreamCache)));

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

    private static MeshNode ConvertContentJsonElementToTyped(MeshNode node, JsonSerializerOptions options, ILogger logger)
    {
        // Only convert when the cache emitted a raw JsonElement (the cache hub
        // doesn't know domain types, so Content lands here as JsonElement). If
        // the cache somehow already has a typed value (e.g. a same-process
        // caller already converted), pass through.
        if (node.Content is JsonElement je)
        {
            var deserialized = je.Deserialize<object>(options);
            // 🚨 Bad-data tolerance: Deserialize<object> degrades to a JsonElement again
            // when the caller's TypeRegistry can't resolve the $type discriminator. The
            // node then crosses the GetStream/GetMeshNodeStream boundary STILL untyped,
            // so every downstream `Content is X` / `as X` silently fails (renders empty,
            // reactive waits time out with no visible cause). Surface it LOUDLY here —
            // the degrade was previously invisible at this seam.
            if (deserialized is JsonElement)
            {
                logger.LogWarning(
                    "MeshNodeStreamCache.GetStream: Content for {Path} stayed an untyped JsonElement after "
                    + "deserialization (TypeRegistry lacks the $type discriminator) — downstream "
                    + "'Content is X'/'as X' consumers will fail (renders empty, reactive waits time out). "
                    + "Raw: {RawJson}",
                    node.Path, TruncateRaw(je));
            }
            return node with { Content = deserialized };
        }
        return node;
    }

    // Truncated raw JSON for diagnostics — bad-data warnings include the offending
    // content so the corrupt row is identifiable in Loki without flooding the log.
    private const int RawJsonLogMax = 512;
    private static string TruncateRaw(JsonElement je)
    {
        var raw = je.GetRawText();
        return raw.Length <= RawJsonLogMax ? raw : raw[..RawJsonLogMax] + "… (truncated)";
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
                    var updates = typeSource.StreamUpdates();
                    // 🚨 Hold SYSTEM identity across this synced-query subscription — the SAME
                    // pattern (and for the SAME reason) as ChatClientCredentialResolver
                    // .RebuildSubscription. The .SubscribeOn(TaskPoolScheduler) below runs this
                    // Defer + StreamUpdates' cross-hub SubscribeRequests on a POOL thread where the
                    // caller's AsyncLocal AccessContext has NOT reliably flowed across the scheduler
                    // hop. SubscribeRequest is [RequiresPermission(Read)] — NOT [SystemMessage]-exempt
                    // — so a null-context one fails CLOSED at the owning partition, and the synced
                    // query then rides the 15s deadlock-guard timeout before recovering (the
                    // ProviderKeyEncryptionTest / ConnectStrategyTest "observable did not emit within
                    // 15s" flake — a freshly-provisioned partition under suite load). Setting the
                    // identity HERE, on the actual subscribe thread, guarantees coverage regardless of
                    // whether the caller's own identity flowed through SubscribeOn.
                    //
                    // 🚨🚨 It MUST be ImpersonateAsSystem (system-security), NOT
                    // SwitchAccessContext(MeshNodeCacheIdentity.Context). The cache hub is declared
                    // WithPostingIdentity(System) (see the cacheHub config in the ctor), so its sync
                    // sub-hubs already post as system-security. system-security therefore MATCHES the
                    // hub's posting identity: setting it on the AsyncLocal is consistent with the
                    // fallback (PostPipeline stamps the non-null Context, which equals what the System
                    // fallback would have stamped) — there is no divergent identity anywhere on the
                    // sync protocol. The previous code used cache/mesh-node-cache, a DIFFERENT
                    // (Read-only) identity that diverged from the hub's System posting identity; that
                    // divergence is what produced the constant DeliveryFailure storm on sync/<id>
                    // (db15ff014, observed ~540/min in prod). The query reads as System regardless
                    // (MeshQueryRequest carries WellKnownUsers.System and short-circuits the validator
                    // chain); per-user RLS is re-applied at the consumer (the GetQuery options
                    // overload's WrapWithPerUserRls), never here. See AccessContextPropagation.md.
                    var accessService = cacheHub.ServiceProvider.GetService<AccessService>();
                    return accessService is null
                        ? updates
                        : Observable.Using(() => accessService.ImpersonateAsSystem(), _ => updates);
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
                (IEnumerable<MeshNode>)items.Select(node => DeserializeContent(node, state.options, state.logger)).ToArray()),
            (raw, options, logger));
    }

    private static MeshNode DeserializeContent(MeshNode node, JsonSerializerOptions options, ILogger logger)
    {
        if (node.Content is not JsonElement je || je.ValueKind != JsonValueKind.Object)
            return node;
        try
        {
            var deserialized = JsonSerializer.Deserialize<object>(je.GetRawText(), options);
            if (deserialized is null)
                return node;
            // 🚨 Bad-data tolerance: Deserialize<object> degrades BACK to a JsonElement when
            // the caller's TypeRegistry can't resolve the $type — the GetQuery resolution
            // boundary then hands consumers an untyped JsonElement and every `Content is X`
            // soft-cast silently fails. Surface it (don't fault the query) so the corrupt row
            // is visible rather than rendering empty / timing out a reactive wait.
            if (deserialized is JsonElement)
            {
                logger.LogWarning(
                    "MeshNodeStreamCache.GetQuery: Content for {Path} stayed an untyped JsonElement after "
                    + "deserialization (TypeRegistry lacks the $type discriminator) — downstream "
                    + "'Content is X'/'as X' consumers will fail (renders empty). Raw: {RawJson}",
                    node.Path, TruncateRaw(je));
                return node;
            }
            return node with { Content = deserialized };
        }
        catch (Exception ex)
        {
            // Was a bare `catch { return node; }` — a swallowed deserialization fault at the
            // cross-hub GetQuery boundary that left Content an untyped JsonElement with NO
            // trace. Keep returning the node (a read must not fault the whole query) but make
            // the fault VISIBLE — this is the secretly-errors-as-a-timeout class.
            logger.LogWarning(ex,
                "MeshNodeStreamCache.GetQuery: FAILED to deserialize Content for {Path} — content stays an "
                + "untyped JsonElement; downstream 'Content is X' will fail (renders empty). Raw: {RawJson}",
                node.Path, TruncateRaw(je));
            return node;
        }
    }

    /// <summary>
    /// Removes the cached entry for <paramref name="path"/> so the next
    /// <c>GetStream</c> call rebuilds a fresh stream. Called by
    /// <c>HandleDeleteNodeRequest</c> after the persistence delete commits —
    /// the Replay(1) cache otherwise holds the pre-delete MeshNode forever
    /// (the upstream observable doesn't emit "deleted" — the per-node hub is
    /// gone). Also drops the storm-breaker negative entry for the path — an
    /// invalidation means "give this path a completely fresh resolution attempt",
    /// so a stale failure window (error, fail count, backoff) must not outlive
    /// it; if the path is genuinely absent, the next read re-records. Idempotent.
    /// </summary>
    public void Invalidate(string path)
    {
        _negative.TryRemove(path, out _);
        if (_streams.TryRemove(path, out var lazyEntry))
        {
            // Dispose the upstream SubscribeRequest so it doesn't dangle in
            // mesh hub's responseSubjects after the path is deleted — and
            // detach + dispose the underlying sync streams so nothing keeps
            // heartbeating a deleted owner. MarkEvicted makes any straggling
            // wrapper reference transparently re-resolve (and hit the deleted
            // node's NotFound → storm breaker) instead of attaching to a dead
            // subject. Skip for Lazy<Entry> that never ran its factory.
            if (lazyEntry.IsValueCreated)
            {
                try
                {
                    var entry = lazyEntry.Value;
                    var detached = entry.Handle.DetachUpstreams();
                    entry.MarkEvicted();
                    var released = TearDownEntry(path, entry, detached);
                    readStreamEvictions.OnNext(new ReadStreamEviction(path, released, "invalidate"));
                }
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

/// <summary>
/// One released read entry of <see cref="MeshNodeStreamCache"/> — emitted on
/// <c>ReadStreamEvictions</c> for diagnostics and deterministic tests.
/// <paramref name="UpstreamReleased"/> is true when at least one upstream sync
/// stream (the <c>SubscribeRequest</c> client whose 45s heartbeat kept the
/// owner-side mirror alive) was actually disposed as part of the release.
/// <paramref name="Reason"/> ∈ { "idle", "invalidate", "storm-breaker" }.
/// </summary>
internal sealed record ReadStreamEviction(string Path, bool UpstreamReleased, string Reason);
