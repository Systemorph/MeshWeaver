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
/// (<see cref="GetStream"/>) and writes (<see cref="Update"/>) share the same
/// underlying stream, so an update is always visible to every reader.
///
/// <para>The handle is opened on the mesh hub's workspace under
/// <see cref="AccessService.ImpersonateAsSystem"/> — that's correct for the
/// system-internal infrastructure subscription. Per-user enforcement lives
/// at the <see cref="GetStream"/> seam: the cache asks the owning node hub
/// via <see cref="GetPermissionRequest"/> for the current user's effective
/// permissions on the path; only when the response carries
/// <see cref="Permission.Read"/> does the gated observable forward the
/// upstream emissions. Per-(path,user) validations are cached for
/// <see cref="AccessTtl"/> to avoid hammering the owning hub.</para>
///
/// <para><b>Never go around the cache.</b> An ad-hoc
/// <c>workspace.GetRemoteStream(...)</c> from some other hub is a SEPARATE
/// stream instance; updating it is "lost" — never seen by the readers of the
/// cached stream (this was the bug behind compile state never landing on a
/// NodeType's MeshNode). Non-owning hubs MUST use <see cref="Update"/>.</para>
///
/// <para><b>No side-effects on emission.</b> The cache does not kick
/// compilation — opening the stream activates the per-NodeType hub via the
/// <c>SubscribeRequest</c>, and that hub's OWN compile watcher
/// (<c>NodeTypeCompilationHelpers.InstallCompileWatcher</c>) flips
/// <c>CompilationStatus = Pending</c> on its OWN stream only on explicit
/// user-driven <c>RequestedReleaseAt</c> flips.</para>
/// </summary>
internal sealed class MeshNodeStreamCache : IMeshNodeStreamCache
{
    /// <summary>One cache entry: the updatable handle plus the shared,
    /// replay-cached read view over it. The Shared observable is the raw
    /// system-side stream; per-user access gating is applied in
    /// <see cref="GetStream"/> before each subscriber consumes it.</summary>
    private sealed record Entry(MeshNodeStreamHandle Handle, IObservable<MeshNode> Shared, IDisposable HydrationSub);

    /// <summary>
    /// Validity window for a cached <c>(path,user) → Permission</c> probe. A
    /// hit within this window short-circuits the <see cref="GetPermissionRequest"/>
    /// round-trip; a miss issues the request and caches its response. Trade-off:
    /// permission revocations propagate after at most <c>AccessTtl</c> — short
    /// enough for interactive UX, long enough that <c>GetStream</c> hot paths
    /// don't hammer the owning hub. The value matches the canonical
    /// AccessControl cache TTL used elsewhere in the codebase (30s).
    /// </summary>
    private static readonly TimeSpan AccessTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum time we wait for the owning per-node hub to answer
    /// <see cref="GetPermissionRequest"/>. The handler is synchronous
    /// (`AccessControlPipeline.HandleGetPermission` resolves the local
    /// <see cref="SecurityService"/> and posts a response immediately)
    /// once the hub is active. The catch is Orleans cold-start: the per-node
    /// grain may take 5-10s to activate on first touch (cluster placement,
    /// MeshNode hydration, SecurityService warm-up). 15s covers cold start
    /// while still bounding genuinely stuck hubs.
    ///
    /// On timeout we DENY this subscription (Permission.None → Unauthorized)
    /// but DO NOT cache the deny — a single slow activation would otherwise
    /// poison the cache for <see cref="AccessTtl"/> and lock out every
    /// subsequent subscription within that window. Real Deny answers from
    /// the hub are cached as normal.
    /// </summary>
    private static readonly TimeSpan PermissionRequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Backoff between hydration retries when the node doesn't exist yet.</summary>
    private static readonly TimeSpan MissingNodeRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Max retries before propagating "no node found" as a terminal OnError on
    /// the shared subject. With <see cref="MissingNodeRetryDelay"/>=200ms that's
    /// a ~6s window — enough to absorb the create-roundtrip + per-node hub
    /// activation race for newly-dispatched nodes, short enough that a genuinely
    /// missing node surfaces promptly to subscribers.
    /// </summary>
    private const int MaxMissingNodeRetries = 30;

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

        // Register cleanup on the cache hub so hydration subscriptions are
        // disposed at its Shutdown entry (before Quiescing). The cache hub
        // owns the cache's lifetime; tearing it down here cancels every
        // upstream SubscribeRequest the cache opened so the leak detector
        // sees a clean response-subjects set at test-class dispose.
        cacheHub.RegisterForDisposal(_ => DisposeHydrationSubscriptions());
    }

    private void DisposeHydrationSubscriptions()
    {
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
    }

    /// <summary>
    /// Recognises errors that mean "the node doesn't exist yet" — the hydration
    /// path retries these instead of poisoning the shared subject.
    /// </summary>
    private static bool IsMissingNodeError(Exception err) =>
        err is DeliveryFailureException
        && err.Message.Contains("No node found", StringComparison.Ordinal);

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

            // 🚨 Retry hydration on "no node found" — a read-during-create
            // race (typical for delegation sub-threads dispatched during a
            // streaming agent turn, or any reader subscribing before the
            // owner finishes its CreateNodeRequest commit) would otherwise
            // OnError the inner ReplaySubject permanently, poisoning every
            // future subscriber. Internal retry keeps the shared subject
            // valid through the create-roundtrip window. Bounded so a
            // genuinely missing node surfaces OnError after
            // ~(MaxMissingNodeRetries × MissingNodeRetryDelay).
            //
            // Other error classes (permission denials, transient routing,
            // corrupted content) are NOT retried — they propagate to
            // subscribers as before so callers can decide what to do.
            var retries = 0;
            var hydrationObs = Observable.Defer(() => (IObservable<MeshNode>)handle)
                .Catch<MeshNode, Exception>(ex =>
                {
                    if (!IsMissingNodeError(ex)
                        || System.Threading.Interlocked.Increment(ref retries) > MaxMissingNodeRetries)
                        return Observable.Throw<MeshNode>(ex);
                    logger.LogDebug(
                        "MeshNodeStreamCache: missing-node retry {Attempt}/{Max} for {Path}",
                        retries, MaxMissingNodeRetries, p);
                    return Observable.Empty<MeshNode>().Delay(MissingNodeRetryDelay);
                })
                .Repeat();

            IDisposable hydrationSub;
            if (accessService is not null)
            {
                using (accessService.SwitchAccessContext(MeshNodeCacheIdentity.Context))
                    hydrationSub = hydrationObs.Subscribe(synced);
            }
            else
            {
                hydrationSub = hydrationObs.Subscribe(synced);
            }
            // Store hydrationSub on the Entry so the mesh hub's pre-Quiescing
            // disposal hook (registered in the ctor) can cancel it. Without
            // this, every cache.GetStream(path) leaks a long-lived
            // SubscribeRequest into the mesh hub's responseSubjects and the
            // test base's leak detection flags it at dispose.
            return new Entry(handle, inner.AsObservable(), hydrationSub);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>
    /// Returns a per-user access-gated view of the cached shared stream. The
    /// gate is enforced by asking the owning node hub for the current user's
    /// effective permissions via <see cref="GetPermissionRequest"/>; the
    /// response is cached for <see cref="AccessTtl"/> per <c>(path,user)</c>.
    /// On <see cref="Permission.Read"/> ⇒ the upstream observable is returned
    /// directly; on missing Read ⇒ the observable terminates with
    /// <see cref="UnauthorizedAccessException"/>.
    ///
    /// <para>Authoritative source: the node's OWN hub (not the cache, not the
    /// caller's hub). The hub already runs the validator chain when it
    /// answers <see cref="GetPermissionRequest"/>; consulting it keeps the
    /// gate aligned with every other access decision in the system.</para>
    /// </summary>
    public IObservable<MeshNode> GetStream(string path)
    {
        var shared = GetEntry(path).Shared;

        var accessService = meshHub.ServiceProvider.GetService<AccessService>();
        if (accessService is null)
            return shared; // No AccessService (minimal test fixture) — pass-through.

        // RLS not installed on this mesh ⇒ no gate. GetPermissionRequest handler
        // is wired up by AddRowLevelSecurity → AddAccessControlPipeline on every
        // per-node hub; without RLS the message has no handler and the feature
        // makes no sense.
        if (meshHub.Configuration.Get<EffectivePermissionsDelegate>() is null)
            return shared;

        // Capture the caller's identity synchronously, on the caller's thread,
        // before the cold pipeline runs. The CarryAccessContext wrap re-stamps
        // AsyncLocal on each emission so downstream Subscribe callbacks see
        // the same identity regardless of where the emission lands.
        var captured = accessService.Context ?? accessService.CircuitContext;
        if (captured is null || string.IsNullOrEmpty(captured.ObjectId))
            return shared; // No user identity (background / system path) — pass-through.

        // 🚨 Prod-2026-05-21 regression guard: posting GetPermissionRequest to
        // an Address whose first segment is a NodeType name (e.g. "Thread",
        // "AccessAssignment") causes PostgreSqlPathRoutingAdapter to lower-case
        // it into a schema name and `EnsureSchemaForPartitionSync` blows up
        // with `relation "thread.mesh_nodes" does not exist`. The cache gate
        // only makes sense for paths that ARE real partition-rooted node
        // paths. If the first segment is empty or matches a known NodeType
        // name, skip the gate entirely.
        if (LooksLikeNodeTypePath(path))
            return shared;

        var key = (Path: path, UserId: captured.ObjectId);
        return Observable.Defer(() =>
        {
            // TTL cache hit ⇒ short-circuit the round-trip.
            if (_access.TryGetValue(key, out var cached)
                && cached.ValidUntil > DateTimeOffset.UtcNow)
            {
                return GateOnRead(cached.Permissions, shared, path, captured);
            }

            // Miss ⇒ ask the owning hub for the user's effective permissions on
            // this path, then gate. The GetPermissionRequest handler is wired
            // on every per-node hub by AddAccessControlPipeline (called from
            // AddRowLevelSecurity). Without RLS the gate doesn't fire at all
            // — see the no-AccessService bail-out at the top of GetStream.
            //
            // 🚨 Timeout is non-negotiable. The owning hub MUST respond fast
            // once active, but real failure modes (corrupted MeshNode, missing
            // ancestor in the security walk, mesh-hub backlog, Orleans
            // grain-activation cold start) can leave the request unanswered.
            // Without a timeout EVERY subscriber to this path's bubble waits
            // forever — the prod 2026-05-23 thread-page deadlock: one stuck
            // ThreadMessage with a broken delegationPath wedged the entire
            // chat view.
            //
            // Two outcomes:
            //   1. Real response (Allow or Deny) → cache for AccessTtl.
            //   2. Timeout → deny THIS subscription with Permission.None,
            //      but DO NOT cache — a single slow activation would otherwise
            //      lock out every subscriber within the 30s TTL. The next
            //      subscription gets a fresh chance.
            return meshHub.Observe(
                    new GetPermissionRequest(),
                    o => o.WithTarget(new Address(path)).WithAccessContext(captured))
                .Select(d => (d.Message as GetPermissionResponse)?.Permissions ?? Permission.None)
                .Take(1)
                .Select(perms => (Perms: perms, IsTimeout: false))
                .Timeout(PermissionRequestTimeout, Observable.Defer(() =>
                {
                    logger.LogWarning(
                        "GetPermissionRequest timed out after {Timeout} for {Path} (user={User}) — denying " +
                        "this subscription as Permission.None WITHOUT caching. " +
                        "Owning hub did not respond; check for stuck per-node hub, corrupted ancestor, " +
                        "or slow Orleans grain activation.",
                        PermissionRequestTimeout, path, captured.ObjectId);
                    return Observable.Return((Perms: Permission.None, IsTimeout: true));
                }))
                .SelectMany(result =>
                {
                    if (!result.IsTimeout)
                    {
                        _access[key] = new AccessEntry(result.Perms,
                            DateTimeOffset.UtcNow + AccessTtl);
                    }
                    return GateOnRead(result.Perms, shared, path, captured);
                });
        }).CarryAccessContext(accessService);
    }

    private static IObservable<MeshNode> GateOnRead(
        Permission perms, IObservable<MeshNode> shared, string path, AccessContext user)
    {
        if (perms.HasFlag(Permission.Read))
            return shared;
        return Observable.Throw<MeshNode>(new UnauthorizedAccessException(
            $"User '{user.ObjectId}' lacks Read permission on '{path}'"));
    }

    public IObservable<MeshNode> Update(string path, Func<MeshNode, MeshNode> update) =>
        // The underlying MeshNodeStreamHandle.Update already wraps with
        // CarryAccessContext, so writes through the cache automatically carry
        // the caller's user identity into the partition write. No additional
        // wrap needed here. See AccessContextPropagation.md.
        //
        // Concurrency: serialization happens at the OWNING HUB, not here. The
        // hub for `path` is single-threaded — its action block processes
        // UpdateNodeRequest deliveries in order, so concurrent cache.Update
        // calls reach the same hub and are naturally serialized. No semaphore
        // or lock at this layer.
        GetEntry(path).Handle.Update(update);

    /// <summary>
    /// Caller-typed read: every emitted MeshNode's <c>Content</c> is round-tripped
    /// through <paramref name="options"/> so the caller sees a typed domain
    /// instance (<c>ModelProviderConfiguration</c>, etc.) rather than the raw
    /// <c>JsonElement</c> the cache hub stores. See
    /// <see cref="IMeshNodeStreamCache.GetStream(string, JsonSerializerOptions)"/>.
    /// </summary>
    public IObservable<MeshNode> GetStream(string path, JsonSerializerOptions options) =>
        GetStream(path).Select(node => ConvertContentJsonElementToTyped(node, options));

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
        return GetEntry(path).Handle.Update(wrapped);
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
    /// Removes the cached entry for <paramref name="path"/> so the next
    /// <see cref="GetStream"/> call rebuilds a fresh stream. Called by
    /// <c>HandleDeleteNodeRequest</c> after the persistence delete commits —
    /// the Replay(1) cache otherwise holds the pre-delete MeshNode forever
    /// (the upstream observable doesn't emit "deleted" — the per-node hub is
    /// gone). Idempotent.
    /// </summary>
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
    // Lock-free atomic-swap over ImmutableDictionary. Avoids the
    // ConcurrentDictionary footgun where the value factory can be invoked
    // multiple times concurrently for the same key (only one value wins,
    // but every loser's factory side-effects already ran and leaked).
    //
    // The stream itself IS the cache: Replay(1).RefCount() caches the latest
    // snapshot and shares one upstream subscription across all consumers.
    // SubscribeOn(TaskPoolScheduler) ensures the heavy SyncedQueryMeshNodes
    // construction + upstream subscription run on the thread pool — never on
    // the calling hub's action block. First subscriber triggers the Defer;
    // subsequent subscribers attach to the already-cached Replay(1) snapshot.
    private System.Collections.Immutable.ImmutableDictionary<object, IObservable<IEnumerable<MeshNode>>> _queries =
        System.Collections.Immutable.ImmutableDictionary<object, IObservable<IEnumerable<MeshNode>>>.Empty;

    public IObservable<IEnumerable<MeshNode>> GetQuery(object id, params string[] queries)
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
                .RefCount();

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
        var raw = GetQuery(id, queries);
        if (options is null) return raw;
        // Round-trip each emitted MeshNode's Content through the caller's
        // JsonSerializerOptions so consumers see typed domain instances
        // (AccessAssignment, PartitionAccessPolicy, etc.) rather than the
        // raw JsonElement the cache hub stores. Same shape as
        // GetStream(path, options).
        return System.Reactive.Linq.Observable.Select(raw, items =>
            (IEnumerable<MeshNode>)items.Select(node => DeserializeContent(node, options)).ToArray());
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
    /// a known NodeType name (from <see cref="PartitionDefinition.NodeTypeToSuffix"/>).
    /// Used by <see cref="GetStream"/> to skip the access-check round-trip on
    /// non-partition-rooted paths, which previously triggered the prod
    /// 2026-05-21 regression where <see cref="PostgreSqlPathRoutingAdapter"/>
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
        return PartitionDefinition.NodeTypeToSuffix.ContainsKey(firstSegment);
    }
}
