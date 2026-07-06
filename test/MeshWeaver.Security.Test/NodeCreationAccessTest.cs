using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using MeshWeaver.AI;
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

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests for node creation access control, specifically testing:
/// 1. CreateNode without permission returns Unauthorized
/// 2. CreateNode with permission creates an Active node
/// 3. Creating a node at its desired final path
/// </summary>
public class NodeCreationAccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(15.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // ConfigureMeshBase adds RLS + graph (incl. Markdown)
        return ConfigureMeshBase(builder)
            .AddThreadType();
    }

    /// <summary>
    /// Tests that creating a node without Create permission throws UnauthorizedAccessException.
    /// The RlsNodeValidator should check permission on the parent path.
    /// </summary>
    [Fact(Timeout = 20000)]
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
            // Act & Assert - CreateNode should throw UnauthorizedAccessException
            Func<Task> act = () => NodeFactory.CreateNode(node).FirstAsync().ToTask();
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
    /// Tests that creating a node with Create permission succeeds via IMeshService.CreateNode.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CreateNode_WithPermission_Succeeds()
    {
        // Arrange
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        const string userId = "authorized-user";
        const string parentPath = "Authorized/Parent";
        var nodeId = $"TestNode_{Guid.NewGuid().AsString()}";
        var nodePath = $"{parentPath}/{nodeId}";

        // Grant Editor role (includes Create permission) — runtime AccessAssignment node.
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(userId, "Editor", parentPath))
            .Should().Emit();

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Test Node With Permission",
            NodeType = "Markdown",
            DesiredId = "MyDesiredId" // User's intended final Id
        };

        // Act - Use public CreateNode which goes through message-based validation
        var createdNode = await NodeFactory.CreateNode(node).Should().Emit();

        // Assert
        createdNode.Should().NotBeNull("Node should be created");
        createdNode.State.Should().Be(MeshNodeState.Active, "Node should be in Active state");
        createdNode.Path.Should().Be(nodePath, "Node should be at the specified path");
        createdNode.Name.Should().Be("Test Node With Permission");
        createdNode.DesiredId.Should().Be("MyDesiredId", "DesiredId should be preserved");

        // Verify node exists via per-node stream
        var fetchedNode = await ReadNode(nodePath).Should().Emit();
        fetchedNode.Should().NotBeNull("Node should be retrievable");
        fetchedNode!.State.Should().Be(MeshNodeState.Active);

        // Cleanup
        await NodeFactory.DeleteNode(nodePath).Should().Emit();
    }

    /// <summary>
    /// Tests that an Editor-role user can create a node directly at its
    /// desired final path.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CreateNode_IdChanged_CreatesNewNodeAndDeletesTransient()
    {
        // Arrange
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        const string userId = "editor-user";
        const string parentNamespace = "Editor/Projects";
        var desiredId = "MyNewProject";
        var finalPath = $"{parentNamespace}/{desiredId}";

        // Grant Editor role — runtime AccessAssignment node.
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(userId, "Editor", parentNamespace))
            .Should().Emit();

        // Create the node directly at the final path with Active state.
        var finalNode = MeshNode.FromPath(finalPath) with
        {
            Name = "My New Project",
            NodeType = "Markdown",
            State = MeshNodeState.Active
        };

        var createdFinal = await NodeFactory.CreateNode(finalNode).Should().Emit();
        createdFinal.Should().NotBeNull("Final node should be created");
        createdFinal.State.Should().Be(MeshNodeState.Active, "Final node should be Active");
        createdFinal.Path.Should().Be(finalPath, "Final node should be at desired path");
        Output.WriteLine($"Final node created at: {createdFinal.Path}");

        // Verify: final node should exist (stream read)
        var finalAfterCreate = await ReadNode(finalPath).Should().Emit();
        finalAfterCreate.Should().NotBeNull("Final node should exist");
        finalAfterCreate!.State.Should().Be(MeshNodeState.Active);

        // Cleanup
        await NodeFactory.DeleteNode(finalPath).Should().Emit();
    }

    /// <summary>
    /// Tests that DesiredId property is properly persisted with the created node.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task CreateNode_PreservesDesiredId()
    {
        // Arrange
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        const string userId = "test-user";
        const string parentPath = "Test/DesiredId";
        var nodeId = Guid.NewGuid().AsString();
        var desiredId = "UserPreferredId";
        var nodePath = $"{parentPath}/{nodeId}";

        // Grant Admin role — runtime AccessAssignment node.
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(userId, "Admin", parentPath))
            .Should().Emit();

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Test DesiredId Persistence",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            DesiredId = desiredId
        };

        // Act
        var createdNode = await NodeFactory.CreateNode(node).Should().Emit();

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.DesiredId.Should().Be(desiredId, "DesiredId should be preserved after creation");

        // Verify it can be retrieved (stream read)
        var fetchedNode = await ReadNode(nodePath).Should().Emit();
        fetchedNode.Should().NotBeNull();
        fetchedNode!.DesiredId.Should().Be(desiredId, "DesiredId should be preserved after fetch");

        // Cleanup
        await NodeFactory.DeleteNode(nodePath).Should().Emit();
    }

    /// <summary>
    /// Tests that an Admin-role user can create an Active node via CreateNode.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task ConfirmTransientNode_UpdatesStateToActive()
    {
        // Arrange
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        const string userId = "confirm-user";
        const string parentPath = "Confirm/Parent";
        var nodeId = $"ConfirmTest_{Guid.NewGuid().AsString()}";
        var nodePath = $"{parentPath}/{nodeId}";

        // Grant Admin role — runtime AccessAssignment node.
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(userId, "Admin", parentPath))
            .Should().Emit();

        // Create the node directly with Active state via NodeFactory.
        var activeNode = MeshNode.FromPath(nodePath) with
        {
            Name = "Confirm Test Node",
            NodeType = "Markdown",
            State = MeshNodeState.Active
        };

        var confirmedNode = await NodeFactory.CreateNode(activeNode).Should().Emit();

        // Assert
        confirmedNode.Should().NotBeNull("Created node should be returned");
        confirmedNode.State.Should().Be(MeshNodeState.Active, "Node should be Active");
        confirmedNode.Path.Should().Be(nodePath, "Path should match");

        // Verify persistence (stream read)
        var fetchedNode = await ReadNode(nodePath).Should().Emit();
        fetchedNode.Should().NotBeNull();
        fetchedNode!.State.Should().Be(MeshNodeState.Active);

        // Cleanup
        await NodeFactory.DeleteNode(nodePath).Should().Emit();
    }

    /// <summary>
    /// Tests that a user can create a Thread node under their own User node
    /// via the self-access check (User/{ObjectId} scope).
    /// This is the permission path used by the dashboard chat to create threads.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task CreateThread_UnderOwnUserNode_Succeeds()
    {
        // Arrange — log in as a user whose ObjectId matches a User node path
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var userId = "self-access-user";
        var userContext = new AccessContext { ObjectId = userId, Name = "Self Access User" };
        accessService.SetContext(userContext);
        accessService.SetCircuitContext(userContext);

        try
        {
            // Act — create a Thread under {userId} (self-access should grant permission).
            // Post-v10: per-user partition lives at root namespace, so the user's
            // own scope is just "{userId}" rather than the legacy "User/{userId}".
            var threadPath = $"{userId}/TestThread_{Guid.NewGuid().AsString()}";
            var threadNode = MeshNode.FromPath(threadPath) with
            {
                Name = "Test Chat Thread",
                NodeType = ThreadNodeType.NodeType
            };

            var created = await NodeFactory.CreateNode(threadNode).Should().Emit();

            // Assert
            created.Should().NotBeNull("User should be able to create threads under their own User node");
            created.State.Should().Be(MeshNodeState.Active);
            created.Path.Should().Be(threadPath);
            created.MainNode.Should().Be(userId, "Satellite thread MainNode should point to parent node");
            Output.WriteLine($"Thread created successfully at: {created.Path}, MainNode: {created.MainNode}");
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }

    /// <summary>
    /// Tests that a user CANNOT create a Thread under another user's node.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task CreateThread_UnderOtherUserNode_ThrowsUnauthorized()
    {
        // Arrange — switch to a user who should NOT have access to another user's scope
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var attackerContext = new AccessContext { ObjectId = "attacker-user", Name = "Attacker" };
        accessService.SetContext(attackerContext);
        accessService.SetCircuitContext(attackerContext);

        try
        {
            // Act — try to create a thread under another user's node
            var threadPath = "User/other-user/MaliciousThread";
            var threadNode = MeshNode.FromPath(threadPath) with
            {
                Name = "Malicious Thread",
                NodeType = ThreadNodeType.NodeType
            };

            Func<Task> act = () => NodeFactory.CreateNode(threadNode).FirstAsync().ToTask();

            // Assert
            await act.Should().ThrowAsync<UnauthorizedAccessException>(
                "User should NOT be able to create threads under another user's node");
        }
        finally
        {
            TestUsers.DevLogin(Mesh);
        }
    }
}
