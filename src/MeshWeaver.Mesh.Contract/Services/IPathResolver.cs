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
/// <para>Implemented by <c>PathResolutionService</c>, which owns a
/// <c>Replay(1).RefCount()</c> per path. Concurrent subscribers share the
/// cached resolution stream; the matched <see cref="MeshNode"/> rides on
/// <see cref="AddressResolution.Node"/> so the routing layer doesn't need a
/// second <c>path:X</c> query.</para>
/// </summary>
public interface IPathResolver
{
    /// <summary>
    /// Resolves a full URL path to an address using score-based matching.
    /// Emits the best matching node's address and the remaining path segments,
    /// or <c>null</c> if no match is found.
    /// </summary>
    IObservable<AddressResolution?> ResolvePath(string path);
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
