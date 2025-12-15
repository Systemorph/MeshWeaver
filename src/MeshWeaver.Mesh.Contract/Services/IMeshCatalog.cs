using MeshWeaver.Messaging;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MeshWeaver.Orleans")]
namespace MeshWeaver.Mesh.Services;

public interface IMeshCatalog
{
    MeshConfiguration Configuration { get; }
    Task<MeshNode?> GetNodeAsync(Address address);

    Task UpdateAsync(MeshNode node);

    Task<StreamInfo> GetStreamInfoAsync(Address address);

    /// <summary>
    /// Global registry for unified path prefixes.
    /// Enables resolution of paths like "pricing:MS-2024" to target address and workspace reference.
    /// </summary>
    IUnifiedPathRegistry PathRegistry { get; }

    /// <summary>
    /// Resolves a full URL path to an address using score-based matching.
    /// Returns the best matching node's address and the remaining path segments.
    /// Score is the number of matching segments from the path start.
    /// </summary>
    AddressResolution? ResolvePath(string path);

    /// <summary>
    /// Gets the persistence service for graph operations.
    /// </summary>
    IPersistenceService Persistence { get; }

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

    /// <summary>
    /// Gets all available node type configurations.
    /// Returns information about each registered node type including its name, data type, and description.
    /// </summary>
    /// <returns>Collection of node type information</returns>
    IEnumerable<NodeTypeInfo> GetNodeTypes();

    /// <summary>
    /// Gets the node type configuration for a specific node type.
    /// </summary>
    /// <param name="nodeType">The node type identifier</param>
    /// <returns>The configuration or null if not found</returns>
    NodeTypeConfiguration? GetNodeTypeConfiguration(string nodeType);
}

/// <summary>
/// Information about a node type for display and schema generation.
/// </summary>
public record NodeTypeInfo(
    string NodeType,
    string? DisplayName,
    string? Description,
    string? IconName,
    string DataTypeName,
    int DisplayOrder
);



public record StreamInfo(
    StreamType Type,
    string Provider, 
    string Namespace);
public enum StreamType{Stream, Channel}
public record StorageInfo(
    string Id, 
    string BaseDirectory, 
    string AssemblyLocation, 
    string AddressType);


public record StartupInfo(Address Address, string PackageName, string AssemblyLocation);

/// <summary>
/// Result of path resolution containing the matched prefix and remaining path.
/// </summary>
public record AddressResolution(
    string Prefix,
    string? Remainder
);
