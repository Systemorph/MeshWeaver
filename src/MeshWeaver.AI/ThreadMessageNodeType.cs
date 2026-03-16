using MeshWeaver.Graph;

namespace MeshWeaver.AI;

/// <summary>
/// Constants, configuration, and MeshNode definition for ThreadMessage node types.
/// ThreadMessage nodes are child nodes of Thread nodes containing individual messages.
/// Each ThreadMessage hub manages its own persistence exclusively via AddMeshDataSource —
/// no external code should access ThreadMessage persistence via IMeshService or IMeshQuery.
/// </summary>
public static class ThreadMessageNodeType
{
    /// <summary>
    /// The NodeType value used to identify thread message nodes.
    /// </summary>
    public const string NodeType = "ThreadMessage";

    /// <summary>
    /// Layout area for thread message overview.
    /// </summary>
    public const string OverviewArea = "Overview";

    /// <summary>
    /// Checks if a node type is ThreadMessage.
    /// </summary>
    public static bool IsThreadMessageNodeType(string? nodeType)
        => string.Equals(nodeType, NodeType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Registers the built-in "ThreadMessage" MeshNode on the mesh builder.
    /// </summary>
    public static TBuilder AddThreadMessageType<TBuilder>(this TBuilder builder) where TBuilder : MeshBuilder
    {
        builder.AddMeshNodes(CreateMeshNode());
        builder.AddAutocompleteExcludedTypes(NodeType);
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the ThreadMessage node type.
    /// HubConfiguration includes AddMeshDataSource — the hub owns persistence exclusively.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Thread Message",
        Icon = "/static/NodeTypeIcons/message.svg",
        IsSatelliteType = true,
        ExcludeFromContext = new HashSet<string> { "search", "create" },
        AssemblyLocation = typeof(ThreadMessageNodeType).Assembly.Location,
        HubConfiguration = config => config
            .AddThreadMessageViews()
            .AddMeshDataSource(source => source.WithContentType<ThreadMessage>())
    };
}
