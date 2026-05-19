using System.Collections.Immutable;
using MeshWeaver.Graph;

namespace MeshWeaver.AI;

/// <summary>
/// Constants, configuration, and MeshNode definition for ThreadMessage node types.
/// ThreadMessage nodes are child nodes of Thread nodes containing individual messages.
/// Each ThreadMessage hub manages its own persistence exclusively via AddMeshDataSource â€”
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
        // Public-read on the ThreadMessage NodeType HOST hub — shared type
        // metadata (layout definitions, schema). Per-message data access is
        // gated by RLS on the message's mainNode/path. Without this, per-
        // instance ThreadMessage hubs can't subscribe to their type's
        // MeshNodeReference at activation. Same rule as Agent / User /
        // Markdown / etc.
        builder.ConfigureNodeTypeAccess(a => a.WithPublicRead(NodeType));
        return builder;
    }

    /// <summary>
    /// Creates a MeshNode definition for the ThreadMessage node type.
    /// HubConfiguration includes AddMeshDataSource â€” the hub owns persistence exclusively.
    /// </summary>
    public static MeshNode CreateMeshNode() => new(NodeType)
    {
        Name = "Thread Message",
        Icon = "/static/NodeTypeIcons/message.svg",
        IsSatelliteType = true,
        ExcludeFromContext = ImmutableHashSet.Create("search", "create"),
        HubConfiguration = config => config
            .AddThreadMessageViews()
            .AddMeshDataSource(source => source.WithContentType<ThreadMessage>())
    };
}
