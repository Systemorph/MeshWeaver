using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Persistence;
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
        return base.ConfigureMesh(builder)
            .AddRowLevelSecurity()
            .AddJsonGraphConfiguration(TestPaths.SamplesGraphData);
    }

    /// <summary>
    /// Tests that creating a node without Create permission throws UnauthorizedAccessException.
    /// The RlsNodeValidator should check permission on the parent path.
    /// </summary>
    [Fact]
    public async Task CreateNode_WithoutPermission_ThrowsUnauthorized()
    {
        // Arrange
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();

        const string userId = "unauthorized-user";
        const string parentPath = "Restricted/Parent";
        var nodePath = $"{parentPath}/TestNode_{Guid.NewGuid().AsString()}";

        // Note: User has NO permissions assigned - should be denied

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Test Node",
            NodeType = "Markdown"
        };

        // Act & Assert - CreateNodeAsync should throw UnauthorizedAccessException
        var act = async () => await catalog.CreateNodeAsync(node, userId, TestTimeout);
        var exception = await act.Should().ThrowAsync<UnauthorizedAccessException>();
        exception.Which.Message.Should().Contain("Access denied", "Should indicate authorization failure");
        Output.WriteLine($"Exception thrown as expected: {exception.Which.Message}");
    }

    /// <summary>
    /// Tests that creating a node with Create permission succeeds via IMeshCatalog.CreateNodeAsync.
    /// </summary>
    [Fact]
    public async Task CreateNode_WithPermission_Succeeds()
    {
        // Arrange
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
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
        var createdNode = await catalog.CreateNodeAsync(node, userId, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull("Node should be created");
        createdNode.State.Should().Be(MeshNodeState.Active, "Node should be in Active state");
        createdNode.Path.Should().Be(nodePath, "Node should be at the specified path");
        createdNode.Name.Should().Be("Test Node With Permission");
        createdNode.DesiredId.Should().Be("MyDesiredId", "DesiredId should be preserved");

        // Verify node exists in catalog
        var fetchedNode = await catalog.GetNodeAsync(new Address(nodePath));
        fetchedNode.Should().NotBeNull("Node should be retrievable from catalog");
        fetchedNode!.State.Should().Be(MeshNodeState.Active);

        // Cleanup
        await catalog.DeleteNodeAsync(nodePath, ct: TestTimeout);
    }

    /// <summary>
    /// Tests the Id change flow: when user changes Id during creation,
    /// a new node is created at the new path and the transient node is deleted.
    /// </summary>
    [Fact]
    public async Task CreateNode_IdChanged_CreatesNewNodeAndDeletesTransient()
    {
        // Arrange
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
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

        var createdTransient = await ((MeshCatalog)catalog).CreateTransientNodeAsync(transientNode, userId, TestTimeout);
        createdTransient.Should().NotBeNull("Transient node should be created");
        Output.WriteLine($"Transient node created at: {createdTransient.Path}");

        // Step 2: "Confirm" by creating new node at final path with Active state
        var finalNode = MeshNode.FromPath(finalPath) with
        {
            Name = "My New Project",
            NodeType = "Markdown",
            State = MeshNodeState.Active,
            Description = "Project created with Id change"
        };

        // Create the final node
        var createdFinal = await catalog.CreateNodeAsync(finalNode, userId, TestTimeout);
        createdFinal.Should().NotBeNull("Final node should be created");
        createdFinal.State.Should().Be(MeshNodeState.Active, "Final node should be Active");
        createdFinal.Path.Should().Be(finalPath, "Final node should be at desired path");
        Output.WriteLine($"Final node created at: {createdFinal.Path}");

        // Step 3: Delete the transient node
        await catalog.DeleteNodeAsync(transientPath, ct: TestTimeout);

        // Verify: Transient should be gone, final should exist
        var transientAfterDelete = await catalog.GetNodeAsync(new Address(transientPath));
        transientAfterDelete.Should().BeNull("Transient node should be deleted");

        var finalAfterCreate = await catalog.GetNodeAsync(new Address(finalPath));
        finalAfterCreate.Should().NotBeNull("Final node should exist");
        finalAfterCreate!.State.Should().Be(MeshNodeState.Active);

        // Cleanup
        await catalog.DeleteNodeAsync(finalPath, ct: TestTimeout);
    }

    /// <summary>
    /// Tests that DesiredId property is properly persisted with the transient node.
    /// </summary>
    [Fact]
    public async Task CreateTransientNode_PreservesDesiredId()
    {
        // Arrange
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
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
        var createdNode = await ((MeshCatalog)catalog).CreateTransientNodeAsync(node, userId, TestTimeout);

        // Assert
        createdNode.Should().NotBeNull();
        createdNode.DesiredId.Should().Be(desiredId, "DesiredId should be preserved after creation");

        // Verify it can be retrieved
        var fetchedNode = await catalog.GetNodeAsync(new Address(nodePath));
        fetchedNode.Should().NotBeNull();
        fetchedNode!.DesiredId.Should().Be(desiredId, "DesiredId should be preserved after fetch");

        // Cleanup
        await catalog.DeleteNodeAsync(nodePath, ct: TestTimeout);
    }

    /// <summary>
    /// Tests that confirming a transient node (Transient -> Active) works correctly
    /// via CreateNodeRequest when node already exists.
    /// </summary>
    [Fact]
    public async Task ConfirmTransientNode_UpdatesStateToActive()
    {
        // Arrange
        var catalog = Mesh.ServiceProvider.GetRequiredService<IMeshCatalog>();
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();

        const string userId = "confirm-user";
        const string parentPath = "Confirm/Parent";
        var nodeId = $"ConfirmTest_{Guid.NewGuid().AsString()}";
        var nodePath = $"{parentPath}/{nodeId}";

        // Grant permissions
        await securityService.AddUserRoleAsync(userId, "Admin", parentPath, "system", TestTimeout);

        // Step 1: Create transient node via catalog
        var transientNode = MeshNode.FromPath(nodePath) with
        {
            Name = "Confirm Test Node",
            NodeType = "Markdown",
            State = MeshNodeState.Transient
        };

        var createdTransient = await ((MeshCatalog)catalog).CreateTransientNodeAsync(transientNode, userId, TestTimeout);
        createdTransient.State.Should().Be(MeshNodeState.Transient);

        // Step 2: Confirm via ConfirmNodeAsync
        var confirmedNode = await ((MeshCatalog)catalog).ConfirmNodeAsync(nodePath, TestTimeout);

        // Assert
        confirmedNode.Should().NotBeNull("Confirmed node should be returned");
        confirmedNode!.State.Should().Be(MeshNodeState.Active, "Node should be Active after confirmation");
        confirmedNode.Path.Should().Be(nodePath, "Path should remain the same");

        // Verify persistence
        var fetchedNode = await catalog.GetNodeAsync(new Address(nodePath));
        fetchedNode.Should().NotBeNull();
        fetchedNode!.State.Should().Be(MeshNodeState.Active);

        // Cleanup
        await catalog.DeleteNodeAsync(nodePath, ct: TestTimeout);
    }
}
