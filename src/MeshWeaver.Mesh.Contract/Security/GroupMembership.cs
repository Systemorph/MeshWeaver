namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Content type for GroupMembership mesh nodes.
/// GroupMembership nodes are children of a Group node.
/// E.g., Groups/Engineering/Alice (namespace=Groups/Engineering, id=Alice, nodeType=GroupMembership).
/// </summary>
public record GroupMembership
{
    /// <summary>
    /// The path of the member (User or nested Group).
    /// </summary>
    public string MemberId { get; init; } = "";
}
