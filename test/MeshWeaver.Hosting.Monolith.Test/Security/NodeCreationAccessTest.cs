using System;
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
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test.Security;

/// <summary>
/// Tests for node creation access control, specifically testing:
/// 1. CreateNode without permission returns Unauthorized
/// 2. CreateNode with permission creates transient node
/// 3. Id change during creation creates new node and deletes transient
/// </summary>
public class NodeCreationAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(15.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // Add Row-Level Security for access control testing
        // Add graph configuration to register NodeTypes like "Markdown"
        return ConfigureMeshBase(builder)
            .AddRowLevelSecurity();
    }

    /// <summary>
    /// Tests that creating a node without Create permission throws UnauthorizedAccessException.
    /// The RlsNodeValidator should check permission on the parent path.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CreateNode_WithoutPermission_ThrowsUnauthorized()
    {
        // Arrange — switch to unauthorized user who has NO permissions
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var unauthorizedUser = new AccessContext { ObjectId = "unauthorized-user", Name = "Unauthorized" };
        accessService.SetContext(unauthorizedUser);
        accessService.SetCircuitContext(unauthorizedUser);

        const string parentPath = "Restricted/Parent";
        var nodePath = $"{parentPath}/TestNode_{Guid.NewGuid().AsString()}";

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Test Node",
            NodeType = "Markdown"
        };

        try
        {
            // Act & Assert - CreateNodeAsync should throw UnauthorizedAccessException
            var act = async () => await NodeFactory.CreateNodeAsync(node, TestTimeout);
            var exception = await act.Should().ThrowAsync<UnauthorizedAccessException>();
            exception.Which.Message.Should().Contain("Access denied", "Should indicate authorization failure");
            Output.WriteLine($"Exception thrown as expected: {exception.Which.Message}");
        }
        finally
        {
            // Restore admin context
            TestUsers.DevLogin(Mesh);
        }
    }

    /// <summary>
    /// Tests that creating a node with Create permission succeeds via IMeshService.CreateNodeAsync.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CreateNode_WithPermission_Succeeds()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string userId = "authorized-user";
        const string parentPath = "Authorized/Parent";
        var nodeId = $"TestNode_{Guid.NewGuid().AsString()}";
        var nodePath = $"{parentPath}/{nodeId}";

        // Grant Editor role (includes Create permission)
        await securityService.AddUserRoleAsync(userId, "Editor", parentPath, "system", TestTimeout);

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Test Node With Permission",
            NodeType = "Markdown",
            DesiredId = "MyDesiredId" // User's intended final Id
        };

        // Act - Use public CreateNodeAsync which goes through message-based validation
        var createdNode = await NodeFactory.CreateNodeAsync(node, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull("Node should be created");
        createdNode.State.Should().Be(MeshNodeState.Active, "Node should be in Active state");
        createdNode.Path.Should().Be(nodePath, "Node should be at the specified path");
        createdNode.Name.Should().Be("Test Node With Permission");
        createdNode.DesiredId.Should().Be("MyDesiredId", "DesiredId should be preserved");

        // Verify node exists via query
        var fetchedNode = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath} scope:exact").FirstOrDefaultAsync();
        fetchedNode.Should().NotBeNull("Node should be retrievable from query");
        fetchedNode!.State.Should().Be(MeshNodeState.Active);

        // Cleanup
        await NodeFactory.DeleteNodeAsync(nodePath, ct: TestTimeout);
    }

    /// <summary>
    /// Tests the Id change flow: when user changes Id during creation,
    /// a new node is created at the new path and the transient node is deleted.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CreateNode_IdChanged_CreatesNewNodeAndDeletesTransient()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string userId = "editor-user";
        const string parentNamespace = "Editor/Projects";
        var transientId = Guid.NewGuid().AsString();
        var desiredId = "MyNewProject";
        var transientPath = $"{parentNamespace}/{transientId}";
        var finalPath = $"{parentNamespace}/{desiredId}";

        // Grant Editor role
        await securityService.AddUserRoleAsync(userId, "Editor", parentNamespace, "system", TestTimeout);

        // Step 1: Create transient node with GUID-based Id
        var transientNode = MeshNode.FromPath(transientPath) with
        {
            Name = "My New Project",
            NodeType = "Markdown",
            State = MeshNodeState.Transient,
            DesiredId = desiredId // User wants this as final Id
        };

        var createdTransient = await NodeFactory.CreateTransientAsync(transientNode, TestTimeout);
        createdTransient.Should().NotBeNull("Transient node should be created");
        Output.WriteLine($"Transient node created at: {createdTransient.Path}");

        // Step 2: "Confirm" by creating new node at final path with Active state
        var finalNode = MeshNode.FromPath(finalPath) with
        {
            Name = "My New Project",
            NodeType = "Markdown",
            State = MeshNodeState.Active
        };

        // Create the final node
        var createdFinal = await NodeFactory.CreateNodeAsync(finalNode, TestTimeout);
        createdFinal.Should().NotBeNull("Final node should be created");
        createdFinal.State.Should().Be(MeshNodeState.Active, "Final node should be Active");
        createdFinal.Path.Should().Be(finalPath, "Final node should be at desired path");
        Output.WriteLine($"Final node created at: {createdFinal.Path}");

        // Step 3: Delete the transient node
        await NodeFactory.DeleteNodeAsync(transientPath, ct: TestTimeout);

        // Verify: Transient should be gone, final should exist
        var transientAfterDelete = await MeshQuery.QueryAsync<MeshNode>($"path:{transientPath} scope:exact").FirstOrDefaultAsync();
        transientAfterDelete.Should().BeNull("Transient node should be deleted");

        var finalAfterCreate = await MeshQuery.QueryAsync<MeshNode>($"path:{finalPath} scope:exact").FirstOrDefaultAsync();
        finalAfterCreate.Should().NotBeNull("Final node should exist");
        finalAfterCreate!.State.Should().Be(MeshNodeState.Active);

        // Cleanup
        await NodeFactory.DeleteNodeAsync(finalPath, ct: TestTimeout);
    }

    /// <summary>
    /// Tests that DesiredId property is properly persisted with the transient node.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task CreateTransientNode_PreservesDesiredId()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string userId = "test-user";
        const string parentPath = "Test/DesiredId";
        var transientId = Guid.NewGuid().AsString();
        var desiredId = "UserPreferredId";
        var nodePath = $"{parentPath}/{transientId}";

        // Grant permissions
        await securityService.AddUserRoleAsync(userId, "Admin", parentPath, "system", TestTimeout);

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Test DesiredId Persistence",
            NodeType = "Markdown",
            State = MeshNodeState.Transient,
            DesiredId = desiredId
        };

        // Act
        var createdNode = await NodeFactory.CreateTransientAsync(node, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.DesiredId.Should().Be(desiredId, "DesiredId should be preserved after creation");

        // Verify it can be retrieved
        var fetchedNode = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath} scope:exact").FirstOrDefaultAsync();
        fetchedNode.Should().NotBeNull();
        fetchedNode!.DesiredId.Should().Be(desiredId, "DesiredId should be preserved after fetch");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(nodePath, ct: TestTimeout);
    }

    /// <summary>
    /// Tests that confirming a transient node (Transient -> Active) works correctly
    /// via CreateNodeRequest when node already exists.
    /// </summary>
    [Fact(Timeout = 10000)]
    public async Task ConfirmTransientNode_UpdatesStateToActive()
    {
        // Arrange
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string userId = "confirm-user";
        const string parentPath = "Confirm/Parent";
        var nodeId = $"ConfirmTest_{Guid.NewGuid().AsString()}";
        var nodePath = $"{parentPath}/{nodeId}";

        // Grant permissions
        await securityService.AddUserRoleAsync(userId, "Admin", parentPath, "system", TestTimeout);

        // Step 1: Create transient node via NodeFactory
        var transientNode = MeshNode.FromPath(nodePath) with
        {
            Name = "Confirm Test Node",
            NodeType = "Markdown",
            State = MeshNodeState.Transient
        };

        var createdTransient = await NodeFactory.CreateTransientAsync(transientNode, TestTimeout);
        createdTransient.State.Should().Be(MeshNodeState.Transient);

        // Step 2: Confirm by creating with Active state via CreateNodeAsync
        // Re-create the node at the same path - the handler will confirm it
        var activeNode = MeshNode.FromPath(nodePath) with
        {
            Name = "Confirm Test Node",
            NodeType = "Markdown",
            State = MeshNodeState.Active
        };

        var confirmedNode = await NodeFactory.CreateNodeAsync(activeNode, TestTimeout);

        // Assert
        confirmedNode.Should().NotBeNull("Confirmed node should be returned");
        confirmedNode.State.Should().Be(MeshNodeState.Active, "Node should be Active after confirmation");
        confirmedNode.Path.Should().Be(nodePath, "Path should remain the same");

        // Verify persistence
        var fetchedNode = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath} scope:exact").FirstOrDefaultAsync();
        fetchedNode.Should().NotBeNull();
        fetchedNode!.State.Should().Be(MeshNodeState.Active);

        // Cleanup
        await NodeFactory.DeleteNodeAsync(nodePath, ct: TestTimeout);
    }
}
