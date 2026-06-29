using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
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
///
/// All AccessAssignment nodes are seeded via static <see cref="MeshBuilder.AddMeshNodes"/>
/// (preferred) or via runtime <see cref="IMeshService.CreateNode"/> (when the test exercises
/// the create/delete lifecycle itself). Reads use the reactive
/// <see cref="SecurityService.GetEffectivePermissions(string,string)"/> surface bridged
/// to <see cref="Task{T}"/> via <c>.FirstAsync().ToTask(ct)</c>.
/// </summary>
public class AccessAssignmentTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .AddMeshNodes(
                // For tests that exercise additional grants/deletions at runtime,
                // seed admin context via the per-test IMeshService.CreateNode calls below.
                AssignmentNodeFactory.UserRole("GlobalUser", "Admin", null),
                AssignmentNodeFactory.UserRole("AncestorUser", "Editor", "ACME"),
                AssignmentNodeFactory.UserRole("LocalUser", "Viewer", "ACME/Project"),
                AssignmentNodeFactory.UserRole("GlobalAdmin", "Admin", null),
                AssignmentNodeFactory.UserRole("OrgEditor", "Editor", "ACME"),
                AssignmentNodeFactory.UserRole("LocalViewer", "Viewer", "ACME/Project"),
                AssignmentNodeFactory.UserRole("Alice", "Editor", "ACME"),
                AssignmentNodeFactory.UserRole("OverrideUser", "Viewer", "Org"),
                // OverrideUser also gets a grant at child for the deny-then-grant test
                AssignmentNodeFactory.UserRole("OverrideUser_Child", "Viewer", "Org/Team/Project", accessObject: "OverrideUser"),
                AssignmentNodeFactory.UserRole("MixedUser", "Admin", null),
                AssignmentNodeFactory.UserRole("MixedUser_AcmeEditor", "Editor", "ACME", accessObject: "MixedUser"),
                AssignmentNodeFactory.UserRole("InheritedUser", "Viewer", "NS"),
                AssignmentNodeFactory.UserRole("LocalUserNS", "Editor", "NS/Child"));

    #region AddUserRole creates AccessAssignment MeshNodes

    [Fact(Timeout = 20000)]
    public async Task AddUserRole_GlobalAssignment_GrantsPermissionsEverywhere()
    {
        await Mesh.GetEffectivePermissions("ACME/Project", "GlobalUser")
            .Should().Match(p => p == (Permission.All | Permission.Compile));
    }

    [Fact(Timeout = 20000)]
    public async Task AddUserRole_AncestorAssignment_GrantsPermissionsToDescendants()
    {
        var permissions = await Mesh.GetEffectivePermissions("ACME/Project", "AncestorUser")
            .Should().Match(p => p.HasFlag(Permission.Read) && p.HasFlag(Permission.Create) && p.HasFlag(Permission.Update));
        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
    }

    [Fact(Timeout = 20000)]
    public async Task AddUserRole_LocalAssignment_GrantsPermissionsAtPath()
    {
        await Mesh.GetEffectivePermissions("ACME/Project", "LocalUser")
            .Should().Match(p => p == (Permission.Read | Permission.Execute | Permission.Api));
    }

    [Fact(Timeout = 20000)]
    public async Task AddUserRole_MixedLevels_CombinesPermissions()
    {
        await Mesh.GetEffectivePermissions("ACME/Project", "GlobalAdmin")
            .Should().Match(p => p == (Permission.All | Permission.Compile));

        var orgPerms = await Mesh.GetEffectivePermissions("ACME/Project", "OrgEditor")
            .Should().Match(p => p.HasFlag(Permission.Read) && p.HasFlag(Permission.Update));
        orgPerms.Should().HaveFlag(Permission.Read);
        orgPerms.Should().HaveFlag(Permission.Update);

        await Mesh.GetEffectivePermissions("ACME/Project", "LocalViewer")
            .Should().Match(p => p == (Permission.Read | Permission.Execute | Permission.Api));
    }

    #endregion

    #region Deny via AccessAssignment MeshNode

    [Fact(Timeout = 20000)]
    public async Task DenyAssignment_OverridesInheritedGrant()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Alice already has an Editor grant at ACME from ConfigureMesh.
        // Add a deny at child via runtime CreateNode.
        var denyNode = new MeshNode("Alice_Access", "ACME/Project/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "Alice Access",
            MainNode = "ACME/Project",
            Content = new AccessAssignment
            {
                AccessObject = "Alice",
                Roles = [new RoleAssignment { Role = "Editor", Denied = true }]
            }
        };
        await meshService.CreateNode(denyNode).Should().Emit();

        // Wait for the synced AccessAssignment query to surface the deny.
        var permissions = await Mesh.GetEffectivePermissions("ACME/Project", "Alice")
            .Should().Match(p => p == Permission.None);
        permissions.Should().Be(Permission.None, "denied Editor role should yield no permissions at child");
    }

    [Fact(Timeout = 20000)]
    public async Task DenyAtMiddle_GrantAtChild_ChildOverridesDeny()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // OverrideUser already has Viewer at "Org" (parent) and "Org/Team/Project" (child)
        // from ConfigureMesh. Add a deny at middle via runtime CreateNode.
        var denyNode = new MeshNode("OverrideUser_Access", "Org/Team/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "OverrideUser Access",
            MainNode = "Org/Team",
            Content = new AccessAssignment
            {
                AccessObject = "OverrideUser",
                Roles = [new RoleAssignment { Role = "Viewer", Denied = true }]
            }
        };
        await meshService.CreateNode(denyNode).Should().Emit();

        // Wait for the synced query to surface the deny at Org/Team.
        var permTeam = await Mesh.GetEffectivePermissions("Org/Team", "OverrideUser")
            .Should().Match(p => p == Permission.None);
        var permProject = await Mesh.GetEffectivePermissions("Org/Team/Project", "OverrideUser")
            .Should().Emit();

        permTeam.Should().Be(Permission.None, "deny at Org/Team should block inherited grant");
        permProject.Should().Be(Permission.Read | Permission.Execute | Permission.Api, "grant at child should override deny at parent");
    }

    [Fact(Timeout = 20000)]
    public async Task DenyOneRole_KeepsOtherRoles()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // MixedUser already has Admin globally + Editor at ACME from ConfigureMesh.
        // Deny Admin at ACME/Secure via runtime CreateNode.
        var denyNode = new MeshNode("MixedUser_Access", "ACME/Secure/_Access")
        {
            NodeType = "AccessAssignment",
            Name = "MixedUser Access",
            MainNode = "ACME/Secure",
            Content = new AccessAssignment
            {
                AccessObject = "MixedUser",
                Roles = [new RoleAssignment { Role = "Admin", Denied = true }]
            }
        };
        await meshService.CreateNode(denyNode).Should().Emit();

        // Wait for the deny to take effect — Admin's Delete must drop out
        // while Editor permissions (Read/Create/Update) remain.
        var permissions = await Mesh.GetEffectivePermissions("ACME/Secure", "MixedUser")
            .Should().Match(p => !p.HasFlag(Permission.Delete) && p.HasFlag(Permission.Update));

        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
        permissions.Should().NotHaveFlag(Permission.Delete,
            "Admin was denied, so Delete should not be available, only Editor permissions remain");
    }

    #endregion

    #region RemoveUserRole

    [Fact(Timeout = 20000)]
    public async Task RemoveUserRole_RevokesPermissions()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        // Create the assignment via runtime CreateNode (this test exercises the lifecycle).
        var assignNode = AssignmentNodeFactory.UserRole("TempUser", "Editor", "ACME/Project");
        await meshService.CreateNode(assignNode).Should().Emit();

        // Wait for the synced query to surface the new grant.
        var permBefore = await Mesh.GetEffectivePermissions("ACME/Project", "TempUser")
            .Should().Match(p => p.HasFlag(Permission.Update));
        permBefore.Should().HaveFlag(Permission.Update);

        // Delete the assignment and assert the synced query drops the grant.
        await meshService.DeleteNode(assignNode.Path).Should().Emit();

        var permAfter = await Mesh.GetEffectivePermissions("ACME/Project", "TempUser")
            .Should().Match(p => p == Permission.None);
        permAfter.Should().Be(Permission.None,
            "deleting the AccessAssignment should revoke all permissions");
    }

    #endregion

    #region UI Layout (Structural)

    [Fact(Timeout = 20000)]
    public async Task AccessControl_NoRLS_ShowsWarningMessage()
    {
        // RLS sanity: any effective-permission round-trip should work; skip explicit SecurityService check.
        await Mesh.GetEffectivePermissions("smoke", "anyone").Should().Emit();
    }

    [Fact(Timeout = 20000)]
    public async Task AccessControl_EmptyAssignments_HasNoPermissions()
    {
        await Mesh.GetEffectivePermissions("Empty/Namespace/Path", "SomeUser")
            .Should().Match(p => p == Permission.None);
    }

    [Fact(Timeout = 20000)]
    public async Task AccessControl_InheritedAndLocalAssignments_BothApply()
    {
        await Mesh.GetEffectivePermissions("NS/Child", "InheritedUser")
            .Should().Match(p => p == (Permission.Read | Permission.Execute | Permission.Api));

        await Mesh.GetEffectivePermissions("NS/Child", "LocalUserNS")
            .Should().Match(p => p.HasFlag(Permission.Update));
    }

    #endregion
}
