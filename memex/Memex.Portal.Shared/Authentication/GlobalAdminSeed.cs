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

        // Two grants per configured admin:
        //   (1) Admin/_Access  — the canonical "global admin = admin on the Admin partition" grant
        //       (scope "Admin"); this is what hub.IsGlobalAdmin() keys off.
        //   (2) Provider/_Access — standing write on the top-level "Provider" model-catalog partition
        //       so platform admins can manage the shared providers/models (endpoints, keys, enabled
        //       models) through the standard mesh catalog UI. A global admin is NOT a data-superuser
        //       (the Admin-scope grant does not cover other partitions), so the Provider catalog —
        //       which moved from Admin/Provider to the top-level Provider partition — needs this
        //       explicit grant; its _Policy lifts the write caps but caps are ceilings, not grants.
        //       Non-admins hold no write role anywhere in the Provider hierarchy → they stay
        //       read-only (the _Policy is PublicRead). See Doc/Architecture/NodeTypeCatalogs.md
        //       and AccessControl.md.
        var nodes = new MeshNode[ids.Length * 2];
        for (var i = 0; i < ids.Length; i++)
        {
            var userId = ids[i].Trim();

            nodes[i * 2] = new MeshNode(userId + "_Access", "Admin/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"{userId} — Admin",
                Content = new AccessAssignment
                {
                    AccessObject = userId,
                    DisplayName = userId,
                    Roles = [new RoleAssignment { Role = "Admin" }],
                },
                MainNode = "",
            };

            nodes[i * 2 + 1] = new MeshNode(userId + "_Access", "Provider/_Access")
            {
                NodeType = "AccessAssignment",
                Name = $"{userId} — Provider Admin",
                Content = new AccessAssignment
                {
                    AccessObject = userId,
                    DisplayName = userId,
                    Roles = [new RoleAssignment { Role = "Admin" }],
                },
                MainNode = "Provider",
            };
        }
        return nodes;
    }
}
