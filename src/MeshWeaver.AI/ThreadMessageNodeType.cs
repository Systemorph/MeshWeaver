namespace MeshWeaver.AI;

/// <summary>
/// Constants for ThreadMessage node types and layout areas.
/// ThreadMessage nodes are child nodes of Thread nodes containing individual messages.
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
    /// <param name="nodeType">The node type to check.</param>
    /// <returns>True if the node type is ThreadMessage.</returns>
    public static bool IsThreadMessageNodeType(string? nodeType)
        => string.Equals(nodeType, NodeType, StringComparison.OrdinalIgnoreCase);
}
