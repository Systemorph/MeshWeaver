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
    /// Navigation-only resolution: <see cref="ResolvePath"/> plus the legacy
    /// <c>/User/{id}[/area]</c> home rewrite (strips the obsolete <c>User/</c> prefix and
    /// re-resolves against the user's own root partition so the home area renders on the
    /// right hub). Used exclusively by the GUI URL→area consumers (Blazor
    /// <c>NavigationService</c> / area pages). Message routing and node reads must NOT use
    /// this — they use <see cref="ResolvePath"/> so <c>User/{id}</c> stays unmodified.
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
);

/// <summary>Information about storage configuration.</summary>
public record StorageInfo(
    string Id,
    string BaseDirectory,
    string AssemblyLocation,
    string AddressType);

/// <summary>Information needed to start a mesh node.</summary>
public record StartupInfo(MeshWeaver.Messaging.Address Address, string PackageName, string AssemblyLocation);
