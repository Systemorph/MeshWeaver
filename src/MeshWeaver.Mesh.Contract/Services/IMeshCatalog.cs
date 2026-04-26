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
    /// Streaming query for autocomplete and node discovery. <see cref="IAsyncEnumerable{T}"/>
    /// is the natural shape for sets — not <c>Task&lt;List&lt;T&gt;&gt;</c>.
    /// </summary>
    IAsyncEnumerable<MeshNode> QueryAsync(string? parentPath, string? query = null, int? maxResults = null, CancellationToken ct = default);
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
