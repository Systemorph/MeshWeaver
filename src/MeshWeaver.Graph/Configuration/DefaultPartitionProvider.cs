using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Provides the default partition nodes that are created during system initialization.
/// Admin: system administration data (PlatformAdmin only).
/// User: user profiles, settings, and all user-scoped satellite data (public read).
/// Portal: portal session nodes (public read, unversioned).
/// Kernel: kernel session nodes (public read, unversioned).
/// Also provides static AccessAssignment nodes granting Public Viewer on non-admin partitions.
/// </summary>
internal class DefaultPartitionProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes()
    {
        yield return CreatePartition("Admin", "admin", "System administration",
            tableMappings: PartitionDefinition.StandardTableMappings);

        yield return CreatePartition("User", "user", "User profiles, settings, and user-scoped data",
            tableMappings: PartitionDefinition.StandardTableMappings);

        yield return CreatePartition("Portal", "portal", "Portal sessions",
            tableMappings: PartitionDefinition.StandardTableMappings, versioned: false);

        yield return CreatePartition("Kernel", "kernel", "Kernel sessions",
            tableMappings: PartitionDefinition.StandardTableMappings, versioned: false);

        // Global-scope satellite namespaces — these are top-level partitions, not
        // a `{partition}/_X/...` suffix, so writes to e.g. `_Access/Roland_Access`
        // (a global access grant) or `_Activity/<id>` (an activity not bound to a
        // partition) need their own routable PartitionDefinition. Schema name
        // sheds the underscore for SQL hygiene; the primary table is the
        // satellite table itself, no further suffix routing needed within the
        // schema. SecurityService already queries `namespace:_Access`
        // unconditionally; without these registrations the Postgres provider's
        // `Matches(...)` returns false and writes fault with
        // "no IPartitionStorageProvider matches" (repro:
        // EffectivePermissionPostgresTest.CreateOrganization_HasPermission_ReturnsAdmin).
        yield return CreateGlobalSatellitePartition("_Access", "system_access", "access",
            "Global access assignments — grants that apply across every partition.");
        yield return CreateGlobalSatellitePartition("_Activity", "system_activity", "activities",
            "Global activity log — long-running operations not bound to a content partition.");
        yield return CreateGlobalSatellitePartition("_UserActivity", "system_user_activity", "user_activities",
            "Global per-user activity stream.");
        yield return CreateGlobalSatellitePartition("_Thread", "system_thread", "threads",
            "Global discussion threads not bound to a content partition.");

        // Grant Public (all authenticated users) Viewer role on Portal and Kernel.
        // User namespace is NOT included — only the User node itself is publicly readable
        // (via UserAccessRule), children require explicit access (via UserScopeGrantHandler).
        foreach (var ns in new[] { "Portal", "Kernel" })
            yield return CreatePublicViewerAccess(ns);
    }

    /// <summary>
    /// Creates a top-level partition for a global satellite namespace
    /// (<c>_Access</c>, <c>_Activity</c>, <c>_UserActivity</c>, <c>_Thread</c>).
    /// The primary table IS the satellite table (no within-partition suffix
    /// routing) so a write to e.g. <c>_Access/Roland_Access</c> lands in
    /// <c>system_access.access</c>.
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
                // No TableMappings — the partition IS the satellite, no further routing.
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

    /// <summary>
    /// Creates a static AccessAssignment granting the Public user Viewer role
    /// on a namespace. This gives all authenticated users read access.
    /// </summary>
    private static MeshNode CreatePublicViewerAccess(string ns) =>
        new($"{WellKnownUsers.Public}_Access", ns)
        {
            NodeType = "AccessAssignment",
            Name = $"{WellKnownUsers.Public} Access",
            Content = new AccessAssignment
            {
                AccessObject = WellKnownUsers.Public,
                DisplayName = "All authenticated users",
                Roles = [new RoleAssignment { Role = "Viewer" }]
            }
        };
}
