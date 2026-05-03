using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.Hosting.Monolith.TestBase;

/// <summary>
/// Test-only factory for AccessAssignment / PartitionAccessPolicy MeshNodes.
/// AccessAssignment is just a MeshNode — these helpers encode the storage
/// convention (<c>{scope}/_Access/{userId}_Access</c> for assignments,
/// <c>{ns}/_Policy</c> for policies) so test authors don't have to repeat it.
/// Seed via <c>builder.AddMeshNodes(...)</c> in <c>ConfigureMesh</c>; SecurityService
/// reads them at hub init via the standard static-node-provider path.
/// </summary>
public static class AssignmentNodeFactory
{
    /// <summary>
    /// AccessAssignment node granting (or, when <paramref name="denied"/> is true,
    /// denying) <paramref name="roleId"/> to <paramref name="userId"/> at
    /// <paramref name="scope"/> (null/empty means global root).
    ///
    /// <para><paramref name="accessObject"/> overrides the AccessAssignment's
    /// <c>AccessObject</c> when the seed needs a unique node id but the role
    /// must apply to a different (e.g., shared) principal — typically
    /// <see cref="WellKnownUsers.Anonymous"/>. When omitted, AccessObject
    /// equals <paramref name="userId"/> (the historical default).</para>
    /// </summary>
    public static MeshNode UserRole(
        string userId, string roleId, string? scope = null, bool denied = false,
        string? accessObject = null)
    {
        var ns = string.IsNullOrEmpty(scope) ? "_Access" : $"{scope}/_Access";
        var subject = accessObject ?? userId;
        return new MeshNode($"{userId}_Access", ns)
        {
            NodeType = "AccessAssignment",
            Name = $"{userId} Access",
            MainNode = scope ?? "",
            Content = new AccessAssignment
            {
                AccessObject = subject,
                DisplayName = subject,
                Roles = [new RoleAssignment { Role = roleId, Denied = denied }]
            }
        };
    }

    /// <summary>PartitionAccessPolicy node at <c>{ns}/_Policy</c>.</summary>
    public static MeshNode Policy(string ns, PartitionAccessPolicy policy)
        => new("_Policy", ns)
        {
            NodeType = "PartitionAccessPolicy",
            Name = "Access Policy",
            Content = policy,
        };
}
