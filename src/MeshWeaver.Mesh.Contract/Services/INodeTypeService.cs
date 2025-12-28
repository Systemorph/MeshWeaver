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
    /// Enriches a MeshNode with its NodeType's HubConfiguration.
    /// Uses cached configuration if available, otherwise triggers async compilation (non-blocking).
    /// </summary>
    /// <param name="node">The MeshNode to enrich</param>
    /// <returns>The MeshNode with HubConfiguration set if NodeType is configured</returns>
    MeshNode EnrichWithNodeType(MeshNode node);

    /// <summary>
    /// Gets the HubConfiguration observable for an address via remote stream subscription.
    /// Returns immediately without blocking - subscribe only when creating the hub.
    /// Encapsulates all caching, stream subscription, and compilation logic.
    /// </summary>
    /// <param name="address">The node address</param>
    /// <returns>Observable that emits the HubConfiguration function, or null if not found/compiled</returns>
    IObservable<Func<MessageHubConfiguration, MessageHubConfiguration>?> GetHubConfiguration(Address address);
}
