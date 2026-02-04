namespace MeshWeaver.Mesh;

/// <summary>
/// Represents the lifecycle state of a MeshNode.
/// </summary>
public enum MeshNodeState
{
    /// <summary>
    /// Node is brand new, just created in memory but not yet persisted.
    /// </summary>
    New,

    /// <summary>
    /// Node is being created and awaiting hub confirmation.
    /// </summary>
    Transient,

    /// <summary>
    /// Node has been validated and is active.
    /// </summary>
    Active,

    /// <summary>
    /// Node creation was rejected by the hub.
    /// </summary>
    Rejected,

    /// <summary>
    /// Node has been deleted or is being deleted.
    /// </summary>
    Deleted
}
