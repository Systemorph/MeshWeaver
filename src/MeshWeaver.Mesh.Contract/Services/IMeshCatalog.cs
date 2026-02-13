using MeshWeaver.Messaging;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeshWeaver.Orleans")]
namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Catalog service for managing mesh nodes and their configuration.
/// </summary>
public interface IMeshCatalog
{
    /// <summary>
    /// Gets the mesh configuration.
    /// </summary>
    MeshConfiguration Configuration { get; }

    /// <summary>
    /// Gets a mesh node by its address.
    /// </summary>
    /// <param name="address">The address of the node to retrieve.</param>
    /// <returns>The mesh node, or null if not found.</returns>
    Task<MeshNode?> GetNodeAsync(Address address);

    /// <summary>
    /// Creates a new node in the catalog with validation.
    /// The node is created in Transient state, validated, and then confirmed.
    /// </summary>
    /// <param name="node">The node to create</param>
    /// <param name="createdBy">The user or system creating the node</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The created node with State set to Confirmed</returns>
    /// <exception cref="InvalidOperationException">If node already exists or validation fails</exception>
    Task<MeshNode> CreateNodeAsync(MeshNode node, string? createdBy = null, CancellationToken ct = default);


    /// <summary>
    /// Deletes a node from the catalog.
    /// </summary>
    /// <param name="path">The path of the node to delete</param>
    /// <param name="recursive">If true, also delete all descendant nodes</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteNodeAsync(string path, bool recursive = false, CancellationToken ct = default);

    /// <summary>
    /// Resolves a full URL path to an address using score-based matching.
    /// Returns the best matching node's address and the remaining path segments.
    /// Score is the number of matching segments from the path start.
    /// </summary>
    Task<AddressResolution?> ResolvePathAsync(string path);

    /// <summary>
    /// Queries for child nodes under a parent path, filtered by query string.
    /// Used for autocomplete and node discovery.
    /// </summary>
    /// <param name="parentPath">Parent path to search under (null or empty for root level)</param>
    /// <param name="query">Optional search query to filter by name/description</param>
    /// <param name="maxResults">Optional maximum number of results to return</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Async enumerable of matching child nodes</returns>
    IAsyncEnumerable<MeshNode> QueryAsync(string? parentPath, string? query = null, int? maxResults = null, CancellationToken ct = default);

}

/// <summary>
/// Information about storage configuration.
/// </summary>
/// <param name="Id">The storage identifier.</param>
/// <param name="BaseDirectory">The base directory for storage.</param>
/// <param name="AssemblyLocation">The location of the assembly.</param>
/// <param name="AddressType">The type of address used.</param>
public record StorageInfo(
    string Id,
    string BaseDirectory,
    string AssemblyLocation,
    string AddressType);

/// <summary>
/// Information needed to start a mesh node.
/// </summary>
/// <param name="Address">The address of the node.</param>
/// <param name="PackageName">The package name.</param>
/// <param name="AssemblyLocation">The location of the assembly.</param>
public record StartupInfo(Address Address, string PackageName, string AssemblyLocation);

/// <summary>
/// Result of path resolution containing the matched prefix and remaining path.
/// </summary>
public record AddressResolution(
    string Prefix,
    string? Remainder
);
