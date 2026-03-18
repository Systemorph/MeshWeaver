using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using Memex.Portal.Shared;
using MeshWeaver.Graph;
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
/// Tests that a global admin (claim-based Admin role) has full CRUD
/// permissions on Organization nodes. Uses base ConfigureMesh which
/// includes PublicAdminAccess — matching production behavior.
/// </summary>
public class GlobalAdminOrganizationCrudTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddOrganizationType()
            .AddSampleUsers();

    [Fact(Timeout = 30000)]
    public async Task GlobalAdmin_CanCreateOrganization()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Test Organization",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Test Organization" }
        };

        var created = await NodeFactory.CreateNodeAsync(orgNode, TestTimeout);

        created.Should().NotBeNull("Global admin should be able to create Organizations");
        created.State.Should().Be(MeshNodeState.Active);
        created.NodeType.Should().Be("Organization");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(orgId, ct: TestTimeout);
    }

    [Fact(Timeout = 30000)]
    public async Task GlobalAdmin_CanReadOrganization()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        // Create
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Test Organization",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Test Organization" }
        };
        await NodeFactory.CreateNodeAsync(orgNode, TestTimeout);

        // Read
        var found = await MeshQuery
            .QueryAsync<MeshNode>($"path:{orgId}")
            .FirstOrDefaultAsync(TestTimeout);

        found.Should().NotBeNull("Global admin should be able to read Organizations");
        found!.Name.Should().Be("Test Organization");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(orgId, ct: TestTimeout);
    }

    [Fact(Timeout = 30000)]
    public async Task GlobalAdmin_CanUpdateOrganization()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        // Create
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Original Name",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Original Name" }
        };
        await NodeFactory.CreateNodeAsync(orgNode, TestTimeout);

        // Update
        var updated = orgNode with
        {
            Name = "Updated Name",
            Content = new Organization { Name = "Updated Name", Description = "Updated description" }
        };
        await NodeFactory.UpdateNodeAsync(updated, TestTimeout);

        // Verify
        var found = await MeshQuery
            .QueryAsync<MeshNode>($"path:{orgId}")
            .FirstOrDefaultAsync(TestTimeout);

        found.Should().NotBeNull();
        found!.Name.Should().Be("Updated Name");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(orgId, ct: TestTimeout);
    }

    [Fact(Timeout = 30000)]
    public async Task GlobalAdmin_CanDeleteOrganization()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        // Create
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "To Delete",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "To Delete" }
        };
        await NodeFactory.CreateNodeAsync(orgNode, TestTimeout);

        // Delete
        await NodeFactory.DeleteNodeAsync(orgId, ct: TestTimeout);

        // Verify gone
        var found = await MeshQuery
            .QueryAsync<MeshNode>($"path:{orgId}")
            .FirstOrDefaultAsync(TestTimeout);

        found.Should().BeNull("Deleted organization should not be found");
    }

    [Fact(Timeout = 30000)]
    public async Task GlobalAdmin_HasAllPermissionsOnOrganization()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        // Create
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Permission Test Org",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Permission Test Org" }
        };
        await NodeFactory.CreateNodeAsync(orgNode, TestTimeout);

        // Check permissions
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var permissions = await securityService.GetEffectivePermissionsAsync(
            orgId, TestUsers.Admin.ObjectId, TestTimeout);

        Output.WriteLine($"Admin permissions on {orgId}: {permissions}");

        permissions.Should().HaveFlag(Permission.Read);
        permissions.Should().HaveFlag(Permission.Create);
        permissions.Should().HaveFlag(Permission.Update);
        permissions.Should().HaveFlag(Permission.Delete);

        // Also check root-level create permission (needed to create new organizations)
        var rootPermissions = await securityService.GetEffectivePermissionsAsync(
            "", TestUsers.Admin.ObjectId, TestTimeout);

        Output.WriteLine($"Admin permissions on root: {rootPermissions}");
        rootPermissions.Should().HaveFlag(Permission.Create,
            "Global admin should have Create permission at root level to create Organizations");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(orgId, ct: TestTimeout);
    }

    [Fact(Timeout = 30000)]
    public async Task GlobalAdmin_CanCreateNodeUnderOrganization()
    {
        var orgId = $"TestOrg_{Guid.NewGuid():N}"[..20];

        // Create Organization first
        var orgNode = MeshNode.FromPath(orgId) with
        {
            Name = "Test Organization",
            NodeType = OrganizationNodeType.NodeType,
            Content = new Organization { Name = "Test Organization" }
        };
        await NodeFactory.CreateNodeAsync(orgNode, TestTimeout);

        // Now create a Markdown child under the Organization
        var childNode = new MeshNode("TestPage", orgId)
        {
            Name = "Test Page",
            NodeType = "Markdown"
        };
        var created = await NodeFactory.CreateNodeAsync(childNode, TestTimeout);

        created.Should().NotBeNull("Admin should be able to create nodes under Organization");
        created.Path.Should().Be($"{orgId}/TestPage");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(orgId, ct: TestTimeout);
    }

    [Fact(Timeout = 30000)]
    public async Task GlobalAdmin_CanAccessCreateLayoutAreaOnOrganization()
    {
        // Reproduce: navigating to /Organization/Create shows "Access Denied"
        // because PermissionHelper runs on the Organization hub where user context
        // may be impersonated. The CreateLayoutArea fallback should match
        // OrganizationAccessRule by hub path "Organization".

        // 1. Admin has Create permission via ISecurityService
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var hasCreate = await securityService.HasPermissionAsync(
            "Organization", TestUsers.Admin.ObjectId, Permission.Create, TestTimeout);
        Output.WriteLine($"Admin has Create on 'Organization' via SecurityService: {hasCreate}");
        hasCreate.Should().BeTrue("Admin should have Create on Organization path");

        // 2. OrganizationAccessRule is registered and supports Create
        // (this is the fallback used by CreateLayoutArea when ISecurityService fails due to impersonation)
        var accessRules = Mesh.ServiceProvider.GetServices<INodeTypeAccessRule>();
        var orgRule = accessRules.FirstOrDefault(r =>
            r.NodeType.Equals("Organization", StringComparison.OrdinalIgnoreCase));

        orgRule.Should().NotBeNull("OrganizationAccessRule should be registered via DI");
        orgRule!.SupportedOperations.Should().Contain(NodeOperation.Create,
            "OrganizationAccessRule should support Create operation");

        // 3. The rule grants Create for admin user
        var ctx = new NodeValidationContext
        {
            Operation = NodeOperation.Create,
            Node = MeshNode.FromPath("TestOrg") with { NodeType = "Organization" }
        };
        var ruleAllows = await orgRule.HasAccessAsync(ctx, TestUsers.Admin.ObjectId, TestTimeout);
        Output.WriteLine($"OrganizationAccessRule.HasAccessAsync for Create: {ruleAllows}");
        ruleAllows.Should().BeTrue(
            "OrganizationAccessRule should allow Create for admin user");
    }

    [Fact(Timeout = 30000)]
    public async Task OrganizationType_IsVisibleToGlobalAdmin()
    {
        // Global admin should be able to see Organization in node types
        var nodeTypeService = Mesh.ServiceProvider.GetRequiredService<INodeTypeService>();
        var creatableTypes = await nodeTypeService
            .GetCreatableTypesAsync("", TestTimeout)
            .ToListAsync(TestTimeout);

        var typeNames = creatableTypes.Select(t => t.NodeTypePath).ToList();
        Output.WriteLine($"Root creatable types: {string.Join(", ", typeNames)}");

        typeNames.Should().Contain("Organization",
            "Organization should be a creatable type at root level for global admin");
    }

    [Fact(Timeout = 30000)]
    public async Task GlobalAdmin_CanReadOrganizationNodeType()
    {
        // The Organization NodeType definition node at path "Organization"
        // should be readable by an admin. This verifies the SubscribeRequest
        // to the Organization hub doesn't fail with "lacks Read permission".
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        var hasRead = await securityService.HasPermissionAsync(
            "Organization", TestUsers.Admin.ObjectId, Permission.Read, TestTimeout);

        Output.WriteLine($"Admin has Read on 'Organization': {hasRead}");
        hasRead.Should().BeTrue(
            "Global admin should have Read permission on the Organization NodeType hub path");
    }
}
