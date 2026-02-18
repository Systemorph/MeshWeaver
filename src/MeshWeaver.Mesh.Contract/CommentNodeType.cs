namespace MeshWeaver.Mesh;

/// <summary>
/// Provides the NodeType constant and configuration for Comment nodes.
/// Comments are stored as child MeshNodes under document nodes.
/// </summary>
public static class CommentNodeType
{
    /// <summary>
    /// The NodeType value used to identify comment nodes.
    /// </summary>
    public const string NodeType = "Comment";

    /// <summary>
    /// When true, only the comment author can edit the comment text.
    /// Other users can still view the comment but cannot switch to edit mode.
    /// </summary>
    public const bool AuthorEditOnly = true;
}
