using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Service for managing NodeType data with caching and compilation.
/// </summary>
public interface INodeTypeService
{
    /// <summary>
    /// Gets the NodeTypeConfiguration for fast hub configuration lookup.
    /// In cached state, returns immediately without remote calls.
    /// </summary>
    NodeTypeConfiguration? GetCachedConfiguration(string nodeTypePath);

    /// <summary>
    /// Enriches a MeshNode with its NodeType's HubConfiguration (async, with compilation).
    /// Triggers compilation if needed and waits for it to complete.
    /// </summary>
    Task<MeshNode> EnrichWithNodeTypeAsync(MeshNode node, CancellationToken ct = default);

    /// <summary>
    /// Gets the node types that can be created as children of the specified node.
    /// </summary>
    IAsyncEnumerable<CreatableTypeInfo> GetCreatableTypesAsync(string nodePath, CancellationToken ct = default);

    /// <summary>
    /// Gets the cached HubConfiguration function for a node type.
    /// Applies the DefaultNodeHubConfiguration from MeshConfiguration if available.
    /// </summary>
    Func<MessageHubConfiguration, MessageHubConfiguration>? GetCachedHubConfiguration(string nodeTypePath);

    /// <summary>
    /// Gets the access rule extracted from the hub configuration for a node type.
    /// Returns null if no access rules are defined in the hub config.
    /// </summary>
    INodeTypeAccessRule? GetAccessRule(string nodeTypePath) => null;
}
