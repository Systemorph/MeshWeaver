using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Handler for a specific unified path prefix.
/// Parses paths after the prefix and returns the target address and workspace reference.
/// </summary>
public interface IUnifiedPathHandler
{
    /// <summary>
    /// Parse the path and return the target address and workspace reference.
    /// </summary>
    /// <param name="pathAfterPrefix">The path part after the prefix (e.g., "MS-2024" from "pricing:MS-2024")</param>
    /// <returns>Target address to route to and workspace reference to request</returns>
    (Address Address, WorkspaceReference Reference) Parse(string pathAfterPrefix);
}

/// <summary>
/// Global registry for unified path prefixes.
/// Registered at mesh level, available to all hubs.
/// </summary>
public interface IUnifiedPathRegistry
{
    /// <summary>
    /// Register a prefix handler.
    /// </summary>
    /// <param name="prefix">The prefix (e.g., "data", "area", "pricing")</param>
    /// <param name="handler">The handler that parses paths with this prefix</param>
    void Register(string prefix, IUnifiedPathHandler handler);

    /// <summary>
    /// Try to resolve a path to an address and workspace reference.
    /// </summary>
    /// <param name="path">The full path including prefix (e.g., "pricing:MS-2024")</param>
    /// <param name="targetAddress">The resolved target address</param>
    /// <param name="reference">The resolved workspace reference</param>
    /// <returns>True if the path was successfully resolved</returns>
    bool TryResolve(string path, out Address? targetAddress, out WorkspaceReference? reference);

    /// <summary>
    /// Get all registered prefixes.
    /// </summary>
    IEnumerable<string> Prefixes { get; }
}
