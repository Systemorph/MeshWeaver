using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The two nodes that make a freshly created partition ROOT a whole partition: its creator's Admin
/// grant, and the <c>Admin/Partition/{id}</c> <see cref="PartitionDefinition"/> that primes routing.
///
/// <para>ONE definition of each, shared by <see cref="SpaceNodeType"/>'s post-creation handler and
/// <see cref="InMeshPartitionOwnerPostCreationHandler"/> (the same contract for a partition-owning
/// type declared in mesh content). Two hand-written copies of "what a new partition needs" is how
/// one of them drifts — the deletion side learned that with #3436.</para>
/// </summary>
public static class PartitionOwnership
{
    /// <summary>
    /// <c>{root}/_Access/{createdBy}_Access</c>, role <c>Admin</c>, scoped to the new partition —
    /// what makes the creator its owner. Written as System: a brand-new partition root is a path its
    /// own creator holds nothing on yet.
    /// </summary>
    public static MeshNode CreatorAdminGrant(MeshNode root, string createdBy) =>
        new($"{createdBy}_Access", $"{root.Id}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{createdBy} Access",
            MainNode = root.Id,
            Content = new AccessAssignment
            {
                AccessObject = createdBy,
                DisplayName = createdBy,
                Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }]
            }
        };

    /// <summary>
    /// The per-partition <c>Admin/Partition/{id}</c> definition. Persisting it primes the partition
    /// cache and the notify-driven schema provisioning, so the partition is consistently routable
    /// across the mesh.
    /// </summary>
    public static MeshNode PartitionDefinitionNode(MeshNode root, string description) =>
        new(root.Id, PartitionNodeType.Namespace)
        {
            NodeType = PartitionNodeType.NodeType,
            Name = root.Name ?? root.Id,
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = root.Id,
                DataSource = "default",
                Schema = root.Id.ToLowerInvariant(),
                TableMappings = PartitionDefinition.DefaultSegmentTableMappings(),
                NodeTypeTableMappings = PartitionDefinition.DefaultNodeTypeTableMappings(),
                Description = description
            }
        };
}
