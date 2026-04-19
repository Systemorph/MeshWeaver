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

    /// <summary>
    /// Returns the last compilation error recorded for the given NodeType path,
    /// or <c>null</c> if compilation has not failed. The error text includes the
    /// formatted Roslyn diagnostics as produced by
    /// <c>MeshNodeCompilationService</c>.
    /// </summary>
    /// <remarks>
    /// Used by MCP <c>Get</c> / <c>GetDiagnostics</c> so callers (e.g. the Coder
    /// agent) can verify that a NodeType they just created or updated actually
    /// compiles. The error is cached by <c>NodeTypeService</c> each time a compile
    /// fails and cleared when it succeeds.
    /// </remarks>
    string? GetCompilationError(string nodeTypePath) => null;

    /// <summary>
    /// Returns <c>true</c> if compilation for the given NodeType path is currently
    /// running (the task has been kicked off but not yet completed). Consumers can use
    /// this to render a "Compiling…" progress indicator so the user sees activity
    /// rather than a blank layout while they wait.
    /// </summary>
    bool IsCompiling(string nodeTypePath) => false;

    /// <summary>
    /// When compilation for <paramref name="nodeTypePath"/> is running, returns when
    /// it started (UTC). Otherwise <c>null</c>. Paired with <see cref="IsCompiling"/>
    /// to show elapsed-time feedback in progress overlays.
    /// </summary>
    DateTimeOffset? GetCompilationStartedAt(string nodeTypePath) => null;
}
