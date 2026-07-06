using System;
using System.Threading.Tasks;
using MeshWeaver.Graph;
using MeshWeaver.Blazor.Portal;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests that GetEffectivePermissionsAsync correctly finds persisted AccessAssignment nodes.
/// Protocol:
/// 1) Install SpaceNodeType
/// 2) Create Space "Systemorph"
/// 3) Ask SecurityService.HasPermission → should NOT return None
/// </summary>
public class EffectivePermissionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddSpaceType();

    protected override async Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", null))
            .Should().Within(45.Seconds()).Emit();
    }

    [Fact(Timeout = 60000)]
    public async Task CreateSpace_HasPermission_ReturnsAdmin()
    {
        var spaceNode = MeshNode.FromPath("Systemorph") with
        {
            Name = "Systemorph",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space()
        };
        await NodeFactory.CreateNode(spaceNode).Should().Within(45.Seconds()).Emit();

        var permissions = await Mesh.GetEffectivePermissions("Systemorph", TestUsers.Admin.ObjectId)
            .Should().Within(90.Seconds()).Match(p => p != Permission.None);

        permissions.Should().NotBe(Permission.None,
            "Creator should have permissions from persisted AccessAssignment on the Space");
        permissions.Should().Be(Permission.All | Permission.Compile,
            "Admin role grants all permissions plus the explicit Compile grant "
            + "(Compile is excluded from Permission.All and added explicitly to the built-in roles)");
    }

    /// <summary>
    /// A <see cref="PartitionAccessPolicy"/> with <c>PublicRead = true</c> grants Read to
    /// a user who holds NO role at that scope — the policy-driven public-read override
    /// (precedence over the per-user roles ∩ cap). This is how the built-in Agent / Model /
    /// Documentation catalogs become world-readable without a per-user grant; the cold-start
    /// "No suitable agent" bug was the agent catalog lacking exactly this.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PublicReadPolicy_GrantsReadOnly_ToUserWithoutRole()
    {
        // World-readable, write-capped namespace via a PublicRead _Policy.
        await NodeFactory.CreateNode(MeshNode.FromPath("PublicCatalog/_Policy") with
        {
            Name = "Catalog Access Policy",
            NodeType = "PartitionAccessPolicy",
            Content = new PartitionAccessPolicy
            {
                PublicRead = true, Create = false, Update = false, Delete = false
            }
        }).Should().Within(45.Seconds()).Emit();

        await NodeFactory.CreateNode(MeshNode.FromPath("PublicCatalog/Item1") with
        {
            Name = "Item 1", NodeType = "Markdown"
        }).Should().Within(45.Seconds()).Emit();

        // A user with NO role anywhere near PublicCatalog.
        const string strangerId = "stranger@example.com";
        var perms = await Mesh.GetEffectivePermissions("PublicCatalog/Item1", strangerId)
            .Should().Within(60.Seconds()).Match(p => p.HasFlag(Permission.Read));

        perms.Should().HaveFlag(Permission.Read,
            "PublicRead policy grants Read to any user, even with no role at the scope");
        perms.Should().NotHaveFlag(Permission.Create,
            "PublicRead grants ONLY Read — it is not a blanket grant");
        perms.Should().NotHaveFlag(Permission.Update,
            "PublicRead grants ONLY Read — writes stay denied");
    }

    /// <summary>
    /// Control: the SAME role-less user gets <see cref="Permission.None"/> on a namespace
    /// WITHOUT a PublicRead policy — proving the grant comes from PublicRead, not from
    /// some default-allow.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task WithoutPublicRead_RolelessUser_GetsNone()
    {
        await NodeFactory.CreateNode(MeshNode.FromPath("PrivateCatalog/Item1") with
        {
            Name = "Item 1", NodeType = "Markdown"
        }).Should().Within(45.Seconds()).Emit();

        const string strangerId = "stranger@example.com";
        // No PublicRead policy here → a role-less user is denied everything.
        await Mesh.GetEffectivePermissions("PrivateCatalog/Item1", strangerId)
            .Should().Within(60.Seconds()).Match(p => p == Permission.None);
    }
}
