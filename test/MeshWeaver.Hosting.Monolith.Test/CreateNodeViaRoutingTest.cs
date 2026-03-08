using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for creating nodes by sending CreateNodeRequest via IMeshNodePersistence.
/// Covers: successful creation, access denial, and invalid content rejection.
/// </summary>
public class CreateNodeViaEventTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(15.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddRowLevelSecurity();
    }

    protected override async Task SetupAccessRightsAsync()
    {
        // Grant Editor role to the default admin user on the test namespace
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        await securityService.AddUserRoleAsync(
            TestUsers.Admin.ObjectId, "Editor", "Test", "system", TestTimeout);
    }

    /// <summary>
    /// Creates a new MeshNode with valid content via CreateNodeRequest.
    /// The node should be persisted and retrievable.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task CreateNode_WithMarkdownContent_Succeeds()
    {
        // Arrange
        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"Test/{nodeId}";

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Test Markdown Node",
            NodeType = "Markdown",
        };

        // Act
        var created = await NodeFactory.CreateNodeAsync(node, TestUsers.Admin.ObjectId, TestTimeout);

        // Assert
        created.Should().NotBeNull();
        created.Path.Should().Be(nodePath);
        created.State.Should().Be(MeshNodeState.Active);
        created.Name.Should().Be("Test Markdown Node");
        created.NodeType.Should().Be("Markdown");

        // Verify node exists in persistence
        var fetched = await MeshQuery
            .QueryAsync<MeshNode>($"path:{nodePath} scope:exact")
            .FirstOrDefaultAsync(TestTimeout);
        fetched.Should().NotBeNull("node should be retrievable from persistence");

        // Cleanup
        await NodeFactory.DeleteNodeAsync(nodePath, ct: TestTimeout);
    }

    /// <summary>
    /// CreateNodeRequest without permission should be rejected.
    /// The RlsNodeValidator checks permission on the parent path.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task CreateNode_WithoutPermission_Rejected()
    {
        // Arrange — "no-access-user" has no permissions on "Restricted" namespace
        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"Restricted/{nodeId}";

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Should Not Be Created",
            NodeType = "Markdown",
        };

        // Act & Assert — should throw UnauthorizedAccessException
        var act = async () => await NodeFactory.CreateNodeAsync(node, "no-access-user", TestTimeout);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        // Verify node does NOT exist
        var fetched = await MeshQuery
            .QueryAsync<MeshNode>($"path:{nodePath} scope:exact")
            .FirstOrDefaultAsync(TestTimeout);
        fetched.Should().BeNull("rejected node should not exist");
    }

    /// <summary>
    /// CreateNodeRequest with an invalid NodeType should be rejected.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task CreateNode_InvalidNodeType_Rejected()
    {
        // Arrange
        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"Test/{nodeId}";

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Invalid Type Node",
            NodeType = "NonExistent/FakeType",
        };

        // Act & Assert — should throw InvalidOperationException
        var act = async () => await NodeFactory.CreateNodeAsync(node, TestUsers.Admin.ObjectId, TestTimeout);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*NodeType*");
    }

    /// <summary>
    /// ImpersonateAsNode sends operations with the hub's own identity.
    /// The mesh hub's address is used as the AccessContext for authorization.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task CreateNode_ImpersonateAsNode_UsesHubIdentity()
    {
        // Arrange — grant access to the mesh hub's address
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var meshAddress = Mesh.Address.ToFullString();
        await securityService.AddUserRoleAsync(meshAddress, "Admin", "Impersonate", "system", TestTimeout);

        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"Impersonate/{nodeId}";

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Hub-Created Node",
            NodeType = "Markdown",
        };

        // Act — create via ImpersonateAsNode (uses hub identity, not user identity)
        var impersonated = NodeFactory.ImpersonateAsNode();
        var created = await impersonated.CreateNodeAsync(node, ct: TestTimeout);

        // Assert
        created.Should().NotBeNull();
        created.Path.Should().Be(nodePath);
        created.State.Should().Be(MeshNodeState.Active);

        // Cleanup — use impersonated delete since regular user has no permission on "Impersonate" namespace
        await impersonated.DeleteNodeAsync(nodePath, ct: TestTimeout);
    }
}
