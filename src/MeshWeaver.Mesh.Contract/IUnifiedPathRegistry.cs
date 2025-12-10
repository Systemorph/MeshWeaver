using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Handler for a specific unified path keyword.
/// Parses paths and returns the target address and workspace reference.
/// Path format: addressType/addressId/keyword/remainingPath
/// </summary>
public interface IUnifiedPathHandler
{
    /// <summary>
    /// Parse the path components and return the target address and workspace reference.
    /// </summary>
    /// <param name="addressType">The address type (e.g., "host", "pricing")</param>
    /// <param name="addressId">The address ID (e.g., "1", "MS-2024")</param>
    /// <param name="remainingPath">The path after the keyword (e.g., "Collection/entityId")</param>
    /// <returns>Target address to route to and workspace reference to request</returns>
    (Address Address, WorkspaceReference Reference) Parse(string addressType, string addressId, string remainingPath);
}

/// <summary>
/// Global registry for unified path keywords.
/// Registered at mesh level, available to all hubs.
/// Path format: addressType/addressId[/keyword[/remainingPath]]
/// If no keyword is specified, defaults to "area".
/// </summary>
public interface IUnifiedPathRegistry
{
    /// <summary>
    /// Register a keyword handler.
    /// </summary>
    /// <param name="keyword">The keyword (e.g., "data", "area", "content")</param>
    /// <param name="handler">The handler that parses paths with this keyword</param>
    void Register(string keyword, IUnifiedPathHandler handler);

    /// <summary>
    /// Try to resolve a path to an address and workspace reference.
    /// </summary>
    /// <param name="path">The full path (e.g., "host/1/data/Collection")</param>
    /// <param name="targetAddress">The resolved target address</param>
    /// <param name="reference">The resolved workspace reference</param>
    /// <returns>True if the path was successfully resolved</returns>
    bool TryResolve(string path, out Address? targetAddress, out WorkspaceReference? reference);

    /// <summary>
    /// Get all registered keywords.
    /// </summary>
    IEnumerable<string> Keywords { get; }
}
