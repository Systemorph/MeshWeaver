using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI;

/// <summary>
/// Constants and configuration for Thread node types.
/// </summary>
public static class ThreadNodeType
{
    /// <summary>
    /// The NodeType value used to identify thread nodes.
    /// </summary>
    public const string NodeType = "Thread";

    /// <summary>
    /// Layout area for thread content and message history (default).
    /// </summary>
    public const string ThreadArea = "Thread";

    /// <summary>
    /// Layout area for delegation sub-thread history.
    /// </summary>
    public const string HistoryArea = "History";

    /// <summary>
    /// Checks if a MeshNode is a Thread by checking its NodeType.
    /// </summary>
    /// <param name="nodeType">The node type to check.</param>
    /// <returns>True if the node type is Thread.</returns>
    public static bool IsThreadNodeType(string? nodeType)
    {
        return string.Equals(nodeType, NodeType, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers the built-in "Thread" MeshNode on the mesh builder.
    /// </summary>
    /// <param name="builder">The mesh builder.</param>
    /// <param name="hubConfiguration">Hub configuration for thread nodes (views, data sources, etc.).</param>
    public static TBuilder AddThreadType<TBuilder>(this TBuilder builder,
        Func<MessageHubConfiguration, MessageHubConfiguration>? hubConfiguration = null)
        where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode(hubConfiguration));
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the Thread node type.
    /// </summary>
    /// <param name="hubConfiguration">Hub configuration for thread nodes (views, data sources, etc.).</param>
    public static MeshNode CreateMeshNode(
        Func<MessageHubConfiguration, MessageHubConfiguration>? hubConfiguration = null) => new(NodeType)
    {
        Name = "Thread",
        Icon = "/static/NodeTypeIcons/chat.svg",
        ExcludeFromContext = new HashSet<string> { "search" },
        AssemblyLocation = typeof(ThreadNodeType).Assembly.Location,
        HubConfiguration = hubConfiguration
    };
}
