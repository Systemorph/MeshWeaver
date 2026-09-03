using System.Collections.Immutable;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The node types whose reads may NEVER be narrowed to a subset of partitions, however a narrowing
/// is expressed — the security fold's own queries. THE one list: the storage planner
/// (<c>PostgreSqlPartitionedMeshQuery</c>, MeshWeaver.Plugins) refuses these at QUERY time, and
/// <c>InstanceLocationDeclarationValidator</c> refuses them at AUTHORING time, from this same set.
///
/// <para>🚨 <b>This is a refusal, not a default.</b> <c>Doc/Architecture/UnanchoredSecurityReads</c>
/// states the rule this set enforces: in the permission fold "no result" and "not allowed" are the
/// same value, so a read that comes back SHORT is indistinguishable from one that came back EMPTY,
/// and both read as denied. A <c>GroupMembership</c> lives under the GROUP node while the grant that
/// names the group lives elsewhere, so those reads carry no <c>path:</c> and no <c>namespace:</c> BY
/// NECESSITY.</para>
///
/// <para>Narrowing them fails in two directions and neither is visible: a group-derived permission
/// silently vanishes (#2011), and — worse — a group-scoped <c>AccessAssignment</c> with
/// <c>Denied = true</c> is applied only to the viewers the membership read SAYS are in the group, so
/// a revocation <b>fails OPEN</b> and the viewer keeps reading content the deny was written to take
/// away. Nothing is logged and nothing fails. The trigger is GROWTH, not a change, so it appears on
/// the largest install first.</para>
///
/// <para>Hoisted here from MeshWeaver.Plugins (#3039) so the authoring gate and the planner cannot
/// drift apart: a type added to the fold is refused by both the moment it is added here.</para>
/// </summary>
public static class NeverNarrowedNodeTypes
{
    /// <summary>
    /// The fold's own node types, by name. Built from <see cref="SecurityQueries"/>' own constants
    /// where it has them, so a rename moves this set with it rather than leaving a literal behind
    /// that quietly stops matching. Immutable and never written at runtime — the sanctioned
    /// <c>static readonly</c> shape (NoStaticState.md).
    /// </summary>
    public static readonly IReadOnlySet<string> Names =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            // SecurityQueries.Roles — a custom Role definition may live in any partition.
            SecurityQueries.RoleNodeType,
            // SecurityQueries.Memberships — the group and the grant that names it live apart.
            SecurityQueries.GroupMembershipNodeType,
            // The grant rows themselves: the root-scope `namespace:_Access` leg, and every
            // per-scope walk that falls through to the fan-out.
            "AccessAssignment",
            // The root scope's policy leg carries the EMPTY namespace by construction.
            "PartitionAccessPolicy");

    /// <summary>
    /// True when <paramref name="nodeType"/> is one the fold reads and therefore one no narrowing
    /// may touch. Gated types (<c>SecurityQueries.GatedNodes</c>) are refused too, but they are
    /// declared per mesh rather than known statically, so the caller passes them in — see
    /// <see cref="GatedNodeTypesOf"/>.
    /// </summary>
    /// <param name="nodeType">The node type, exactly as instances carry it in <c>nodeType:</c>.</param>
    /// <param name="gatedNodeTypes">
    /// The mesh's type-declared gates (<see cref="MeshConfiguration.NodeTypeGates"/>' node types),
    /// or null when none are configured.
    /// </param>
    /// <returns>True if the type must always fan out.</returns>
    public static bool Refuses(string? nodeType, IReadOnlySet<string>? gatedNodeTypes = null)
        => !string.IsNullOrEmpty(nodeType)
           && (Names.Contains(nodeType)
               || gatedNodeTypes?.Contains(nodeType) == true);

    /// <summary>
    /// The node types of every type-declared gate on <paramref name="configuration"/>
    /// (<c>ConfigureNodeTypeAccess(a => a.WithGate(...))</c>), as the set
    /// <see cref="Refuses"/> expects. Empty on a mesh that declares none.
    /// </summary>
    /// <param name="configuration">The mesh configuration carrying the gates.</param>
    /// <returns>The gated node types, compared case-insensitively.</returns>
    public static IReadOnlySet<string> GatedNodeTypesOf(MeshConfiguration configuration)
        => configuration.NodeTypeGates
            .Select(gate => gate.NodeType)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
}
