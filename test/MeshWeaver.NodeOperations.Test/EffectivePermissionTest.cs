using System;
using System.Threading.Tasks;
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

    protected override Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", null))
            .Should().Within(45.Seconds()).Emit();
        return Task.CompletedTask;
    }

    [Fact(Timeout = 60000)]
    public void CreateSpace_HasPermission_ReturnsAdmin()
    {
        var spaceNode = MeshNode.FromPath("Systemorph") with
        {
            Name = "Systemorph",
            NodeType = SpaceNodeType.NodeType,
            Content = new Space { Name = "Systemorph" }
        };
        NodeFactory.CreateNode(spaceNode).Should().Within(45.Seconds()).Emit();

        var permissions = Mesh.GetEffectivePermissions("Systemorph", TestUsers.Admin.ObjectId)
            .Should().Within(90.Seconds()).Match(p => p != Permission.None);

        permissions.Should().NotBe(Permission.None,
            "Creator should have permissions from persisted AccessAssignment on the Space");
        permissions.Should().Be(Permission.All,
            "Admin role grants all permissions");
    }
}
