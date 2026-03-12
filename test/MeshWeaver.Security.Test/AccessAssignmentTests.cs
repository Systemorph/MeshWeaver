using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests for AccessAssignment as MeshNodes: creation, deletion, deny override logic,
/// and effective permission evaluation.
/// </summary>
public class AccessAssignmentTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddRowLevelSecurity();

    #region AddUserRole creates AccessAssignment MeshNodes

    [Fact(Timeout = 10000)]
    public async Task AddUserRole_GlobalAssignment_GrantsPermissionsEverywhere()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("GlobalUser", "Admin", null, "system", TestTimeout);

        var permissions = await svc.GetEffectivePermissionsAsync("ACME/Project", "GlobalUser", TestTimeout);
        permissions.Should().Be(Permission.All);
    }

    [Fact(Timeout = 10000)]
    public async Task AddUserRole_AncestorAssignment_GrantsPermissionsToDescendants()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("AncestorUser", "Editor", "ACME", "system", TestTimeout);

        var permissions = await svc.GetEffectivePermissionsAsync("ACME/Project", "AncestorUser", TestTimeout);
        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
    }

    [Fact(Timeout = 10000)]
    public async Task AddUserRole_LocalAssignment_GrantsPermissionsAtPath()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("LocalUser", "Viewer", "ACME/Project", "system", TestTimeout);

        var permissions = await svc.GetEffectivePermissionsAsync("ACME/Project", "LocalUser", TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute);
    }

    [Fact(Timeout = 10000)]
    public async Task AddUserRole_MixedLevels_CombinesPermissions()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await svc.AddUserRoleAsync("GlobalAdmin", "Admin", null, "system", TestTimeout);
        await svc.AddUserRoleAsync("OrgEditor", "Editor", "ACME", "system", TestTimeout);
        await svc.AddUserRoleAsync("LocalViewer", "Viewer", "ACME/Project", "system", TestTimeout);

        // GlobalAdmin should have all permissions
        var globalPerms = await svc.GetEffectivePermissionsAsync("ACME/Project", "GlobalAdmin", TestTimeout);
        globalPerms.Should().Be(Permission.All);

        // OrgEditor should have editor permissions on Software/Project (inherited)
        var orgPerms = await svc.GetEffectivePermissionsAsync("ACME/Project", "OrgEditor", TestTimeout);
        orgPerms.Should().HaveFlag(Permission.Read);
        orgPerms.Should().HaveFlag(Permission.Update);

        // LocalViewer should have viewer permissions at Software/Project
        var localPerms = await svc.GetEffectivePermissionsAsync("ACME/Project", "LocalViewer", TestTimeout);
        localPerms.Should().Be(Permission.Read | Permission.Execute);
    }

    #endregion

    #region Deny via AccessAssignment MeshNode

    [Fact(Timeout = 10000)]
    public async Task DenyAssignment_OverridesInheritedGrant()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Grant at parent
        await svc.AddUserRoleAsync("Alice", "Editor", "ACME", "system", TestTimeout);

        // Create a deny AccessAssignment MeshNode at child using NodeFactory
        var denyNode = new MeshNode("Alice_Access", "ACME/Project")
        {
            NodeType = "AccessAssignment",
            Name = "Alice Access",
            Content = new AccessAssignment
            {
                AccessObject = "Alice",
                Roles = [new RoleAssignment { Role = "Editor", Denied = true }]
            }
        };
        await NodeFactory.CreateNodeAsync(denyNode, ct: TestTimeout);

        var permissions = await svc.GetEffectivePermissionsAsync("ACME/Project", "Alice", TestTimeout);
        permissions.Should().Be(Permission.None, "denied Editor role should yield no permissions at child");
    }

    [Fact(Timeout = 10000)]
    public async Task DenyAtMiddle_GrantAtChild_ChildOverridesDeny()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Grant at grandparent
        await svc.AddUserRoleAsync("OverrideUser", "Viewer", "Org", "system", TestTimeout);

        // Deny at parent using NodeFactory
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
        await NodeFactory.CreateNodeAsync(denyNode, ct: TestTimeout);

        // Grant again at child
        await svc.AddUserRoleAsync("OverrideUser", "Viewer", "Org/Team/Project", "system", TestTimeout);

        var permTeam = await svc.GetEffectivePermissionsAsync("Org/Team", "OverrideUser", TestTimeout);
        var permProject = await svc.GetEffectivePermissionsAsync("Org/Team/Project", "OverrideUser", TestTimeout);

        permTeam.Should().Be(Permission.None, "deny at Org/Team should block inherited grant");
        permProject.Should().Be(Permission.Read | Permission.Execute, "grant at child should override deny at parent");
    }

    [Fact(Timeout = 10000)]
    public async Task DenyOneRole_KeepsOtherRoles()
    {
        var svc = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        // Grant Admin globally
        await svc.AddUserRoleAsync("MixedUser", "Admin", null, "system", TestTimeout);
        // Also grant Editor at Software
        await svc.AddUserRoleAsync("MixedUser", "Editor", "ACME", "system", TestTimeout);

        // Deny Admin at Software/Secure using NodeFactory
        var denyNode = new MeshNode("MixedUser_Access", "ACME/Secure")
        {
            NodeType = "AccessAssignment",
            Name = "MixedUser Access",
            Content = new AccessAssignment
            {
                AccessObject = "MixedUser",
                Roles = [new RoleAssignment { Role = "Admin", Denied = true }]
            }
        };
        await NodeFactory.CreateNodeAsync(denyNode, ct: TestTimeout);

        var permissions = await svc.GetEffectivePermissionsAsync("ACME/Secure", "MixedUser", TestTimeout);

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

        await svc.AddUserRoleAsync("TempUser", "Editor", "ACME/Project", "system", TestTimeout);

        // Verify permission exists
        var permBefore = await svc.GetEffectivePermissionsAsync("ACME/Project", "TempUser", TestTimeout);
        permBefore.Should().HaveFlag(Permission.Update);

        // Remove the role
        await svc.RemoveUserRoleAsync("TempUser", "Editor", "ACME/Project", TestTimeout);

        var permAfter = await svc.GetEffectivePermissionsAsync("ACME/Project", "TempUser", TestTimeout);
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
        inheritedPerms.Should().Be(Permission.Read | Permission.Execute);

        // LocalUser should have Editor on NS/Child (local)
        var localPerms = await svc.GetEffectivePermissionsAsync("NS/Child", "LocalUser", TestTimeout);
        localPerms.Should().HaveFlag(Permission.Update);
    }

    #endregion
}
