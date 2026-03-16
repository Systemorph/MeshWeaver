using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using Memex.Portal.Shared;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests that an admin user can create Organization nodes.
/// </summary>
public class OrganizationNodeCreationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddOrganizationType();

    [Fact(Timeout = 15000)]
    public async Task Admin_CanCreateOrganization()
    {
        // Arrange
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];
        var orgPath = orgId;

        var node = MeshNode.FromPath(orgPath) with
        {
            Name = "Test Organization",
            NodeType = OrganizationNodeType.NodeType
        };

        // Act
        var created = await NodeFactory.CreateNodeAsync(node, TestTimeout);

        // Assert
        created.Should().NotBeNull("Admin should be able to create Organization nodes");
        created.State.Should().Be(MeshNodeState.Active);
        created.Path.Should().Be(orgPath);
        created.NodeType.Should().Be("Organization");
        created.Name.Should().Be("Test Organization");
        Output.WriteLine($"Organization created at: {created.Path}");

        // Verify retrievable
        var fetched = await MeshQuery.QueryAsync<MeshNode>($"path:{orgPath}").FirstOrDefaultAsync();
        fetched.Should().NotBeNull("Created organization should be queryable");
        fetched!.NodeType.Should().Be("Organization");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(orgPath, ct: TestTimeout);
    }

    [Fact(Timeout = 30000)]
    public async Task Admin_CanCreateOrganizationWithContent()
    {
        // Arrange
        var orgId = $"ContentOrg_{Guid.NewGuid():N}"[..20];
        var orgPath = orgId;

        var orgContent = new Organization
        {
            Name = "Acme Corp",
            Description = "A test organization",
            Website = "https://acme.example.com",
            Location = "Switzerland",
            Email = "info@acme.example.com",
            IsVerified = true
        };

        var node = MeshNode.FromPath(orgPath) with
        {
            Name = "Acme Corp",
            NodeType = OrganizationNodeType.NodeType,
            Content = orgContent
        };

        // Act
        var created = await NodeFactory.CreateNodeAsync(node, TestTimeout);

        // Assert
        created.Should().NotBeNull();
        created.State.Should().Be(MeshNodeState.Active);
        created.NodeType.Should().Be("Organization");
        Output.WriteLine($"Organization with content created at: {created.Path}");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(orgPath, ct: TestTimeout);
    }
}
