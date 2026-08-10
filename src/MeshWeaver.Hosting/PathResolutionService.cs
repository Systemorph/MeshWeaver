using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Resolves URL paths to hub addresses by delegating to
/// <see cref="IMeshQueryCore.Query{T}"/>. The query expresses
/// <i>"every path that is a prefix of the requested path"</i> via the
/// canonical idiom (see <c>Doc/DataMesh/QuerySyntax.md</c> → "Path
/// Resolution"):
///
/// <code>path:{a|b|c} sort:length(path)-desc limit:1</code>
///
/// where <c>a|b|c</c> is the requested path plus each ancestor. The
/// multi-value <c>path:</c> parses to <c>WHERE path IN (...)</c> on backends
/// that push it down; <see cref="Observable.Scan{TSource, TAccumulate}(IObservable{TSource}, TAccumulate, Func{TAccumulate, TSource, TAccumulate})"/> over the change stream
/// maintains a path-keyed set so deletions of the current top fall back to
/// the next-deepest ancestor without re-querying.
///
/// <para><b>Backend behaviour</b>:
/// <list type="bullet">
///   <item><b>Postgres</b> — one indexed lookup per partition, server-side
///     sort + limit.</item>
///   <item><b>InMemory / FileSystem</b> — exact-path probes via
///     <c>StorageAdapterMeshQueryProvider</c>. FileSystem reads honour the
///     directory + <c>index.md</c> convention.</item>
///   <item><b>Static providers</b> (built-in roles, agents,
///     <c>AddMeshNodes</c> seed) — in-memory <c>StartsWith</c> filter.</item>
/// </list></para>
///
/// <para><b>Positive-only value cache per path.</b> Resolution runs per routed
/// message (<c>RoutingServiceBase.RouteMessage</c>) and per Blazor navigation, so
/// re-querying on every call was the dominant slide-switch latency.
/// <see cref="ResolveSegments"/> memoizes each SUCCESSFUL resolution — the
/// resolved <see cref="AddressResolution"/> VALUE, keyed by the joined segment
/// path — in a <see cref="ConcurrentDictionary{TKey,TValue}"/>. A warm entry is
/// returned as <c>Observable.Return(value)</c>, which emits SYNCHRONOUSLY on
/// Subscribe (the contract the Blazor layer relies on to skip progress UI).</para>
///
/// <para><b>Cache the RESULT, not the in-flight query.</b> An earlier design cached
/// the <c>Replay(1).AutoConnect(0)</c> observable itself and self-evicted only on
/// <c>onNext(null)</c> / <c>onError</c>. That amplified a transient into a permanent
/// hang: if the in-flight resolution query never emitted its Initial snapshot (the
/// known subscribe-handshake "dropped Full" race, worse under bulk load), the cached
/// observable NEVER emitted, errored, or completed — so the self-eviction never fired
/// and every later resolution of that path replayed the same dead observable forever.
/// A one-call, self-healing race became a permanent per-path stall (repro:
/// <c>PathResolutionCacheTest.HungFirstQuery_DoesNotPoisonCache</c> — a downstream
/// <c>mesh.Query(...).Take(1)</c> with no timeout, e.g. AI Export's subtree snapshot,
/// then hangs its whole operation). Caching only the resolved VALUE fixes this at the
/// root: a hung / errored / null resolution stores NOTHING, so it can never be served
/// from cache — it can only ever stall its own caller, which recovers via that
/// caller's existing timeout/retry exactly as before the cache existed. Concurrent
/// first-callers for one path each run their own query (no in-flight sharing); that
/// is a negligible cost — misses for the same path are rare — bought back by never
/// being able to pin a non-terminating query.</para>
///
/// <para><b>Why positive-only</b>: a NULL resolution is NEVER stored. The previous
/// <c>Replay(1).RefCount()</c> cache here was removed because caching a null (the
/// query snapshot racing change-feed propagation right after CreateNode) pinned a
/// permanent 404 (repro: <c>PathResolutionCacheTest.NullResolution_IsNotCached</c>).
/// Only a non-null resolution is added to the map, so negatives are never served.</para>
///
/// <para>🚨 <b>Positive-only is not enough on its own</b> — two ways a non-null answer is
/// still a negative in disguise, both of which pinned a path PERMANENTLY:
/// <list type="bullet">
///   <item><b>A fill that lands after its own invalidation.</b> The query is a live
///     round-trip; a write during it invalidates a cache entry that does not exist yet, so
///     the sweep finds nothing and the pre-write answer is stored afterwards — and no later
///     event removes it. A resolution therefore CLAIMS its key before querying
///     (<see cref="_pendingFills"/>) and commits only if the claim survived.</item>
///   <item><b>A fabricated bare-partition-root placeholder.</b>
///     <see cref="SynthesizePartitionRoot"/> invents a NodeType-less node so a bare
///     partition path can still answer Ping. It is applied OUTSIDE the cache and never
///     stored; caching it silently binds the partition root's hub to the mesh default
///     configuration for the life of the process.</item>
/// </list>
/// Repros: <c>PathResolutionCachePoisonTest</c>.</para>
///
/// <para><b>Invalidation</b>: the constructor subscribes the optional
/// <see cref="IMeshChangeFeed"/> (the same post-commit broadcast
/// <c>MeshNodeStreamCache</c> uses). Any <see cref="MeshChangeEvent"/> with path
/// <c>P</c> — Created, Updated or Deleted — removes every entry whose key equals
/// <c>P</c> or starts with <c>P + "/"</c>: Created(P) can deepen resolutions of
/// P and its descendants; Deleted/Updated(P) invalidate anything resolving to or
/// under P. Iterating the whole dictionary per event is fine — events are rare
/// relative to resolutions. When no <see cref="IMeshChangeFeed"/> is registered
/// (minimal test fixtures) the service does not cache at all and behaves exactly
/// like the uncached implementation.</para>
/// </summary>
internal class PathResolutionService : IPathResolver, IDisposable
{
    private readonly IMessageHub _hub;
    private readonly IMeshQueryCore _queryCore;
    private readonly AccessService? _accessService;
    private readonly ILogger<PathResolutionService>? _logger;
    private readonly IReadOnlyList<IPartitionStorageProvider> _writablePartitionProviders;
    private readonly bool _hasWritablePartitionProvider;

    /// <summary>
    /// Positive-only value cache: joined segment path → the resolved
    /// <see cref="AddressResolution"/> (never null). Non-null ONLY when an
    /// <see cref="IMeshChangeFeed"/> is registered — without the invalidation
    /// signal, caching would serve stale routes forever, so the service then
    /// resolves uncached (exactly the pre-cache behaviour). Storing the VALUE (not
    /// the in-flight observable) means a hung / errored / null resolution caches
    /// nothing and so can never poison the path — see the class doc.
    /// </summary>
    private readonly ConcurrentDictionary<string, AddressResolution>? _resolutionCache;

    /// <summary>
    /// In-flight fill claims: joined segment path → the monotonic claim stamped when its
    /// resolution query started. <see cref="OnMeshChange"/> drops matching claims, so an
    /// answer computed from a PRE-change snapshot can no longer be committed to
    /// <see cref="_resolutionCache"/> after the invalidation swept an entry that did not
    /// exist yet. Bounded by the number of CONCURRENT resolutions (entries are removed in
    /// the resolution's <c>Finally</c>), never by the number of paths. Non-null exactly
    /// when <see cref="_resolutionCache"/> is.
    /// </summary>
    private readonly ConcurrentDictionary<string, long>? _pendingFills;

    /// <summary>Monotonic source for <see cref="_pendingFills"/> claims.</summary>
    private long _fillSequence;

    /// <summary>Change-feed invalidation subscription; disposed with the singleton.</summary>
    private readonly IDisposable? _changeFeedSubscription;

    public PathResolutionService(
        IMessageHub hub,
        IMeshQueryCore queryCore,
        IEnumerable<IPartitionStorageProvider> partitionProviders,
        ILogger<PathResolutionService>? logger = null)
    {
        _hub = hub;
        _queryCore = queryCore;
        _accessService = hub.ServiceProvider.GetService<AccessService>();
        _logger = logger;
        // Optional service (same pattern as MeshNodeStreamCache): minimal test
        // fixtures without the feed registration get NO cache — never a cache
        // without its invalidation signal.
        var changeFeed = hub.ServiceProvider.GetService<IMeshChangeFeed>();
        if (changeFeed is not null)
        {
            _resolutionCache = new ConcurrentDictionary<string, AddressResolution>(StringComparer.Ordinal);
            _pendingFills = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
            _changeFeedSubscription = changeFeed.Subscribe(OnMeshChange);
        }
        // Gates the partition-root MeshNode synthesis below — we only fall back
        // to a placeholder when at least one writable provider could plausibly
        // own the partition (otherwise we'd activate grains for genuinely
        // bogus paths). In tests + monolith this is typically the wildcard
        // InMemoryPartitionStorageProvider; in prod it's the Postgres per-user
        // schema provider. Read-only seed providers (EmbeddedResource,
        // StaticNode) don't count — they can't accept new partitions, and they
        // answer PartitionExists with the indeterminate default (null), which
        // would dilute the confirmed-absent vote below. Keep only the writable
        // set so the existence probe reflects the providers that could own it.
        _writablePartitionProviders = partitionProviders.Where(p => !p.IsReadOnly).ToList();
        _hasWritablePartitionProvider = _writablePartitionProviders.Count > 0;
    }

    public IObservable<AddressResolution?> ResolvePath(string path)
        => Resolve(path, forNavigation: false);

    public IObservable<AddressResolution?> ResolveNavigationPath(string path)
        => Resolve(path, forNavigation: true);

    /// <summary>
    /// Shared resolution core. <paramref name="forNavigation"/> gates the legacy
    /// <c>/User/{id}</c> home rewrite: it fires ONLY for GUI navigation
    /// (<see cref="ResolveNavigationPath"/>), never for the shared
    /// <see cref="ResolvePath"/> that message routing (<c>RoutingServiceBase.RouteMessage</c>)
    /// and node reads (<c>GetMeshNodeStream</c>) go through. A genuine read/route of
    /// <c>User/{id}</c> must stay UNMODIFIED — it resolves to the bare <c>User</c> catalog
    /// node with a non-empty remainder (→ NotFound), which is what preserves the
    /// "no legacy User mirror" onboarding invariant
    /// (<c>UserOnboardingServiceTests.CreateUser_WritesPartitionRootOnly_NoUserMirror</c>).
    /// </summary>
    private IObservable<AddressResolution?> Resolve(string path, bool forNavigation)
    {
        if (string.IsNullOrEmpty(path))
            return Observable.Return<AddressResolution?>(null);

        var normalized = path.TrimStart('/');
        if (string.IsNullOrEmpty(normalized))
            return Observable.Return<AddressResolution?>(null);

        var resolved = ResolveSegments(normalized.Split('/'));
        if (forNavigation)
            resolved = resolved.SelectMany(RewriteLegacyUserHome);
        return resolved.DistinctUntilChanged(AddressResolutionEquality.Instance);
    }

    /// <summary>
    /// Rewrites the LEGACY <c>/User/{id}[/area]</c> URL shape onto the user's own
    /// ROOT partition. Pre-v10 user content lived under a shared <c>User/</c>
    /// namespace; post-v10 every user OWNS a root partition at <c>{id}</c> (see
    /// <see cref="UserNodeType.CreateMeshNode"/> — <c>RestrictedToNamespaces=[""]</c>,
    /// <c>OwnsPartition</c>). The built-in <c>User</c> NodeType node still exists at
    /// path <c>"User"</c>, so a legacy <c>/User/{id}</c> URL prefix-matches THAT catalog
    /// node (Prefix="User", Remainder="{id}[/area]") — nothing deeper under <c>User/</c>
    /// exists. Consumed as-is that yields hub=<c>User</c>, area=<c>{id}</c> ("No renderer
    /// is registered for area '{id}' on hub 'User'"). Strip the <c>User/</c> prefix and
    /// re-resolve against the user's own partition so the home area renders on the right
    /// hub. Bare <c>{id}</c> is the canonical form and already resolves — this makes the
    /// legacy prefixed URL (still emitted by the portal) and any stale bookmark match it.
    /// <para><b>NAVIGATION ONLY.</b> Called exclusively from <see cref="ResolveNavigationPath"/>
    /// (the GUI URL→area path), never from the shared <see cref="ResolvePath"/> that
    /// message routing + node reads use — so a read/route of <c>User/{id}</c> sees the
    /// UNMODIFIED resolution. The re-resolution below runs through the raw
    /// <see cref="ResolveSegments"/> (NOT <see cref="ResolveNavigationPath"/>), so the
    /// rewrite is applied at most once and cannot loop.</para>
    /// <para>Only fires when the SOLE match is the bare <c>User</c> catalog node: a real
    /// node under <c>User/</c> (transitional data, or a NodeType child) matches deeper,
    /// so Prefix != "User" and this is a no-op — that data still resolves by its own path.</para>
    /// </summary>
    private IObservable<AddressResolution?> RewriteLegacyUserHome(AddressResolution? resolution)
        => resolution is not null
            && string.Equals(resolution.Prefix, UserNodeType.NodeType, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(resolution.Remainder)
            ? ResolveSegments(resolution.Remainder.Split('/'))
            : Observable.Return(resolution);

    /// <summary>
    /// Cache-fronted resolution. A warm entry returns the cached
    /// <see cref="AddressResolution"/> VALUE via <c>Observable.Return</c> (emits
    /// synchronously on Subscribe — the Blazor skip-progress contract). A miss runs
    /// <see cref="ResolveSegmentsCore"/> and stores ONLY a non-null result, so a
    /// null / errored / never-emitting query caches nothing and can never poison the
    /// path (see the class doc). Without a registered <see cref="IMeshChangeFeed"/>
    /// there is no cache and every call queries.
    ///
    /// <para>🚨 The <see cref="SynthesizePartitionRoot"/> fallback is appended HERE, past
    /// the cache write, so a fabricated bare-partition-root placeholder is never stored —
    /// see that method for why pinning it is a permanent, silent mis-binding of the
    /// partition root's hub.</para>
    /// </summary>
    private IObservable<AddressResolution?> ResolveSegments(string[] segments)
    {
        if (_resolutionCache is null || _pendingFills is null)
            return ResolveSegmentsCore(segments)
                .SelectMany(resolution => resolution is not null
                    ? Observable.Return<AddressResolution?>(resolution)
                    : SynthesizePartitionRoot(segments));

        var key = string.Join("/", segments);
        if (_resolutionCache.TryGetValue(key, out var cached))
            return Observable.Return<AddressResolution?>(cached);

        // 🚨 CLAIM the fill BEFORE the query runs. The query is a live round-trip that
        // can take milliseconds-to-seconds under load; a write landing WHILE it is in
        // flight invalidates an entry that does not exist yet, so the sweep in
        // OnMeshChange finds nothing and the answer computed from the PRE-write snapshot
        // is added AFTERWARDS — and nothing ever removes it again, because the write
        // that would have invalidated it has already happened. That fill-after-invalidate
        // race is how a freshly-created node answers "No node found" (or resolves to its
        // ancestor with a remainder) for the rest of the process.
        // Repro: PathResolutionCachePoisonTest.InvalidationDuringInFlightQuery_DoesNotPinThePreChangeAnswer.
        var claim = Interlocked.Increment(ref _fillSequence);
        _pendingFills[key] = claim;
        return ResolveSegmentsCore(segments)
            .Do(resolution =>
            {
                // Positive-only, value-cache: store ONLY a resolved result. A null
                // (absent path) is never cached — a concurrent CreateNode may still
                // be propagating — and a query that never emits or errors stores
                // nothing at all, so it can only stall its own caller, never the
                // whole path. The change feed invalidates positive entries on write.
                if (resolution is null)
                    return;
                if (!ClaimStillHeld(key, claim))
                    return;
                _resolutionCache.TryAdd(key, resolution);
                // Re-check AFTER the add: an invalidation that ran between the check
                // above and the add would have swept an empty slot. Undoing here makes
                // the pair safe in BOTH interleavings — worst case the entry is dropped
                // twice and the next resolution re-queries.
                if (!ClaimStillHeld(key, claim))
                    _resolutionCache.TryRemove(key, out _);
            })
            .Finally(() => _pendingFills.TryRemove(new KeyValuePair<string, long>(key, claim)))
            .SelectMany(resolution => resolution is not null
                ? Observable.Return<AddressResolution?>(resolution)
                : SynthesizePartitionRoot(segments));
    }

    /// <summary>
    /// True while this resolution's fill claim is still the live one for <paramref name="key"/>:
    /// no invalidation for that key has run since the query started
    /// (<see cref="OnMeshChange"/> drops matching claims).
    /// </summary>
    private bool ClaimStillHeld(string key, long claim) =>
        _pendingFills is not null
        && _pendingFills.TryGetValue(key, out var current)
        && current == claim;

    /// <summary>
    /// Change-feed invalidation: any Created/Updated/Deleted event with path
    /// <c>P</c> removes every cached entry resolving to or under <c>P</c>
    /// (<c>key == P</c> or <c>key.StartsWith(P + "/")</c>). Created(P) can DEEPEN
    /// the resolution of P and of every descendant path (they previously resolved
    /// to a shallower ancestor); Deleted/Updated(P) staleness anything that
    /// resolved to or through P. Runs synchronously on the publisher's thread —
    /// pure dictionary ops, no I/O, no hub post. Full-dictionary iteration is
    /// deliberate: change events are rare relative to resolutions.
    ///
    /// <para>The SAME sweep is applied to the IN-FLIGHT fill claims: a resolution whose
    /// query started before this event computed its answer from a pre-change snapshot,
    /// so dropping its claim is what stops that answer from being cached once it lands.
    /// The pending map is bounded by the number of concurrent resolutions, not by the
    /// number of paths.</para>
    /// </summary>
    private void OnMeshChange(MeshChangeEvent change)
    {
        if (_resolutionCache is null || string.IsNullOrEmpty(change.Path))
            return;
        var path = change.Path;
        var childPrefix = path + "/";
        foreach (var key in _resolutionCache.Keys)
        {
            if (Affected(key))
                _resolutionCache.TryRemove(key, out _);
        }
        if (_pendingFills is null)
            return;
        foreach (var key in _pendingFills.Keys)
        {
            if (Affected(key))
                _pendingFills.TryRemove(key, out _);
        }

        bool Affected(string key) =>
            string.Equals(key, path, StringComparison.Ordinal)
            || key.StartsWith(childPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Disposes the change-feed subscription. The service is a mesh singleton
    /// (see <c>PersistenceExtensions.AddMeshCatalog</c>), so the DI container
    /// disposes it with the mesh.
    /// </summary>
    public void Dispose() => _changeFeedSubscription?.Dispose();

    /// <summary>
    /// The uncached resolution query (the body <see cref="ResolveSegments"/>
    /// memoizes). One live prefix query, first emission only — see the inline
    /// notes below.
    /// </summary>
    private IObservable<AddressResolution?> ResolveSegmentsCore(string[] segments)
    {
        // Quote each prefix so segments containing SPACES survive the query parser.
        // Unquoted values terminate at the first whitespace (the parser's "space = AND"
        // rule), which silently truncated paths like `AgenticPension/Data Analytics/…`
        // to `AgenticPension/Data` → "No matching address found". Quoting + the parser's
        // quoted `|`-alternation (QueryParser.ParseFieldValue) keeps the full path intact.
        var pathList = string.Join("|", Enumerable.Range(1, segments.Length)
            .Select(depth => "\"" + string.Join("/", segments.Take(depth)) + "\"")
            .Reverse());
        // 🚨 Path resolution MUST bypass access control. Mapping a URL path to
        // an address is ROUTING, not data access. If the user lacks Read on the
        // target, they should see "Access denied" at content-load time (the
        // owning hub's RLS handles that), NOT "Page not found" (which suggests
        // the URL is wrong). Prod symptom 2026-05-21:
        //   /Systemorph/_Thread/add-markus-kleiner-as-admin-to-systemorp-c578
        // returns NotFound because the PG cross-schema query applies
        // BuildPerSchemaAccessClause; the user's portal hub posts the inbound
        // request with accessContext=(null) (Blazor → Orleans flow loses
        // identity); the access clause falls through to "user IN ('Anonymous',
        // 'Public') AND partition='systemorph'" — no row, query returns empty,
        // resolver emits NotFound.
        //
        // Bypass requires BOTH: UserId=System on the request AND an active
        // ImpersonateAsSystem() scope on the AsyncLocal Context. The PG
        // provider's GetEffectiveUserId checks both (defense-in-depth) —
        // setting UserId alone wouldn't be enough to bypass, so a malicious
        // caller can't construct a "system" query without also having
        // AccessService access. PathResolutionService is framework-internal,
        // so the trust boundary holds.
        var request = MeshQueryRequest.FromQuery($"path:{pathList}") with
        {
            UserId = WellKnownUsers.System,
        };

        // Observable.Using ensures the ImpersonateAsSystem scope is opened on
        // Subscribe AND disposed when the inner observable completes — keeping
        // the AsyncLocal Context = system-security alive for the lifetime of
        // the query (the PG provider may capture context lazily on its first
        // emission, well past the chaining call).
        // Routing is a SIMPLE PATH LOOKUP — never a synced subscription.
        // We take only the FIRST emission (the Initial snapshot of which path
        // prefixes exist) and dispose. No fan-out, no ongoing watching, no
        // change deltas. If the resolver later needs to track new nodes, the
        // caller re-asks; the cache that wraps this method invalidates on
        // CreateNode/DeleteNode events.
        return Observable.Using(
            () => _accessService?.ImpersonateAsSystem() ?? System.Reactive.Disposables.Disposable.Empty,
            _ => _queryCore.Query<MeshNode>(request, _hub.JsonSerializerOptions))
            .Where(change => change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1)
            .Select<QueryResultChange<MeshNode>, AddressResolution?>(change =>
            {
                var best = change.Items.OrderByDescending(n => n.Path.Length).FirstOrDefault();
                if (best is null) return null;
                var matchedSegments = best.Path.Split('/').Length;
                return BuildResolution(best.Path, segments, matchedSegments, matchedNode: best);
            })
            // Query returns Observable.Empty when no provider matches.
            // Without DefaultIfEmpty the chain dies silently on completion and
            // RoutingServiceBase's .Take(1) waits forever. The null it emits is what
            // ResolveSegments turns into the bare-partition-root synthesis — which
            // runs AFTER this (and OUTSIDE the cache; see SynthesizePartitionRoot),
            // because when Query emits nothing the Select above never runs at all and
            // the only emission is this default.
            .DefaultIfEmpty();
    }

    /// <summary>
    /// The bare-partition-root fallback: when a SINGLE-segment path matches no node but
    /// there is at least one writable partition provider that could own it, fabricate a
    /// placeholder <see cref="MeshNode"/> so <c>MessageHubGrain.OnActivateAsync</c> has
    /// something to bind to. The grain then activates against
    /// <c>DefaultNodeHubConfiguration</c>; <c>PingRequest</c> and other default-hub
    /// operations route normally. Without it the routing grain emits NotFound and the
    /// user's home page hangs the full Orleans response budget — prod symptom: /rbuergi
    /// start screen blank, 30s "Response did not arrive on time".
    /// Repro: <c>PartitionRootActivationTest.BarePartitionPath_NoMeshNode_RespondsToPing</c>.
    ///
    /// <para>🚨 Synthesize a placeholder root ONLY for a partition that actually exists.
    /// A single-segment path that matches no node AND no provisioned partition — a
    /// mistyped/garbage URL like <c>markdown</c>, <c>code</c>, <c>search?q=…</c>,
    /// <c>login?returnurl=…</c> — must resolve to NotFound (a clean 404), NOT a synthetic
    /// root. The synthetic activated a grain that queried the never-provisioned
    /// <c>&lt;segment&gt;.mesh_nodes</c> schema → Npgsql 42P01, and the bogus segment
    /// leaked into a junk partition schema (prod log storm). Existence is a global OR
    /// across the WRITABLE providers: synthesize unless a provider that could own it
    /// definitively says it is absent (some <c>false</c>, none <c>true</c>).
    /// <c>true</c>/<c>null</c> (indeterminate, InMemory test, probe hiccup) still
    /// synthesize, so the real-partition home-page fast path (<c>/rbuergi</c>) is never
    /// regressed.</para>
    ///
    /// <para>🚨 <b>Never cached.</b> This is a FABRICATION, not an observed fact — the
    /// node carries no NodeType and no Content. Pinning it in the resolution cache is the
    /// same defect as pinning a <c>null</c> (a permanent 404), only harder to see: every
    /// later activation of that partition root then routes on a NodeType-less node, so
    /// <c>NodeTypeEnrichmentHelpers.EnrichWithNodeType</c> takes its silent
    /// <c>string.IsNullOrEmpty(nodeType)</c> branch and binds the mesh
    /// <c>DefaultNodeHubConfiguration</c> — the root serves ONLY the framework's default
    /// layout areas and none of its real NodeType's, with no log line and no self-heal,
    /// for the life of the process. That was the plugin gate's <c>Store/Catalog</c> RED
    /// ("No renderer is registered for area <c>Tests</c> on hub <c>Store</c>", listing
    /// exactly the framework defaults): the install provisions the <c>Store</c> partition
    /// BEFORE writing its root, so any routed touch inside that window fabricated the
    /// placeholder, and the fabrication outlived every later write.
    /// Repro: <c>PathResolutionCachePoisonTest.SynthesizedPartitionRoot_IsNotCached</c>.</para>
    ///
    /// <para>🚨 NOT cached is not the same as NOT pinned, and the difference is still open.
    /// The identical symptom recurred on 2026-08-10 (gate run 31361446933) on a commit that
    /// already carried the no-cache fix, listing exactly this default set again. Whoever routes
    /// on a synthesized resolution ACTIVATES a hub, and that hub is pinned by address in
    /// <c>Mesh.GetHostedHub</c> — an instance hub never re-reads its node's NodeType, so it keeps
    /// serving the default configuration for the life of the process even though every later
    /// RESOLUTION is now correct. The fabrication is no longer the thing that outlives the write;
    /// the HUB it activated is. So when this symptom reappears, check THREE things: is the
    /// resolution for the bare root path serving a real node, was the hub recycled after the root
    /// was retyped (<c>PackageInstaller</c> posts a <c>DisposeRequest</c> — fire-and-forget, and
    /// only when the placeholder dance ran), and is the live hub the one that was activated inside
    /// the provisioning window? Issue #1077 carries the analysis; the plugin gate now prints WHICH
    /// node hosted the failing area, which is what makes that third question answerable.</para>
    /// </summary>
    private IObservable<AddressResolution?> SynthesizePartitionRoot(string[] segments)
    {
        if (segments.Length != 1
            || string.IsNullOrEmpty(segments[0])
            || !_hasWritablePartitionProvider)
            return Observable.Return<AddressResolution?>(null);
        var partitionPath = segments[0];
        return PartitionConfirmedAbsent(partitionPath)
            .Select<bool, AddressResolution?>(confirmedAbsent =>
            {
                if (confirmedAbsent)
                    return null;
                // Greppable, because the consequence is invisible otherwise: whoever
                // routes on this resolution activates the partition root against the
                // mesh DEFAULT hub configuration (the node has no NodeType to bind).
                _logger?.LogDebug(
                    "PathResolution: no node at bare partition path '{Partition}' — synthesizing a "
                    + "placeholder root; its hub activates on the default configuration until the "
                    + "real root node is visible",
                    partitionPath);
                return BuildResolution(partitionPath, segments, matchedSegments: 1,
                    matchedNode: new MeshNode(partitionPath)
                    {
                        Name = partitionPath,
                        State = MeshNodeState.Active
                    });
            });
    }

    /// <summary>
    /// Reactive existence vote over the WRITABLE partition providers (the same set
    /// that gates synthesis). Mirrors <c>PartitionWriteGuardValidator</c>'s
    /// global-OR semantics but answers the synthesis question: a partition is
    /// CONFIRMED ABSENT only when at least one writable provider says <c>false</c>
    /// (it knows its store and the partition is not there) AND none says
    /// <c>true</c>. Indeterminate probes (<c>null</c>: a provider that can't answer,
    /// a 5s timeout, or an errored probe) never confirm absence — they fail OPEN to
    /// synthesis, so a probe hiccup can never turn a real partition's root into a 404.
    /// </summary>
    private IObservable<bool> PartitionConfirmedAbsent(string partition)
    {
        if (_writablePartitionProviders.Count == 0)
            return Observable.Return(false);

        var probes = _writablePartitionProviders
            .Select(p => p.PartitionExists(partition)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(5))
                .Catch<bool?, Exception>(ex =>
                {
                    _logger?.LogDebug(ex,
                        "PathResolution: partition existence probe for '{Partition}' via {Provider} failed; treating as indeterminate",
                        partition, p.Name);
                    return Observable.Return<bool?>(null);
                }))
            .ToList();

        return Observable.CombineLatest(probes)
            .Take(1)
            .Select(results => results.Any(r => r == false) && !results.Any(r => r == true));
    }

    private static AddressResolution BuildResolution(
        string matchedPath, string[] requestedSegments, int matchedSegments, MeshNode? matchedNode)
    {
        var remainder = matchedSegments < requestedSegments.Length
            ? string.Join("/", requestedSegments.Skip(matchedSegments))
            : null;
        return new AddressResolution(matchedPath, remainder, matchedNode);
    }

    /// <summary>
    /// Resolution-equality for <see cref="Observable.DistinctUntilChanged{TSource, TKey}(IObservable{TSource}, Func{TSource, TKey}, IEqualityComparer{TKey})"/>.
    /// Path-shape (Prefix + Remainder) is the cache key; the Node is metadata
    /// that can change without altering the route.
    /// </summary>
    private sealed class AddressResolutionEquality : IEqualityComparer<AddressResolution?>
    {
        public static readonly AddressResolutionEquality Instance = new();
        public bool Equals(AddressResolution? x, AddressResolution? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return string.Equals(x.Prefix, y.Prefix, StringComparison.Ordinal)
                && string.Equals(x.Remainder ?? "", y.Remainder ?? "", StringComparison.Ordinal);
        }
        public int GetHashCode(AddressResolution? obj) =>
            obj is null ? 0 : HashCode.Combine(obj.Prefix, obj.Remainder ?? "");
    }
}
