namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Types of operations that can be performed on mesh nodes.
/// Used by the unified INodeValidator interface.
/// </summary>
public enum NodeOperation
{
    /// <summary>
    /// Reading or viewing a node.
    /// </summary>
    Read,

    /// <summary>
    /// Creating a new node.
    /// </summary>
    Create,

    /// <summary>
    /// Updating an existing node.
    /// </summary>
    Update,

    /// <summary>
    /// Deleting a node.
    /// </summary>
    Delete,

    /// <summary>
    /// Moving a node to a new path.
    /// </summary>
    Move
}
