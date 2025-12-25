using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service for managing NodeType definitions via hub messaging.
///
/// NodeTypes are scoped hierarchically - for a node at path "a/b/c", applicable NodeTypes
/// are found by walking up the path: a/b/c/*, a/b/*, a/*, and root level (including type/*).
///
/// NodeType data is accessed via GetDataRequest/Response to the NodeType hub.
/// CodeConfiguration is stored as data on the NodeType hub and modified via DataChangeRequest.
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
    /// For path "a/b/c": searches a/b/c/*, a/b/*, a/*, root/*, and type/*.
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
    /// Gets the NodeTypeData for a node type path via hub messaging.
    /// Returns combined NodeTypeDefinition and CodeConfiguration.
    /// </summary>
    /// <param name="nodeTypePath">The full path to the NodeType (e.g., "type/Person")</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The NodeTypeData or null if not found</returns>
    Task<NodeTypeData?> GetNodeTypeDataAsync(string nodeTypePath, CancellationToken ct = default);

    /// <summary>
    /// Gets the CodeConfiguration for a node type, resolved for a context path.
    /// Uses hub messaging via GetDataRequest to the NodeType's hub.
    /// </summary>
    /// <param name="nodeType">The node type identifier</param>
    /// <param name="contextPath">The path context to resolve from</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The CodeConfiguration or null if not found</returns>
    Task<CodeConfiguration?> GetCodeConfigurationAsync(string nodeType, string contextPath, CancellationToken ct = default);

    /// <summary>
    /// Saves a CodeConfiguration to a node type's hub via DataChangeRequest.
    /// </summary>
    /// <param name="nodeTypePath">The full path to the NodeType node</param>
    /// <param name="config">The CodeConfiguration to save</param>
    /// <param name="ct">Cancellation token</param>
    Task SaveCodeConfigurationAsync(string nodeTypePath, CodeConfiguration config, CancellationToken ct = default);

    /// <summary>
    /// Gets all NodeType MeshNodes in the system (global search).
    /// Use GetNodeTypeNodesAsync for context-aware resolution.
    /// </summary>
    /// <returns>Async enumerable of all NodeType MeshNodes</returns>
    IAsyncEnumerable<MeshNode> GetAllNodeTypeNodesAsync();

    /// <summary>
    /// Gets all CodeConfigurations from all NodeType nodes (global search).
    /// Uses hub messaging for each NodeType.
    /// </summary>
    /// <param name="ct">Cancellation token</param>
    /// <returns>List of all CodeConfigurations</returns>
    Task<IReadOnlyList<CodeConfiguration>> GetAllCodeConfigurationsAsync(CancellationToken ct = default);
}
