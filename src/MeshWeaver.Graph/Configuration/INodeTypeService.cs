using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service for managing NodeType definitions stored as MeshNodes with partition data.
///
/// NodeTypes are scoped hierarchically - for a node at path "a/b/c", applicable NodeTypes
/// are found by walking up the path: a/b/c/*, a/b/*, a/*, and root level (including _types/*).
///
/// NodeType nodes have NodeType = "NodeType" and store their DataModel, LayoutAreas, etc.
/// as separate files in their partition folder.
/// </summary>
public interface INodeTypeService
{
    /// <summary>
    /// Prefix for global/root-level node types.
    /// </summary>
    const string GlobalTypesPrefix = "type";

    /// <summary>
    /// Gets all NodeType nodes applicable to a given context path.
    /// Walks up the path hierarchy to find all inherited types.
    /// For path "a/b/c": searches a/b/c/*, a/b/*, a/*, root/*, and _types/*.
    /// </summary>
    /// <param name="contextPath">The path context to resolve types for</param>
    /// <returns>Async enumerable of applicable NodeType MeshNodes, from most local to most global</returns>
    IAsyncEnumerable<MeshNode> GetNodeTypeNodesAsync(string contextPath);

    /// <summary>
    /// Gets a specific NodeType node by type identifier, resolved for a context path.
    /// </summary>
    /// <param name="nodeType">The node type identifier (e.g., "story")</param>
    /// <param name="contextPath">The path context to resolve from</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The most local NodeType MeshNode matching the identifier, or null</returns>
    Task<MeshNode?> GetNodeTypeNodeAsync(string nodeType, string contextPath, CancellationToken ct = default);

    /// <summary>
    /// Gets the DataModel definition for a node type, resolved for a context path.
    /// </summary>
    /// <param name="nodeType">The node type identifier</param>
    /// <param name="contextPath">The path context to resolve from</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The DataModel or null if not found</returns>
    Task<DataModel?> GetDataModelAsync(string nodeType, string contextPath, CancellationToken ct = default);

    /// <summary>
    /// Gets all LayoutAreaConfig definitions for a node type, resolved for a context path.
    /// </summary>
    /// <param name="nodeType">The node type identifier</param>
    /// <param name="contextPath">The path context to resolve from</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of layout area configurations</returns>
    Task<IReadOnlyList<LayoutAreaConfig>> GetLayoutAreasAsync(string nodeType, string contextPath, CancellationToken ct = default);

    /// <summary>
    /// Gets the HubFeatureConfig for a node type, resolved for a context path.
    /// </summary>
    /// <param name="nodeType">The node type identifier</param>
    /// <param name="contextPath">The path context to resolve from</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The hub feature config or null if not found</returns>
    Task<HubFeatureConfig?> GetHubFeaturesAsync(string nodeType, string contextPath, CancellationToken ct = default);

    /// <summary>
    /// Saves a DataModel definition to a node type's partition.
    /// </summary>
    /// <param name="nodeTypePath">The full path to the NodeType node</param>
    /// <param name="dataModel">The DataModel to save</param>
    /// <param name="ct">Cancellation token</param>
    Task SaveDataModelAsync(string nodeTypePath, DataModel dataModel, CancellationToken ct = default);

    /// <summary>
    /// Saves a LayoutAreaConfig to a node type's partition.
    /// </summary>
    /// <param name="nodeTypePath">The full path to the NodeType node</param>
    /// <param name="layoutArea">The LayoutAreaConfig to save</param>
    /// <param name="ct">Cancellation token</param>
    Task SaveLayoutAreaAsync(string nodeTypePath, LayoutAreaConfig layoutArea, CancellationToken ct = default);

    /// <summary>
    /// Deletes a LayoutAreaConfig from a node type's partition.
    /// </summary>
    /// <param name="nodeTypePath">The full path to the NodeType node</param>
    /// <param name="layoutAreaId">The layout area ID to delete</param>
    /// <param name="ct">Cancellation token</param>
    Task DeleteLayoutAreaAsync(string nodeTypePath, string layoutAreaId, CancellationToken ct = default);

    /// <summary>
    /// Gets all NodeType MeshNodes in the system (global search).
    /// Use GetNodeTypeNodesAsync for context-aware resolution.
    /// </summary>
    /// <returns>Async enumerable of all NodeType MeshNodes</returns>
    IAsyncEnumerable<MeshNode> GetAllNodeTypeNodesAsync();

    /// <summary>
    /// Gets all DataModels from all NodeType nodes (global search).
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of all DataModels</returns>
    Task<IReadOnlyList<DataModel>> GetAllDataModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all LayoutAreaConfigs from all NodeType nodes (global search).
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of all LayoutAreaConfigs</returns>
    Task<IReadOnlyList<LayoutAreaConfig>> GetAllLayoutAreasAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all partition objects (DataModels, LayoutAreas, HubFeatures) for a node type.
    /// This loads all objects from the partition with their timestamps for cache validation.
    /// </summary>
    /// <param name="nodeType">The node type identifier</param>
    /// <param name="contextPath">The path context to resolve from</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The TypeNodePartition with all objects and newest timestamp</returns>
    Task<TypeNodePartition?> GetTypeNodePartitionAsync(string nodeType, string contextPath, CancellationToken ct = default);
}
