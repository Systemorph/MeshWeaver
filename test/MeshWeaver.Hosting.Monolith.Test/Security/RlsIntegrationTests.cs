using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test.Security;

/// <summary>
/// Integration tests for Row-Level Security with actual CRUD operations.
/// These tests verify the permission evaluation and validator behavior.
/// </summary>
public class RlsIntegrationTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // First configure base (adds persistence), then add Row-Level Security
        // RLS must be added after persistence so it can decorate IPersistenceService
        var configured = base.ConfigureMesh(builder).AddRowLevelSecurity();

        // Register node types as MeshNodes
        configured.AddMeshNodes(new MeshNode("secure") { Name = "Secure Type" });

        return configured;
    }

    [Fact]
    public async Task CreateNode_WithCreatePermission_Succeeds()
    {
        // Arrange
        var client = GetClient();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string userId = "creator";
        const string parentPath = "rls/create";

        // Assign Editor role (has Create permission) to the parent path
        await securityService.AddUserRoleAsync(userId, "Editor", parentPath, "system", TestTimeout);

        // Create node with CreatedBy set to the authorized user
        var node = new MeshNode("NewNode", parentPath)
        {
            Name = "Created Node",
            NodeType = "secure"
        };
        var request = new CreateNodeRequest(node) { CreatedBy = userId };

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeTrue();
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.Name.Should().Be("Created Node");
    }

    [Fact]
    public async Task CreateNode_WithoutPermission_Fails()
    {
        // Arrange
        var client = GetClient();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string userId = "reader";
        const string parentPath = "rls/readonly";

        // Assign Viewer role (only Read permission, no Create)
        await securityService.AddUserRoleAsync(userId, "Viewer", parentPath, "system", TestTimeout);

        var node = new MeshNode("FailedNode", parentPath)
        {
            Name = "Should Fail",
            NodeType = "secure"
        };
        var request = new CreateNodeRequest(node) { CreatedBy = userId };

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeFalse();
        response.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
    }

    [Fact]
    public async Task CreateNode_WithNoRoleAssigned_Fails()
    {
        // Arrange
        var client = GetClient();
        const string userId = "unknown";

        var node = new MeshNode("UnauthorizedNode", "rls/noauth")
        {
            Name = "Unauthorized",
            NodeType = "secure"
        };
        var request = new CreateNodeRequest(node) { CreatedBy = userId };

        // Act
        var response = await client.AwaitResponse(
            request,
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeFalse();
        response.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
    }

    [Fact]
    public async Task DeleteNode_WithDeletePermission_Succeeds()
    {
        // Arrange
        var client = GetClient();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string adminId = "deletor";
        const string parentPath = "rls/delete";

        // Assign Admin role (has all permissions including Delete)
        await securityService.AddUserRoleAsync(adminId, "Admin", parentPath, "system", TestTimeout);

        // Create a node first
        var node = new MeshNode("ToDelete", parentPath)
        {
            Name = "To Be Deleted",
            NodeType = "secure"
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node) { CreatedBy = adminId },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - delete the node using DeletedBy
        var deleteResponse = await client.AwaitResponse(
            new DeleteNodeRequest("rls/delete/ToDelete") { DeletedBy = adminId },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        deleteResponse.Message.Success.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteNode_WithoutPermission_Fails()
    {
        // Arrange
        var client = GetClient();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string adminId = "admin";
        const string viewerId = "viewer";
        const string parentPath = "rls/nodelete";

        // Create node as admin
        await securityService.AddUserRoleAsync(adminId, "Admin", parentPath, "system", TestTimeout);
        var node = new MeshNode("Protected", parentPath)
        {
            Name = "Protected Node",
            NodeType = "secure"
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node) { CreatedBy = adminId },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Assign Viewer role (no Delete permission)
        await securityService.AddUserRoleAsync(viewerId, "Viewer", parentPath, "system", TestTimeout);

        // Act - viewer tries to delete
        var deleteResponse = await client.AwaitResponse(
            new DeleteNodeRequest("rls/nodelete/Protected") { DeletedBy = viewerId },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert - Should fail due to insufficient permissions
        deleteResponse.Message.Success.Should().BeFalse();
        deleteResponse.Message.RejectionReason.Should().Be(NodeDeletionRejectionReason.ValidationFailed);
    }

    [Fact]
    public async Task UpdateNode_WithUpdatePermission_Succeeds()
    {
        // Arrange
        var client = GetClient();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string editorId = "editor";
        const string parentPath = "rls/update";

        // Assign Editor role (has Create and Update permissions)
        await securityService.AddUserRoleAsync(editorId, "Editor", parentPath, "system", TestTimeout);

        // Create a node first
        var node = new MeshNode("ToUpdate", parentPath)
        {
            Name = "Original Name",
            NodeType = "secure"
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node) { CreatedBy = editorId },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Act - update the node using UpdatedBy
        var updatedNode = node with { Name = "Updated Name" };
        var updateResponse = await client.AwaitResponse(
            new UpdateNodeRequest(updatedNode) { UpdatedBy = editorId },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        updateResponse.Message.Success.Should().BeTrue();
        updateResponse.Message.Node!.Name.Should().Be("Updated Name");
    }

    [Fact]
    public async Task UpdateNode_WithoutPermission_Fails()
    {
        // Arrange
        var client = GetClient();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string adminId = "admin";
        const string viewerId = "viewer";
        const string parentPath = "rls/noupdate";

        // Create node as admin
        await securityService.AddUserRoleAsync(adminId, "Admin", parentPath, "system", TestTimeout);
        var node = new MeshNode("NoUpdate", parentPath)
        {
            Name = "Cannot Update",
            NodeType = "secure"
        };
        var createResponse = await client.AwaitResponse(
            new CreateNodeRequest(node) { CreatedBy = adminId },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        createResponse.Message.Success.Should().BeTrue();

        // Assign Viewer role (no Update permission)
        await securityService.AddUserRoleAsync(viewerId, "Viewer", parentPath, "system", TestTimeout);

        // Act - viewer tries to update
        var updatedNode = node with { Name = "Trying to Update" };
        var updateResponse = await client.AwaitResponse(
            new UpdateNodeRequest(updatedNode) { UpdatedBy = viewerId },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        updateResponse.Message.Success.Should().BeFalse();
        updateResponse.Message.RejectionReason.Should().Be(NodeUpdateRejectionReason.ValidationFailed);
    }

    [Fact]
    public async Task HierarchicalPermission_InheritsFromParent()
    {
        // Arrange
        var client = GetClient();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string userId = "hierarchyuser";
        const string topLevelPath = "rls";
        const string parentPath = "rls/parent";

        // Assign Admin role at top level (rls) so user can create anywhere under rls/
        await securityService.AddUserRoleAsync(userId, "Admin", topLevelPath, "system", TestTimeout);

        // Create parent node first
        var parentNode = new MeshNode("parent", topLevelPath)
        {
            Name = "Parent Node",
            NodeType = "secure"
        };
        var parentResponse = await client.AwaitResponse(
            new CreateNodeRequest(parentNode) { CreatedBy = userId },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);
        parentResponse.Message.Success.Should().BeTrue();

        // Act - create child node (should inherit parent permissions)
        var childNode = new MeshNode("child", parentPath)
        {
            Name = "Child Node",
            NodeType = "secure"
        };
        var childResponse = await client.AwaitResponse(
            new CreateNodeRequest(childNode) { CreatedBy = userId },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert - child creation should succeed due to inherited permissions
        childResponse.Message.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GlobalAdmin_HasAccessEverywhere()
    {
        // Arrange
        var client = GetClient();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string globalAdminId = "globaladmin";

        // Assign global Admin role (null path = applies everywhere)
        await securityService.AddUserRoleAsync(globalAdminId, "Admin", null!, "system", TestTimeout);

        // Act - create nodes in different paths
        var node1 = new MeshNode("GlobalTest1", "random/path")
        {
            Name = "Global Test 1",
            NodeType = "secure"
        };
        var response1 = await client.AwaitResponse(
            new CreateNodeRequest(node1) { CreatedBy = globalAdminId },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        var node2 = new MeshNode("GlobalTest2", "another/random/path")
        {
            Name = "Global Test 2",
            NodeType = "secure"
        };
        var response2 = await client.AwaitResponse(
            new CreateNodeRequest(node2) { CreatedBy = globalAdminId },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert - global admin should be able to create anywhere
        response1.Message.Success.Should().BeTrue();
        response2.Message.Success.Should().BeTrue();
    }

    [Fact]
    public async Task MultipleRoles_CombinePermissions()
    {
        // Arrange
        var client = GetClient();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string userId = "multirole";
        const string path1 = "rls/multi/project1";
        const string path2 = "rls/multi/project2";

        // Assign different roles at different paths
        await securityService.AddUserRoleAsync(userId, "Viewer", path1, "system", TestTimeout);
        await securityService.AddUserRoleAsync(userId, "Editor", path2, "system", TestTimeout);

        // Act - check permissions at each path
        var permissions1 = await securityService.GetEffectivePermissionsAsync(path1, userId, TestTimeout);
        var permissions2 = await securityService.GetEffectivePermissionsAsync(path2, userId, TestTimeout);

        // Assert
        permissions1.Should().Be(Permission.Read); // Viewer only
        permissions2.Should().Be(Permission.Read | Permission.Create | Permission.Update); // Editor
    }
}

/// <summary>
/// Tests for the SecurePersistenceServiceDecorator.
/// </summary>
public class SecurePersistenceDecoratorTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;
    private JsonSerializerOptions _jsonOptions => Mesh.ServiceProvider.GetRequiredService<IMessageHub>().JsonSerializerOptions;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // First configure base (adds persistence), then add Row-Level Security
        // RLS must be added after persistence so it can decorate IPersistenceService
        return base.ConfigureMesh(builder).AddRowLevelSecurity();
    }

    [Fact]
    public async Task SecureDecorator_CanBeCreated()
    {
        // Arrange
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceServiceCore>();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILogger<SecurePersistenceServiceDecorator>>();

        // Act
        var decorator = new SecurePersistenceServiceDecorator(persistence, new Lazy<ISecurityService>(() => securityService), logger);

        // Assert
        decorator.Should().NotBeNull();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetNodeSecureAsync_WithPermission_ReturnsNode()
    {
        // Arrange
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceServiceCore>();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILogger<SecurePersistenceServiceDecorator>>();
        var decorator = new SecurePersistenceServiceDecorator(persistence, new Lazy<ISecurityService>(() => securityService), logger);

        const string userId = "secureReader";
        const string nodePath = "secure/test/node";

        // Create a node directly in persistence
        var node = new MeshNode("node", "secure/test")
        {
            Name = "Secure Node",
            State = MeshNodeState.Active
        };
        await persistence.SaveNodeAsync(node, _jsonOptions, TestTimeout);

        // Assign read permission
        await securityService.AddUserRoleAsync(userId, "Viewer", "secure/test", "system", TestTimeout);

        // Act
        var result = await decorator.GetNodeSecureAsync(nodePath, userId, _jsonOptions, TestTimeout);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Secure Node");
    }

    [Fact]
    public async Task GetNodeSecureAsync_WithoutPermission_ReturnsNull()
    {
        // Arrange
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceServiceCore>();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILogger<SecurePersistenceServiceDecorator>>();
        var decorator = new SecurePersistenceServiceDecorator(persistence, new Lazy<ISecurityService>(() => securityService), logger);

        const string userId = "noAccess";
        const string nodePath = "restricted/hidden/node";

        // Create a node directly in persistence
        var node = new MeshNode("node", "restricted/hidden")
        {
            Name = "Hidden Node",
            State = MeshNodeState.Active
        };
        await persistence.SaveNodeAsync(node, _jsonOptions, TestTimeout);

        // No permission assigned

        // Act
        var result = await decorator.GetNodeSecureAsync(nodePath, userId, _jsonOptions, TestTimeout);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetChildrenSecureAsync_FiltersUnauthorizedNodes()
    {
        // Arrange
        var persistence = Mesh.ServiceProvider.GetRequiredService<IPersistenceServiceCore>();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var logger = Mesh.ServiceProvider.GetRequiredService<ILogger<SecurePersistenceServiceDecorator>>();
        var decorator = new SecurePersistenceServiceDecorator(persistence, new Lazy<ISecurityService>(() => securityService), logger);

        const string userId = "partialAccess";
        const string parentPath = "filter/test";

        // Create multiple nodes directly in persistence
        var node1 = new MeshNode("accessible", parentPath)
        {
            Name = "Accessible Node",
            State = MeshNodeState.Active
        };
        var node2 = new MeshNode("restricted", parentPath)
        {
            Name = "Restricted Node",
            State = MeshNodeState.Active
        };
        await persistence.SaveNodeAsync(node1, _jsonOptions, TestTimeout);
        await persistence.SaveNodeAsync(node2, _jsonOptions, TestTimeout);

        // Only grant access to node1's subtree
        await securityService.AddUserRoleAsync(userId, "Viewer", "filter/test/accessible", "system", TestTimeout);

        // Act
        var children = await decorator.GetChildrenSecureAsync(parentPath, userId, _jsonOptions).ToListAsync();

        // Assert - Should only include the accessible node
        children.Should().ContainSingle(n => n.Name == "Accessible Node");
        children.Should().NotContain(n => n.Name == "Restricted Node");
    }
}
