using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using Memex.Portal.Shared;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests that creating an Organization via normal CreateNodeRequest
/// implicitly creates the partition, grants admin access, and creates a markdown page.
/// </summary>
public class CreateOrganizationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddOrganizationType();

    [Fact(Timeout = 30000)]
    public async Task CreateOrganization_CreatesPartitionAndAdminAccess()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        // Act: normal CreateNodeAsync — post-creation handler does the rest
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Test Organization",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Test Organization" }
        };
        var created = await NodeFactory.CreateNodeAsync(orgNode, TestTimeout);

        // Assert: Organization node created
        created.Should().NotBeNull();
        created.State.Should().Be(MeshNodeState.Active);
        created.NodeType.Should().Be("Organization");
        created.Name.Should().Be("Test Organization");

        // Assert: Partition exists at Admin/Partition/{OrgId}
        var partitionPath = $"{PartitionNodeType.Namespace}/{orgId}";
        var partition = await MeshQuery.QueryAsync<MeshNode>($"path:{partitionPath}").FirstOrDefaultAsync(TestTimeout);
        partition.Should().NotBeNull("Partition should be created by post-creation handler");
        partition!.NodeType.Should().Be("Partition");

        // Assert: Creator has Admin permissions on the org namespace
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var hasAdmin = await securityService.HasPermissionAsync(
            orgId, TestUsers.Admin.ObjectId, Permission.Update, TestTimeout);
        hasAdmin.Should().BeTrue("Creator should have Admin permissions on the organization");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(orgId, ct: TestTimeout);
    }
}
