namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Public interface for resolving URL paths to mesh node addresses.
/// Used by Blazor navigation to map browser URLs to hub addresses.
///
/// <para>
/// 100% reactive — emits <see cref="AddressResolution"/> via <see cref="IObservable{T}"/>.
/// Compose with <c>.Select</c> / <c>.SelectMany</c> / <c>.Subscribe</c>. NEVER bridge to
/// <c>Task</c> (that's a 100% deadlock surface; see
/// <c>Doc/Architecture/AsynchronousCalls.md</c>).
/// </para>
///
/// <para>Implemented by <c>PathResolutionService</c>, which owns a POSITIVE-ONLY
/// per-path VALUE cache (the resolved <see cref="AddressResolution"/>, invalidated
/// by the mesh change feed): a warm entry replays synchronously via
/// <c>Observable.Return</c>, and only a non-null result is stored — a null
/// (not-found), errored, or never-emitting resolution caches nothing, so a query
/// snapshot racing change-feed propagation right after CreateNode can't pin a stale
/// 404 and a dropped-Initial query can't poison the path (it stalls only its own
/// caller). The matched <see cref="MeshNode"/> rides on
/// <see cref="AddressResolution.Node"/> so the routing layer doesn't need a second
/// <c>path:X</c> query.</para>
/// </summary>
public interface IPathResolver
{
    /// <summary>
    /// Resolves a full URL path to an address using score-based matching.
    /// Emits the best matching node's address and the remaining path segments,
    /// or <c>null</c> if no match is found.
    ///
    /// <para>This is the SHARED, literal resolution used by message routing
    /// (<c>RoutingServiceBase.RouteMessage</c>) and single-node reads. It applies NO
    /// URL-shape rewrites — a legacy <c>User/{id}</c> path resolves to the bare
    /// <c>User</c> catalog node with a non-empty remainder (i.e. NotFound for a route),
    /// which is exactly what preserves read/route invariants. GUI navigation that wants
    /// the legacy-home rewrite must call <see cref="ResolveNavigationPath"/>.</para>
    /// </summary>
    IObservable<AddressResolution?> ResolvePath(string path);

    /// <summary>
    /// ROUTE-SHAPE resolution: like <see cref="ResolvePath"/>, but the caller only consumes
    /// <see cref="AddressResolution.Prefix"/> / <see cref="AddressResolution.Remainder"/> — never
    /// <see cref="AddressResolution.Node"/>. That contract lets the implementation serve a cached
    /// entry whose NODE snapshot is stale: an in-place <c>Updated</c> write cannot change any
    /// path's resolution SHAPE (the node existed at that path before the write and still does),
    /// so only <c>Created</c>/<c>Deleted</c> invalidate this view.
    ///
    /// <para>🚨 This is what keeps the silo router off the storage backend for hot-WRITTEN paths
    /// (issue #1172): a compile activity receives one routed <c>PatchDataRequest</c> per appended
    /// log line, and each write used to invalidate the very cache entry the NEXT routed message
    /// needed — so during the boot NodeType bake every route to <c>…/_Activity/compile-*</c> paid
    /// a fresh storage query while holding a RoutingGrain in-flight slot. 64+ slots filled, the
    /// pre-warmer's own stream waits timed out behind the saturated router, and the bake inflated
    /// ~10× — the routing/compile feedback loop. Callers that read <see cref="AddressResolution.Node"/>
    /// (grain activation, monolith hub creation, navigation) MUST stay on
    /// <see cref="ResolvePath"/>, which re-queries when the node snapshot is stale.</para>
    /// </summary>
    IObservable<AddressResolution?> ResolveRoute(string path) => ResolvePath(path);

    /// <summary>
    /// Navigation-only resolution: <see cref="ResolvePath"/> plus the URL-shape rewrites that
    /// belong to browsing and to nothing else. Used exclusively by the GUI/agent URL→area consumers
    /// (Blazor <c>NavigationService</c>, area pages, <c>MeshOperations.NavigateTo</c>). Message
    /// routing and node reads must NOT use this — they use <see cref="ResolvePath"/>, which stays
    /// literal so a read can never silently answer with a DIFFERENT node than the caller named.
    ///
    /// <para>Two rewrites, in order:</para>
    /// <list type="number">
    ///   <item>the legacy <c>/User/{id}[/area]</c> home rewrite (strips the obsolete <c>User/</c>
    ///     prefix and re-resolves against the user's own root partition);</item>
    ///   <item><b>moved-node redirects</b> — a <c>Redirect</c> node (content
    ///     <see cref="NodeRedirect"/>) left at a retired path sends the whole subtree under it to
    ///     the new location, chained up to <see cref="NodeRedirectRules.MaxHops"/> hops with cycle
    ///     detection. A followed resolution carries <see cref="AddressResolution.RedirectedFrom"/>;
    ///     a chain that loops, overruns the cap or names a missing target stops at the redirect node
    ///     and reports <see cref="AddressResolution.RedirectDiagnostic"/>.</item>
    /// </list>
    ///
    /// <para>🚨 A redirect rewrites a PATH — it confers no access. The rewritten target is gated and
    /// read exactly as if the viewer had typed the target URL, so a redirect can never widen what
    /// someone can see. See <c>Doc/Architecture/NodeRedirects.md</c>.</para>
    /// </summary>
    IObservable<AddressResolution?> ResolveNavigationPath(string path);
}

/// <summary>
/// Result of path resolution. <see cref="Prefix"/> is the matched node's path,
/// <see cref="Remainder"/> is anything that wasn't matched, and
/// <see cref="Node"/> is the matched <see cref="MeshNode"/> itself when the
/// resolution came from persistence / a static provider / a configuration
/// node — <c>null</c> for partition-root virtual matches (where no concrete
/// MeshNode exists at the bare partition path). Carrying the node lets the
/// routing layer share <see cref="IPathResolver"/>'s cached stream instead of
/// issuing a second <c>path:X</c> query.
/// </summary>
public record AddressResolution(
    string Prefix,
    string? Remainder,
    MeshNode? Node = null
)
{
    /// <summary>
    /// The path the caller ORIGINALLY asked for, when a <c>Redirect</c> declaration moved this
    /// resolution somewhere else — <c>null</c> on every normal resolution. Set only by
    /// <see cref="IPathResolver.ResolveNavigationPath"/> (the one surface that follows redirects;
    /// routing and node reads stay literal). The GUI uses it for two things: to rewrite the browser
    /// URL to the canonical target — so relative links inside the page resolve against the right
    /// path and the bookmark heals — and to TELL the viewer they were moved.
    /// </summary>
    public string? RedirectedFrom { get; init; }

    /// <summary>
    /// Why a redirect chain stopped short, when it did: a cycle, the hop cap, or a target that does
    /// not exist. <c>null</c> whenever no chain was walked or the chain arrived. The resolution
    /// itself then points at the last <c>Redirect</c> node, so the viewer lands on a page that names
    /// the intended destination instead of a dead end — and the reason is a value the GUI and the
    /// tests can read, not just a log line.
    /// </summary>
    public RedirectDiagnostic? RedirectDiagnostic { get; init; }
}

/// <summary>Information about storage configuration.</summary>
public record StorageInfo(
    string Id,
    string BaseDirectory,
    string AssemblyLocation,
    string AddressType);

/// <summary>Information needed to start a mesh node.</summary>
public record StartupInfo(MeshWeaver.Messaging.Address Address, string PackageName, string AssemblyLocation);
