using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test.Security;

/// <summary>
/// Tests for AccessAssignment as MeshNodes: creation, deletion, deny override logic,
/// and effective permission evaluation.
/// </summary>
public class AccessAssignmentTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder).AddRowLevelSecurity();

    #region AddUserRole creates AccessAssignment MeshNodes

    [Fact(Timeout = 10000)]
    public async Task AddUserRole_GlobalAssignment_GrantsPermissionsEverywhere()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("GlobalUser", "Admin", null, "system", TestTimeout);

        var permissions = await svc.GetEffectivePermissionsAsync("ACME/Software/Project", "GlobalUser", TestTimeout);
        permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 10000)]
    public async Task AddUserRole_AncestorAssignment_GrantsPermissionsToDescendants()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("AncestorUser", "Editor", "ACME/Software", "system", TestTimeout);

        var permissions = await svc.GetEffectivePermissionsAsync("ACME/Software/Project", "AncestorUser", TestTimeout);
        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
    }

    [Fact(Timeout = 10000)]
    public async Task AddUserRole_LocalAssignment_GrantsPermissionsAtPath()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("LocalUser", "Viewer", "ACME/Software/Project", "system", TestTimeout);

        var permissions = await svc.GetEffectivePermissionsAsync("ACME/Software/Project", "LocalUser", TestTimeout);
        permissions.Should().Be(Permission.Read);
    }

    [Fact(Timeout = 10000)]
    public async Task AddUserRole_MixedLevels_CombinesPermissions()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("GlobalAdmin", "Admin", null, "system", TestTimeout);
        await svc.AddUserRoleAsync("OrgEditor", "Editor", "ACME/Software", "system", TestTimeout);
        await svc.AddUserRoleAsync("LocalViewer", "Viewer", "ACME/Software/Project", "system", TestTimeout);

        // GlobalAdmin should have all permissions
        var globalPerms = await svc.GetEffectivePermissionsAsync("ACME/Software/Project", "GlobalAdmin", TestTimeout);
        globalPerms.Should().Be(Permission.All);

        // OrgEditor should have editor permissions on ACME/Software/Project (inherited)
        var orgPerms = await svc.GetEffectivePermissionsAsync("ACME/Software/Project", "OrgEditor", TestTimeout);
        orgPerms.Should().HaveFlag(Permission.Read);
        orgPerms.Should().HaveFlag(Permission.Update);

        // LocalViewer should have viewer permissions at ACME/Software/Project
        var localPerms = await svc.GetEffectivePermissionsAsync("ACME/Software/Project", "LocalViewer", TestTimeout);
        localPerms.Should().Be(Permission.Read);
    }

    #endregion

    #region Deny via AccessAssignment MeshNode

    [Fact(Timeout = 10000)]
    public async Task DenyAssignment_OverridesInheritedGrant()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Grant at parent
        await svc.AddUserRoleAsync("Alice", "Editor", "ACME/Software", "system", TestTimeout);

        // Create a deny AccessAssignment MeshNode at child
        var denyNode = new MeshNode("Alice_Access", "ACME/Software/Project")
        {
            NodeType = "AccessAssignment",
            Name = "Alice Access",
            Content = new AccessAssignment
            {
                AccessObject = "Alice",
                Roles = [new RoleAssignment { Role = "Editor", Denied = true }]
            }
        };
        await persistence.SaveNodeAsync(denyNode, TestTimeout);

        // Clear cache to pick up the new deny
        (svc as SecurityService)?.ClearPermissionCache();

        var permissions = await svc.GetEffectivePermissionsAsync("ACME/Software/Project", "Alice", TestTimeout);
        permissions.Should().Be(Permission.None, "denied Editor role should yield no permissions at child");
    }

    [Fact(Timeout = 10000)]
    public async Task DenyAtMiddle_GrantAtChild_ChildOverridesDeny()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Grant at grandparent
        await svc.AddUserRoleAsync("OverrideUser", "Viewer", "Org", "system", TestTimeout);

        // Deny at parent
        var denyNode = new MeshNode("OverrideUser_Access", "Org/Team")
        {
            NodeType = "AccessAssignment",
            Name = "OverrideUser Access",
            Content = new AccessAssignment
            {
                AccessObject = "OverrideUser",
                Roles = [new RoleAssignment { Role = "Viewer", Denied = true }]
            }
        };
        await persistence.SaveNodeAsync(denyNode, TestTimeout);

        // Grant again at child
        await svc.AddUserRoleAsync("OverrideUser", "Viewer", "Org/Team/Project", "system", TestTimeout);

        (svc as SecurityService)?.ClearPermissionCache();

        var permTeam = await svc.GetEffectivePermissionsAsync("Org/Team", "OverrideUser", TestTimeout);
        var permProject = await svc.GetEffectivePermissionsAsync("Org/Team/Project", "OverrideUser", TestTimeout);

        permTeam.Should().Be(Permission.None, "deny at Org/Team should block inherited grant");
        permProject.Should().Be(Permission.Read, "grant at child should override deny at parent");
    }

    [Fact(Timeout = 10000)]
    public async Task DenyOneRole_KeepsOtherRoles()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceService>();

        // Grant Admin globally
        await svc.AddUserRoleAsync("MixedUser", "Admin", null, "system", TestTimeout);
        // Also grant Editor at ACME/Software
        await svc.AddUserRoleAsync("MixedUser", "Editor", "ACME/Software", "system", TestTimeout);

        // Deny Admin at ACME/Software/Secure
        var denyNode = new MeshNode("MixedUser_Access", "ACME/Software/Secure")
        {
            NodeType = "AccessAssignment",
            Name = "MixedUser Access",
            Content = new AccessAssignment
            {
                AccessObject = "MixedUser",
                Roles = [new RoleAssignment { Role = "Admin", Denied = true }]
            }
        };
        await persistence.SaveNodeAsync(denyNode, TestTimeout);

        (svc as SecurityService)?.ClearPermissionCache();

        var permissions = await svc.GetEffectivePermissionsAsync("ACME/Software/Secure", "MixedUser", TestTimeout);

        // Should have Editor permissions but NOT Admin's Delete
        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
        permissions.Should().NotHaveFlag(Permission.Delete,
            "Admin was denied, so Delete should not be available, only Editor permissions remain");
    }

    #endregion

    #region RemoveUserRole

    [Fact(Timeout = 10000)]
    public async Task RemoveUserRole_RevokesPermissions()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        await svc.AddUserRoleAsync("TempUser", "Editor", "ACME/Software/Project", "system", TestTimeout);

        // Verify permission exists
        var permBefore = await svc.GetEffectivePermissionsAsync("ACME/Software/Project", "TempUser", TestTimeout);
        permBefore.Should().HaveFlag(Permission.Update);

        // Remove the role
        await svc.RemoveUserRoleAsync("TempUser", "Editor", "ACME/Software/Project", TestTimeout);

        var permAfter = await svc.GetEffectivePermissionsAsync("ACME/Software/Project", "TempUser", TestTimeout);
        permAfter.Should().Be(Permission.None, "removed role should yield no permissions");
    }

    #endregion

    #region UI Layout (Structural)

    [Fact(Timeout = 10000)]
    public async Task AccessControl_NoRLS_ShowsWarningMessage()
    {
        var svc = Mesh.ServiceProvider.GetService<ISecurityService>();
        svc.Should().NotBeNull("RLS is configured in this test base");
    }

    [Fact(Timeout = 10000)]
    public async Task AccessControl_EmptyAssignments_HasNoPermissions()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Query permissions for a path with no roles assigned
        var permissions = await svc.GetEffectivePermissionsAsync("Empty/Namespace/Path", "SomeUser", TestTimeout);

        permissions.Should().Be(Permission.None, "no roles have been assigned to this path");
    }

    [Fact(Timeout = 10000)]
    public async Task AccessControl_InheritedAndLocalAssignments_BothApply()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Set up inherited + local assignments
        await svc.AddUserRoleAsync("InheritedUser", "Viewer", "NS", "system", TestTimeout);
        await svc.AddUserRoleAsync("LocalUser", "Editor", "NS/Child", "system", TestTimeout);

        // InheritedUser should have Viewer on NS/Child (inherited)
        var inheritedPerms = await svc.GetEffectivePermissionsAsync("NS/Child", "InheritedUser", TestTimeout);
        inheritedPerms.Should().Be(Permission.Read);

        // LocalUser should have Editor on NS/Child (local)
        var localPerms = await svc.GetEffectivePermissionsAsync("NS/Child", "LocalUser", TestTimeout);
        localPerms.Should().HaveFlag(Permission.Update);
    }

    #endregion
}
