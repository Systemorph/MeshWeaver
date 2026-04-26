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
    /// </summary>
    public static MeshNode UserRole(string userId, string roleId, string? scope = null, bool denied = false)
    {
        var ns = string.IsNullOrEmpty(scope) ? "_Access" : $"{scope}/_Access";
        return new MeshNode($"{userId}_Access", ns)
        {
            NodeType = "AccessAssignment",
            Name = $"{userId} Access",
            MainNode = scope ?? "",
            Content = new AccessAssignment
            {
                AccessObject = userId,
                DisplayName = userId,
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
