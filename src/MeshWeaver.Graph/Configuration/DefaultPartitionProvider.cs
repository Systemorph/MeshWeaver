using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Seeds the only two framework partitions the clean model keeps:
/// <list type="bullet">
///   <item><b>Admin</b> — system administration data + version tracking + global catalogs
///     (agents / models / roles). Its schema is created eagerly by the migration.</item>
///   <item><b>Auth</b> — the central access-object lookup mirror. The per-partition trigger
///     (<c>mirror_access_object_to_auth_schema</c>, V27) lands <c>User</c> / <c>Group</c> /
///     <c>Role</c> / <c>VUser</c> / <c>ApiToken</c> / <c>Space</c> rows here so token validation,
///     role/group lookup, and email→user resolution are single-schema queries.
///     <b>Trigger-populated only — application code NEVER writes to <c>auth</c></b>
///     (<c>PartitionWriteGuardValidator</c> rule 1 blocks it). The migration creates the
///     <c>auth</c> schema so the trigger has a destination.</item>
/// </list>
///
/// <para><b>Legacy partitions are gone (2026-06-05).</b> <c>Portal</c> / <c>Kernel</c> session
/// partitions are removed — compilation / script execution is modelled as <b>Activities</b>
/// inside the owning partition's <c>activities</c> table, not a <c>kernel</c> schema (the
/// standalone <c>kernel/*</c> address was retired; see <c>KernelContainer</c>). The global
/// <c>_Activity</c> / <c>_UserActivity</c> / <c>_Thread</c> satellite partitions are dropped
/// (everything is partition-scoped now), and the seed grants go too: the system identity gets
/// <c>Permission.All</c> from the <c>PermissionEvaluator</c> fast-path, and the only Public-viewer
/// grants were on the now-removed Portal/Kernel. <b><c>_Access</c> is KEPT</b> — global /
/// root-scope access grants are a live feature, not legacy. See
/// <c>Doc/Architecture/PartitionStorageRouting.md</c>.</para>
///
/// <para>User and Space partitions are NOT seeded here — they own their partitions and are
/// provisioned on create by <c>OwnsPartitionProvisioningValidator</c>.</para>
/// </summary>
internal class DefaultPartitionProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return CreatePartition("Admin", "admin", "System administration",
            tableMappings: PartitionDefinition.StandardTableMappings);

        yield return CreatePartition("Auth", "auth",
            "Auth lookup mirror: User / Group / Role / VUser / ApiToken / Space rows mirrored "
                + "from every source partition by the V27 trigger. Trigger-populated only — "
                + "application code never writes here.",
            tableMappings: PartitionDefinition.StandardTableMappings);

        // Global access-grant scope. A `_Access`-namespace AccessAssignment with MainNode=""
        // is a ROOT-SCOPE grant that applies across every partition (platform-admin / global
        // viewer) — the canonical "namespace == _Access globally" pattern. It is NOT legacy
        // (unlike the _Activity/_UserActivity/_Thread global satellites, which are dropped):
        // SecurityService reads `namespace:_Access` and root-scope admin gates depend on it.
        // Schema name sheds the underscore (`system_access`); the primary table IS the
        // `access` satellite. Created eagerly by the migration (no lazy create).
        yield return CreateGlobalSatellitePartition("_Access", "system_access", "access",
            "Global / root-scope access assignments — grants that apply across every partition.");
    }

    /// <summary>
    /// A top-level partition for a global satellite namespace whose schema name differs from
    /// the lowercased namespace (e.g. <c>_Access</c> → <c>system_access</c>) and whose primary
    /// table IS the satellite table (no within-partition suffix routing).
    /// </summary>
    private static MeshNode CreateGlobalSatellitePartition(
        string ns, string schema, string table, string description) =>
        new(ns, PartitionNodeType.Namespace)
        {
            NodeType = PartitionNodeType.NodeType,
            Name = ns,
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = ns,
                DataSource = "default",
                Schema = schema,
                Table = table,
                Versioned = true,
                Description = description,
            }
        };

    private static MeshNode CreatePartition(
        string id, string schema, string description,
        Dictionary<string, string>? tableMappings,
        bool versioned = true) =>
        new(id, PartitionNodeType.Namespace)
        {
            NodeType = PartitionNodeType.NodeType,
            Name = id,
            State = MeshNodeState.Active,
            Content = new PartitionDefinition
            {
                Namespace = id,
                DataSource = "default",
                Schema = schema,
                Table = "mesh_nodes",
                TableMappings = tableMappings,
                Versioned = versioned,
                Description = description,
            }
        };
}
