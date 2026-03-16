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
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests that an Organization created via normal CreateNodeRequest
/// grants the creator Admin permissions (Create, Update, Delete)
/// and that standard node types (Markdown, Thread, Agent) are creatable.
/// </summary>
public class OrganizationMenuAndAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGraph()
            .AddOrganizationType()
            .AddSampleUsers();

    [Fact(Timeout = 30000)]
    public async Task AdminCreator_HasFullPermissions()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        // Create Organization
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Test Organization",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Test Organization" }
        };
        var created = await NodeFactory.CreateNodeAsync(orgNode, TestTimeout);
        created.Should().NotBeNull();

        // Check effective permissions for the creator (admin user from TestBase)
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var permissions = await securityService.GetEffectivePermissionsAsync(
            orgId, TestUsers.Admin.ObjectId, TestTimeout);

        Output.WriteLine($"Effective permissions on {orgId}: {permissions}");

        permissions.Should().HaveFlag(Permission.Read, "Admin should have Read permission");
        permissions.Should().HaveFlag(Permission.Create, "Admin should have Create permission");
        permissions.Should().HaveFlag(Permission.Update, "Admin should have Update permission");
        permissions.Should().HaveFlag(Permission.Delete, "Admin should have Delete permission");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(orgId, ct: TestTimeout);
    }

    [Fact(Timeout = 30000)]
    public async Task Organization_HasStandardCreatableTypes()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        // Create Organization
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Test Organization",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Test Organization" }
        };
        await NodeFactory.CreateNodeAsync(orgNode, TestTimeout);

        // Check creatable types for the organization
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();
        var creatableTypes = await nodeTypeService.GetCreatableTypesAsync(orgId, TestTimeout).ToListAsync(TestTimeout);
        var typeNames = creatableTypes.Select(t => t.NodeTypePath).ToList();

        Output.WriteLine($"Creatable types at {orgId}: {string.Join(", ", typeNames)}");

        typeNames.Should().Contain("Markdown", "Markdown should be creatable under Organization");
        typeNames.Should().Contain("Thread", "Thread should be creatable under Organization");
        typeNames.Should().Contain("Agent", "Agent should be creatable under Organization");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(orgId, ct: TestTimeout);
    }

    [Fact(Timeout = 30000)]
    public async Task Organization_ChildrenAreQueryable()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        // Create Organization (post-creation handler creates Overview markdown page)
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Test Organization",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Test Organization" }
        };
        await NodeFactory.CreateNodeAsync(orgNode, TestTimeout);

        // Query children under the organization namespace
        var children = await MeshQuery
            .QueryAsync<MeshNode>($"namespace:{orgId} is:main")
            .ToListAsync(TestTimeout);

        Output.WriteLine($"Children under {orgId}: {string.Join(", ", children.Select(c => $"{c.Path} ({c.NodeType})"))}");

        // The Overview markdown page should be a child
        children.Should().Contain(c => c.NodeType == "Markdown" && c.Path == $"{orgId}/Overview",
            "Overview markdown page should exist as child");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(orgId, ct: TestTimeout);
    }
}
