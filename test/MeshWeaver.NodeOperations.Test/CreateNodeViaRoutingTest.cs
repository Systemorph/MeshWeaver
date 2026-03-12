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

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests for creating nodes by sending CreateNodeRequest via IMeshService.
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
        var created = await NodeFactory.CreateNodeAsync(node, TestTimeout);

        // Assert
        created.Should().NotBeNull();
        created.Path.Should().Be(nodePath);
        created.State.Should().Be(MeshNodeState.Active);
        created.Name.Should().Be("Test Markdown Node");
        created.NodeType.Should().Be("Markdown");

        // Verify node exists in persistence
        var fetched = await MeshQuery
            .QueryAsync<MeshNode>($"path:{nodePath}")
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
        // Arrange — switch to "no-access-user" who has no permissions on "Restricted" namespace
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var noAccessUser = new AccessContext { ObjectId = "no-access-user", Name = "No Access" };
        accessService.SetContext(noAccessUser);
        accessService.SetCircuitContext(noAccessUser);

        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"Restricted/{nodeId}";

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Should Not Be Created",
            NodeType = "Markdown",
        };

        try
        {
            // Act & Assert — should throw UnauthorizedAccessException
            var act = async () => await NodeFactory.CreateNodeAsync(node, TestTimeout);
            await act.Should().ThrowAsync<UnauthorizedAccessException>();

            // Verify node does NOT exist
            var fetched = await MeshQuery
                .QueryAsync<MeshNode>($"path:{nodePath}")
                .FirstOrDefaultAsync(TestTimeout);
            fetched.Should().BeNull("rejected node should not exist");
        }
        finally
        {
            // Restore admin context
            TestUsers.DevLogin(Mesh);
        }
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
        var act = async () => await NodeFactory.CreateNodeAsync(node, TestTimeout);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*NodeType*");
    }

    /// <summary>
    /// ImpersonateAsHub scope sends operations with the hub's own identity.
    /// The mesh node's address is used as the AccessContext for authorization.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task CreateNode_ImpersonateAsHub_UsesHubIdentity()
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

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Act — create via ImpersonateAsHub scope (uses hub identity, not user identity)
        using (accessService.ImpersonateAsHub(Mesh))
        {
            var created = await NodeFactory.CreateNodeAsync(node, ct: TestTimeout);

            // Assert
            created.Should().NotBeNull();
            created.Path.Should().Be(nodePath);
            created.State.Should().Be(MeshNodeState.Active);

            // Cleanup — still within hub scope, so hub has permission on "Impersonate" namespace
            await NodeFactory.DeleteNodeAsync(nodePath, ct: TestTimeout);
        }
    }

    /// <summary>
    /// Query without ImpersonateAsHub on a namespace where the current user
    /// has no read access should return no results (security filtering).
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Query_WithoutImpersonation_ReturnsNoResults()
    {
        // Arrange — grant Admin to mesh hub on "Impersonate" namespace, but NOT to "no-access-user"
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var meshAddress = Mesh.Address.ToFullString();
        await securityService.AddUserRoleAsync(meshAddress, "Admin", "Impersonate", "system", TestTimeout);

        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"Impersonate/{nodeId}";

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Query Test Node",
            NodeType = "Markdown",
        };

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Create via impersonation scope (hub has access)
        using (accessService.ImpersonateAsHub(Mesh))
        {
            await NodeFactory.CreateNodeAsync(node, ct: TestTimeout);
        }

        try
        {
            // Switch to a user with no roles (the default admin has claim-based "Admin" role)
            accessService.SetCircuitContext(new AccessContext
            {
                ObjectId = "no-access-user",
                Name = "No Access"
            });

            // Act — query WITHOUT impersonation ("no-access-user" has no read access)
            var result = await MeshQuery
                .QueryAsync<MeshNode>($"path:{nodePath}")
                .FirstOrDefaultAsync(TestTimeout);

            // Assert — should return null (filtered by RLS)
            result.Should().BeNull("user has no read access to the Impersonate namespace");
        }
        finally
        {
            // Restore admin context for cleanup
            TestUsers.DevLogin(Mesh);
            using (accessService.ImpersonateAsHub(Mesh))
            {
                await NodeFactory.DeleteNodeAsync(nodePath, ct: TestTimeout);
            }
        }
    }

    /// <summary>
    /// Query with ImpersonateAsHub should succeed when the hub has read access.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task Query_WithImpersonation_ReturnsNode()
    {
        // Arrange — grant Admin to mesh hub on "Impersonate" namespace
        var securityService = Mesh.ServiceProvider.GetRequiredService<ISecurityService>();
        var meshAddress = Mesh.Address.ToFullString();
        await securityService.AddUserRoleAsync(meshAddress, "Admin", "Impersonate", "system", TestTimeout);

        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"Impersonate/{nodeId}";

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Impersonated Query Node",
            NodeType = "Markdown",
        };

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Create via impersonation scope (hub has access)
        using (accessService.ImpersonateAsHub(Mesh))
        {
            await NodeFactory.CreateNodeAsync(node, ct: TestTimeout);
        }

        try
        {
            // Act — query WITH impersonation scope (hub has read access)
            using (accessService.ImpersonateAsHub(Mesh))
            {
                var result = await MeshQuery
                    .QueryAsync<MeshNode>($"path:{nodePath}")
                    .FirstOrDefaultAsync(TestTimeout);

                // Assert
                result.Should().NotBeNull("hub has Admin role on Impersonate namespace");
                result!.Path.Should().Be(nodePath);
                result.Name.Should().Be("Impersonated Query Node");
            }
        }
        finally
        {
            // Cleanup
            using (accessService.ImpersonateAsHub(Mesh))
            {
                await NodeFactory.DeleteNodeAsync(nodePath, ct: TestTimeout);
            }
        }
    }
}
