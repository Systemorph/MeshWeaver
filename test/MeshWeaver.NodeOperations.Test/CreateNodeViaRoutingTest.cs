using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
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
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddRowLevelSecurity();
    }

    protected override Task SetupAccessRightsAsync()
    {
        // Grant Editor role to the default admin user on the test namespace
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Editor", "Test"))
            .Should().Within(45.Seconds()).Emit();
        return Task.CompletedTask;
    }

    /// <summary>
    /// SecurityService's synced query over AccessAssignment satellites populates
    /// asynchronously. After creating an AccessAssignment, subscribing to the
    /// live <c>GetEffectivePermissions</c> stream and using
    /// <c>.Match(p => p.HasFlag(permission))</c> deterministically absorbs the
    /// index-propagation race — no polling, no Task.Delay. Reactive (blocking)
    /// assertion so callers stay <c>void</c>.
    /// </summary>
    private void WaitForPermission(string path, string objectId, Permission permission)
        => Mesh.GetEffectivePermissions(path, objectId)
            .Should().Within(90.Seconds()).Match(p => p.HasFlag(permission));

    /// <summary>
    /// Creates a new MeshNode with valid content via CreateNodeRequest.
    /// The node should be persisted and retrievable.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void CreateNode_WithMarkdownContent_Succeeds()
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
        var created = NodeFactory.CreateNode(node).Should().Within(45.Seconds()).Emit();

        // Assert
        created.Should().NotBeNull();
        created.Path.Should().Be(nodePath);
        created.State.Should().Be(MeshNodeState.Active);
        created.Name.Should().Be("Test Markdown Node");
        created.NodeType.Should().Be("Markdown");

        // Verify node exists via stream (CQRS-correct read after write)
        var fetched = ReadNode(nodePath).Should().Within(45.Seconds()).Emit();
        fetched.Should().NotBeNull("node should be retrievable from persistence");

        // Cleanup
        NodeFactory.DeleteNode(nodePath).Should().Within(45.Seconds()).Emit();
    }

    /// <summary>
    /// CreateNodeRequest without permission should be rejected.
    /// The RlsNodeValidator checks permission on the parent path.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void CreateNode_WithoutPermission_Rejected()
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
            // Act & Assert — CreateNode must ERROR with UnauthorizedAccessException.
            // Materialize folds the OnError into a value so we assert it reactively
            // (no await, no ThrowAsync).
            var notification = NodeFactory.CreateNode(node).Materialize()
                .Should().Within(45.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
            notification.Exception.Should().BeOfType<UnauthorizedAccessException>();

            // Verify node does NOT exist (per-node hub returns NotFound — ReadNode surfaces null)
            var fetched = ReadNode(nodePath).Should().Within(45.Seconds()).Emit();
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
    [Fact(Timeout = 60000)]
    public void CreateNode_InvalidNodeType_Rejected()
    {
        // Arrange
        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"Test/{nodeId}";

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Invalid Type Node",
            NodeType = "NonExistent/FakeType",
        };

        // Act & Assert — CreateNode must ERROR with InvalidOperationException.
        // Materialize folds the OnError into a value so we assert it reactively.
        var notification = NodeFactory.CreateNode(node).Materialize()
            .Should().Within(45.Seconds()).Match(n => n.Kind == NotificationKind.OnError);
        notification.Exception.Should().BeOfType<InvalidOperationException>();
        notification.Exception!.Message.Should().Contain("NodeType");
    }

    /// <summary>
    /// ImpersonateAsHub scope sends operations with the hub's own identity.
    /// The mesh node's address is used as the AccessContext for authorization.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void CreateNode_ImpersonateAsHub_UsesHubIdentity()
    {
        // Arrange — grant access to the mesh hub's address
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var meshAddress = Mesh.Address.ToFullString();
        meshService.CreateNode(AssignmentNodeFactory.UserRole(meshAddress, "Admin", "Impersonate"))
            .Should().Within(45.Seconds()).Emit();
        WaitForPermission("Impersonate", meshAddress, Permission.Create);

        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"Impersonate/{nodeId}";

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Hub-Created Node",
            NodeType = "Markdown",
        };

        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        // Act — create via ImpersonateAsHub scope (uses hub identity, not user identity).
        // The blocking .Should().Emit() subscribes synchronously inside the `using`, so
        // CarryAccessContext captures the hub identity at subscribe time.
        using (accessService.ImpersonateAsHub(Mesh))
        {
            var created = NodeFactory.CreateNode(node).Should().Within(45.Seconds()).Emit();

            // Assert
            created.Should().NotBeNull();
            created.Path.Should().Be(nodePath);
            created.State.Should().Be(MeshNodeState.Active);

            // Cleanup — still within hub scope, so hub has permission on "Impersonate" namespace
            NodeFactory.DeleteNode(nodePath).Should().Within(45.Seconds()).Emit();
        }
    }

    /// <summary>
    /// Query without ImpersonateAsHub on a namespace where the current user
    /// has no read access should return no results (security filtering).
    /// </summary>
    [Fact(Timeout = 60000)]
    public void Query_WithoutImpersonation_ReturnsNoResults()
    {
        // Arrange — grant Admin to mesh hub on "Impersonate" namespace, but NOT to "no-access-user"
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var meshAddress = Mesh.Address.ToFullString();
        meshService.CreateNode(AssignmentNodeFactory.UserRole(meshAddress, "Admin", "Impersonate"))
            .Should().Within(45.Seconds()).Emit();
        WaitForPermission("Impersonate", meshAddress, Permission.Create);

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
            NodeFactory.CreateNode(node).Should().Within(45.Seconds()).Emit();
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
            var initial = MeshQuery
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{nodePath}"))
                .Should().Within(45.Seconds()).Match(c => c.ChangeType == QueryChangeType.Initial);

            // Assert — should be empty (filtered by RLS)
            initial.Items.Should().BeEmpty("user has no read access to the Impersonate namespace");
        }
        finally
        {
            // Restore admin context for cleanup
            TestUsers.DevLogin(Mesh);
            using (accessService.ImpersonateAsHub(Mesh))
            {
                NodeFactory.DeleteNode(nodePath).Should().Within(45.Seconds()).Emit();
            }
        }
    }

    /// <summary>
    /// Query with ImpersonateAsHub should succeed when the hub has read access.
    /// </summary>
    [Fact(Timeout = 60000)]
    public void Query_WithImpersonation_ReturnsNode()
    {
        // Arrange — grant Admin to mesh hub on "Impersonate" namespace
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var meshAddress = Mesh.Address.ToFullString();
        meshService.CreateNode(AssignmentNodeFactory.UserRole(meshAddress, "Admin", "Impersonate"))
            .Should().Within(45.Seconds()).Emit();
        WaitForPermission("Impersonate", meshAddress, Permission.Create);

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
            NodeFactory.CreateNode(node).Should().Within(45.Seconds()).Emit();
        }

        try
        {
            // Act — query WITH impersonation scope (hub has read access).
            // The blocking .Should().Match() subscribes synchronously inside the `using`,
            // so CarryAccessContext captures the hub identity at subscribe time.
            using (accessService.ImpersonateAsHub(Mesh))
            {
                var change = MeshQuery
                    .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{nodePath}"))
                    .Should().Within(45.Seconds())
                    .Match(c => c.ChangeType == QueryChangeType.Initial && c.Items.Count > 0);
                var result = change.Items;

                // Assert
                result.Should().ContainSingle("hub has Admin role on Impersonate namespace");
                result[0].Path.Should().Be(nodePath);
                result[0].Name.Should().Be("Impersonated Query Node");
            }
        }
        finally
        {
            // Cleanup
            using (accessService.ImpersonateAsHub(Mesh))
            {
                NodeFactory.DeleteNode(nodePath).Should().Within(45.Seconds()).Emit();
            }
        }
    }
}
