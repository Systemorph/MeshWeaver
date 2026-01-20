using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Service for managing NodeType data with caching and stream subscriptions.
/// Caches Tasks to avoid deadlocks - uses ConcurrentDictionary.GetOrAdd pattern.
/// </summary>
/// <remarks>
/// The service:
/// 1. Subscribes to remote streams for NodeType hubs with empty DataReference
/// 2. Caches compiled assembly paths and NodeTypeConfigurations
/// 3. Invalidates cache when stream emits updates, triggering recompilation on next access
/// </remarks>
public interface INodeTypeService
{
    /// <summary>
    /// Gets the compiled assembly path for a node type.
    /// Returns cached path if valid, otherwise triggers compilation.
    /// </summary>
    /// <param name="nodeTypePath">The node type path (e.g., "Type/Person")</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Assembly path or null if compilation not possible</returns>
    Task<string?> GetAssemblyPathAsync(string nodeTypePath, CancellationToken ct = default);

    /// <summary>
    /// Gets the NodeTypeConfiguration for fast hub configuration lookup.
    /// In cached state, returns immediately without remote calls.
    /// </summary>
    /// <param name="nodeTypePath">The node type path</param>
    /// <returns>NodeTypeConfiguration if cached and compiled, null otherwise</returns>
    NodeTypeConfiguration? GetCachedConfiguration(string nodeTypePath);

    /// <summary>
    /// Invalidates the cache for a specific node type, triggering recompilation on next access.
    /// </summary>
    /// <param name="nodeTypePath">The node type path to invalidate</param>
    void InvalidateCache(string nodeTypePath);

    /// <summary>
    /// Enriches a MeshNode with its NodeType's HubConfiguration (synchronous, cached only).
    /// Uses cached configuration if available, otherwise returns node unchanged.
    /// For async compilation support, use <see cref="EnrichWithNodeTypeAsync"/>.
    /// </summary>
    /// <param name="node">The MeshNode to enrich</param>
    /// <returns>The MeshNode with HubConfiguration set if NodeType is already cached</returns>
    MeshNode EnrichWithNodeType(MeshNode node);

    /// <summary>
    /// Enriches a MeshNode with its NodeType's HubConfiguration (async, with compilation).
    /// Triggers compilation if needed and waits for it to complete.
    /// </summary>
    /// <param name="node">The MeshNode to enrich</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The MeshNode with HubConfiguration set if NodeType is configured</returns>
    Task<MeshNode> EnrichWithNodeTypeAsync(MeshNode node, CancellationToken ct = default);

    /// <summary>
    /// Gets the HubConfiguration for an address via remote stream subscription.
    /// Encapsulates all caching, stream subscription, and compilation logic.
    /// </summary>
    /// <param name="address">The node address</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The HubConfiguration function, or null if not found/compiled</returns>
    Task<Func<MessageHubConfiguration, MessageHubConfiguration>?> GetHubConfigurationAsync(Address address, CancellationToken ct = default);

    /// <summary>
    /// Gets the node types that can be created as children of the specified node.
    ///
    /// Algorithm:
    /// 1. Query by node.Path to find NodeTypes defined directly under this path
    ///    (types whose namespace matches the node's path)
    /// 2. If node has a NodeType, also query by node.NodeType to find types
    ///    that can be created in instances of this type
    /// 3. Add global types (Markdown, NodeType) if IncludeGlobalTypes is true
    /// </summary>
    /// <param name="nodePath">The path of the node where we want to create children</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Async enumerable of creatable type information, sorted by display order</returns>
    IAsyncEnumerable<CreatableTypeInfo> GetCreatableTypesAsync(string nodePath, CancellationToken ct = default);
}
