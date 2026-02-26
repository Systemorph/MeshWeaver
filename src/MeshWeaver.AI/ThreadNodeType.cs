namespace MeshWeaver.AI;

/// <summary>
/// Constants for Thread node types and layout areas.
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
}
