using MeshWeaver.Messaging;

// Infrastructure assemblies that need internal access to IMeshStorage, IStorageService, IMeshCatalog
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeshWeaver.Hosting")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeshWeaver.Graph")]

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Internal catalog service backing the routing layer. Stripped to the minimum
/// surface that the path resolver + autocomplete need. Application code MUST NOT
/// use this — the CQRS-correct primitives (<c>GetDataRequest + RegisterCallback</c>
/// for one-shot reads, <c>GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;</c> for live)
/// are the only sanctioned paths to MeshNode content. See
/// <c>Doc/Architecture/CqrsAndContentAccess.md</c>.
/// </summary>
internal interface IMeshCatalog : IPathResolver
{
    /// <summary>
    /// In-memory mesh-level configured nodes (from <c>AddMeshNodes</c>). Used by the
    /// routing layer to find statically configured node types without hitting persistence.
    /// </summary>
    MeshConfiguration Configuration { get; }

    /// <summary>
    /// Live observable of MeshNodes matching the catalog query. Emits the
    /// initial snapshot and every subsequent update. Subscribers compose with
    /// <c>Select</c>/<c>Subscribe</c> — no await, no <see cref="IAsyncEnumerable{T}"/>
    /// bridge.
    /// </summary>
    IObservable<IReadOnlyList<MeshNode>> Query(string? parentPath, string? query = null, int? maxResults = null);
}

/// <summary>
/// Information about storage configuration.
/// </summary>
public record StorageInfo(
    string Id,
    string BaseDirectory,
    string AssemblyLocation,
    string AddressType);

/// <summary>
/// Information needed to start a mesh node.
/// </summary>
public record StartupInfo(Address Address, string PackageName, string AssemblyLocation);

/// <summary>
/// Result of path resolution containing the matched prefix and remaining path.
/// </summary>
public record AddressResolution(
    string Prefix,
    string? Remainder
);
