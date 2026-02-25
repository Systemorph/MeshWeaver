using MeshWeaver.Domain;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Content type for GroupMembership mesh nodes.
/// Maps a member (User or Group) to one or more groups at a specific scope.
/// The scope is determined by the node's namespace in the mesh hierarchy.
/// Node ID = {Member}_Membership, so one node per member per scope.
/// </summary>
public record GroupMembership
{
    /// <summary>Member identifier (User or Group path) for this membership.</summary>
    [MeshNode("namespace:User nodeType:User", "namespace:{node.namespace} nodeType:Group scope:selfAndAncestors")]
    public string Member { get; init; } = "";

    /// <summary>Optional display name for the member.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Group memberships for this member at this scope.</summary>
    [MeshNodeCollection("namespace:{node.namespace} nodeType:Group scope:selfAndAncestors")]
    public IReadOnlyList<MembershipEntry> Groups { get; init; } = [];
}

/// <summary>
/// A single group membership entry within a GroupMembership.
/// </summary>
public record MembershipEntry
{
    /// <summary>The group identifier (path to the Group node).</summary>
    public string Group { get; init; } = "";
}
