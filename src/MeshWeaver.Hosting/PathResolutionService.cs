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
/// <para><b>No PathResolution-level cache.</b> <c>Query</c> is live;
/// memoizing here either races change-feed propagation (stale-NULL after
/// CreateNode, repro:
/// <c>AddressResolutionTest.ResolvePath_ExactMatch_ReturnsNullRemainder</c>)
/// or duplicates the provider-level caching that already exists. Each call
/// triggers a fresh query — bounded cost, single round-trip on Postgres.
/// No <c>MeshConfiguration.Nodes</c> scan, no <c>IStorageAdapter</c>
/// reach-through, no partition-provider enumeration.</para>
/// </summary>
internal class PathResolutionService : IPathResolver
{
    private readonly IMessageHub _hub;
    private readonly IMeshQueryCore _queryCore;
    private readonly AccessService? _accessService;
    private readonly ILogger<PathResolutionService>? _logger;
    private readonly IReadOnlyList<IPartitionStorageProvider> _writablePartitionProviders;
    private readonly bool _hasWritablePartitionProvider;

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
    {
        if (string.IsNullOrEmpty(path))
            return Observable.Return<AddressResolution?>(null);

        var normalized = path.TrimStart('/');
        if (string.IsNullOrEmpty(normalized))
            return Observable.Return<AddressResolution?>(null);

        return ResolveOnce(normalized)
            .DistinctUntilChanged(AddressResolutionEquality.Instance);
    }

    private IObservable<AddressResolution?> ResolveOnce(string path)
        => ResolveSegments(path.Split('/'))
            .SelectMany(RewriteLegacyUserHome);

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
    /// <para>Only fires when the SOLE match is the bare <c>User</c> catalog node: a real
    /// node under <c>User/</c> (transitional data, or a NodeType child) matches deeper,
    /// so Prefix != "User" and this is a no-op — that data still resolves by its own path.
    /// Applied once (to the top-level resolution, not to the re-resolution below), so it
    /// cannot loop.</para>
    /// </summary>
    private IObservable<AddressResolution?> RewriteLegacyUserHome(AddressResolution? resolution)
        => resolution is not null
            && string.Equals(resolution.Prefix, UserNodeType.NodeType, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(resolution.Remainder)
            ? ResolveSegments(resolution.Remainder.Split('/'))
            : Observable.Return(resolution);

    private IObservable<AddressResolution?> ResolveSegments(string[] segments)
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
            // RoutingServiceBase's .Take(1) waits forever.
            .DefaultIfEmpty()
            // Partition-root fallback runs AFTER DefaultIfEmpty so the empty-
            // upstream case (no match for the requested path or any ancestor)
            // gets a chance to synthesize. Doing this inside the upstream Select
            // wouldn't help: when Query emits nothing, Scan never emits,
            // Select never runs — the only emission is the null from
            // DefaultIfEmpty. The Select below intercepts that null.
            //
            // If the request is for a bare partition path (a single segment)
            // AND there's at least one writable partition provider that could
            // own it, synthesize a placeholder MeshNode so MessageHubGrain.
            // OnActivateAsync sees something to bind to. The grain then
            // activates against DefaultNodeHubConfiguration; PingRequest and
            // other default-hub operations route normally. Without this, the
            // routing-grain emits NotFound and the user's home page hangs the
            // full Orleans response budget — prod symptom: /rbuergi start
            // screen blank, 30s "Response did not arrive on time".
            // Repro: PartitionRootActivationTest.BarePartitionPath_NoMeshNode_RespondsToPing.
            .SelectMany(resolution =>
            {
                if (resolution is not null)
                    return Observable.Return<AddressResolution?>(resolution);
                if (segments.Length != 1
                    || string.IsNullOrEmpty(segments[0])
                    || !_hasWritablePartitionProvider)
                    return Observable.Return<AddressResolution?>(null);
                var partitionPath = segments[0];
                // 🚨 Synthesize a placeholder root ONLY for a partition that actually
                // exists. A single-segment path that matches no node AND no provisioned
                // partition — a mistyped/garbage URL like `markdown`, `code`,
                // `search?q=…`, `login?returnurl=…` — must resolve to NotFound (a clean
                // 404), NOT a synthetic root. The synthetic activated a grain that queried
                // the never-provisioned `<segment>.mesh_nodes` schema → Npgsql 42P01, and
                // the bogus segment leaked into a junk partition schema (atioz log storm).
                // Existence is a global OR across the WRITABLE providers: synthesize unless
                // a provider that could own it definitively says it is absent (some `false`,
                // none `true`). `true`/`null` (indeterminate, InMemory test, probe hiccup)
                // still synthesize, so the real-partition home-page fast path (`/rbuergi`)
                // is never regressed.
                return PartitionConfirmedAbsent(partitionPath)
                    .Select<bool, AddressResolution?>(confirmedAbsent => confirmedAbsent
                        ? null
                        : BuildResolution(partitionPath, segments, matchedSegments: 1,
                            matchedNode: new MeshNode(partitionPath)
                            {
                                Name = partitionPath,
                                State = MeshNodeState.Active
                            }));
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
