namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Workspace collection names + data-source ids used by the reactive
/// SecurityService. The synced query data sources are registered in
/// <c>AddRowLevelSecurity</c> via <c>AddSyncedQuery</c>; SecurityService reads
/// from <c>workspace.GetStream(new CollectionReference(...))</c> using the
/// names below. Keeping the strings here means the producer
/// (SecurityServiceExtensions) and the consumer (SecurityService) cannot drift.
/// </summary>
public static class SecurityCollections
{
    /// <summary>NodeType discriminator for access-assignment MeshNodes.</summary>
    public const string AccessAssignmentNodeType = "AccessAssignment";

    /// <summary>NodeType discriminator for partition-access-policy MeshNodes.</summary>
    public const string PartitionAccessPolicyNodeType = "PartitionAccessPolicy";

    /// <summary>Data-source id for the synced access-assignments collection.</summary>
    public const string AccessAssignmentsId = "$mesh-access-assignments";

    /// <summary>Data-source id for the synced partition-access-policies collection.</summary>
    public const string PoliciesId = "$mesh-access-policies";

    /// <summary>Workspace collection name for access assignments (read by SecurityService).</summary>
    public const string AccessAssignments = "AccessAssignments";

    /// <summary>Workspace collection name for partition access policies (read by SecurityService).</summary>
    public const string Policies = "Policies";
}
