using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.Configuration;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Seeds root-scope <see cref="AccessAssignment"/> nodes that grant the <c>Admin</c>
/// role to each user listed under <c>Auth:GlobalAdmins</c> in configuration.
///
/// <para>Background: <c>SecurityService.GetEffectiveRoles</c> walks scopes from root
/// down and accumulates role assignments. Without a root-scope AccessAssignment
/// granting Admin, a configured Microsoft Entra ID user has zero roles on the
/// root scope, which surfaces as <c>"lacks Read permission on 'Space'"</c>
/// when navigating to the NodeType detail page (and equivalent denials on
/// cross-partition operations like creating a new Space).</para>
///
/// <para>The test base ships an equivalent seed via
/// <c>TestUsers.PublicAdminAccess()</c> — production needs the same shape,
/// driven by config instead of hardcoded so each deployment can declare its own
/// admin list. See <c>Doc/Architecture/AccessControl.md</c> for the role
/// accumulation rules and <c>src/MeshWeaver.Mesh.Contract/Security/AccessAssignment.cs</c>
/// for the schema.</para>
/// </summary>
public static class GlobalAdminSeed
{
    private const string ConfigSection = "Auth:GlobalAdmins";

    /// <summary>
    /// Builds AccessAssignment MeshNodes for every user id in
    /// <c>Auth:GlobalAdmins</c>. Returns an empty array when the section is
    /// missing or empty — safe to chain via <c>builder.AddMeshNodes(...)</c>
    /// in environments that have no admins configured.
    /// </summary>
    public static MeshNode[] Build(IConfiguration configuration)
    {
        var ids = configuration.GetSection(ConfigSection).Get<string[]>()
                  ?? [];
        if (ids.Length == 0)
            return [];

        var nodes = new MeshNode[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            var userId = ids[i].Trim();
            var assignment = new AccessAssignment
            {
                AccessObject = userId,
                DisplayName = userId,
                Roles = [new RoleAssignment { Role = "Admin" }],
            };
            // 🚨 Global admin = admin on the ADMIN PARTITION. The grant lives at
            // namespace "Admin/_Access" (→ scope "Admin"); the global-admin
            // short-circuit in PermissionEvaluator turns Permission.All at scope
            // "Admin" into platform-superuser (All on every path). MainNode "" = the
            // whole Admin scope. This IS the db-init seed for config-driven admins —
            // a fresh DB with Auth:GlobalAdmins set comes up with each listed user
            // already a platform admin. See Doc/Architecture/AccessControl.md.
            nodes[i] = new MeshNode(userId + "_Access", "Admin/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"{userId} — Admin",
                Content = assignment,
                MainNode = "",
            };
        }
        return nodes;
    }
}
