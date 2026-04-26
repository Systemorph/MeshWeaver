using System;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using Memex.Portal.Shared;
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
/// 1) Install OrganizationNodeType
/// 2) Create Organization "Systemorph"
/// 3) Ask ISecurityService.HasPermission → should NOT return None
/// </summary>
public class EffectivePermissionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddOrganizationType();

    protected override async Task SetupAccessRightsAsync()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin", null)).FirstAsync().ToTask(TestTimeout);
    }

    [Fact(Timeout = 60000)]
    public async Task CreateOrganization_HasPermission_ReturnsAdmin()
    {
        // 1) OrganizationNodeType installed via ConfigureMesh

        // 2) Create Organization "Systemorph"
        var orgNode = MeshNode.FromPath("Systemorph") with
        {
            Name = "Systemorph",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Systemorph" }
        };
        await NodeFactory.CreateNode(orgNode);

        // 3) Ask ISecurityService.HasPermission for this node
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var permissions = await Mesh.GetPermissionAsync(
            "Systemorph", TestUsers.Admin.ObjectId, TestTimeout);

        permissions.Should().NotBe(Permission.None,
            "Creator should have permissions from persisted AccessAssignment on the Organization");
        permissions.Should().Be(Permission.All,
            "Admin role grants all permissions");
    }
}
