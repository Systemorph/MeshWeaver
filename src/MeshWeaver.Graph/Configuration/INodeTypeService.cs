using MeshWeaver.Mesh;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Service for managing NodeType definitions.
///
/// NodeTypes are scoped hierarchically - for a node at path "a/b/c", applicable NodeTypes
/// are found by walking up the path: a/b/c/*, a/b/*, a/*, and root level (including type/*).
///
/// Statically registered types (via NodeTypeRegistry) take precedence over persistence.
/// </summary>
public interface INodeTypeService
{
    /// <summary>
    /// Gets a specific NodeType node by type identifier, resolved for a context path.
    /// First checks the static registry, then searches persistence.
    /// </summary>
    /// <param name="nodeType">The node type identifier (e.g., "story" or "type/story")</param>
    /// <param name="contextPath">The path context to resolve from</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The most local NodeType MeshNode matching the identifier, or null</returns>
    Task<MeshNode?> GetNodeTypeNodeAsync(string nodeType, string contextPath, CancellationToken ct = default);

    /// <summary>
    /// Gets the CodeConfiguration for a node type, resolved for a context path.
    /// </summary>
    /// <param name="nodeType">The node type identifier</param>
    /// <param name="contextPath">The path context to resolve from</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>The CodeConfiguration or null if not found</returns>
    Task<CodeConfiguration?> GetCodeConfigurationAsync(string nodeType, string contextPath, CancellationToken ct = default);

    /// <summary>
    /// Gets combined code from all dependencies of a NodeType.
    /// Used for Monaco autocomplete to include types from dependent configurations.
    /// </summary>
    /// <param name="dependencyPaths">List of NodeType paths to include (e.g., ["type/Person"])</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Combined source code from all dependencies</returns>
    Task<string> GetDependencyCodeAsync(IEnumerable<string> dependencyPaths, CancellationToken ct = default);
}
