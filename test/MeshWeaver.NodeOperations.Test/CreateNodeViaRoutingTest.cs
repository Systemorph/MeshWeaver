using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
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
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        return ConfigureMeshBase(builder)
            .AddRowLevelSecurity();
    }

    protected override async Task SetupAccessRightsAsync()
    {
        // Grant Editor role to the default admin user on the test namespace
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Editor", "Test")).FirstAsync().ToTask(TestTimeout);
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
    // Stays `async Task` + `await … FirstAsync()` (the sanctioned async leaf — see
    // FluentAssertionsToReactive.md §2a). The blocking reactive `.Should().Emit()` form
    // DEADLOCKED here: under RLS the CreateNodeRequest validation pipeline pumps the mesh
    // hub on the test thread, but `Should().Emit()` blocks that thread on a
    // ManualResetEventSlim → the response never gets pumped → 45s/60s [Fact] kill (same
    // failure the CreateNode_Without/InvalidNodeType throw-tests hit with `.Wait()`). The
    // await yields the thread so the pump runs and the create completes.
    [Fact(Timeout = 60000)]
    public async Task CreateNode_WithMarkdownContent_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange
        var nodeId = $"Md_{Guid.NewGuid().AsString()}";
        var nodePath = $"Test/{nodeId}";

        var node = MeshNode.FromPath(nodePath) with
        {
            Name = "Test Markdown Node",
            NodeType = "Markdown",
        };

        // Act
        var created = await NodeFactory.CreateNode(node).FirstAsync().ToTask(ct);

        // Assert
        created.Should().NotBeNull();
        created.Path.Should().Be(nodePath);
        created.State.Should().Be(MeshNodeState.Active);
        created.Name.Should().Be("Test Markdown Node");
        created.NodeType.Should().Be("Markdown");

        // Verify node exists via stream (CQRS-correct read after write)
        var fetched = await ReadNode(nodePath).FirstAsync().ToTask(ct);
        fetched.Should().NotBeNull("node should be retrievable from persistence");

        // Cleanup
        await NodeFactory.DeleteNode(nodePath).FirstAsync().ToTask(ct);
    }

    /// <summary>
    /// CreateNodeRequest without permission should be rejected.
    /// The RlsNodeValidator checks permission on the parent path.
    /// </summary>
    // Genuine-throw test: stays `async Task` and uses `await act.Should().ThrowAsync<T>()`
    // (the sanctioned async leaf — see FluentAssertionsToReactive.md §2a). A blocking
    // `Action act = () => CreateNode(node).Wait()` deadlocked here: under RLS the
    // CreateNode validation pipeline pumps the mesh hub, and `.Wait()` blocks the test
    // thread that pump needs → 60s [Fact] kill. The await yields the thread, so the pump
    // runs and the UnauthorizedAccessException surfaces. No blocking reactive `.Should()`
    // in this method (the post-throw read awaits its own FirstAsync leaf).
    [Fact(Timeout = 60000)]
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
            var act = async () => await NodeFactory.CreateNode(node);
            await act.Should().ThrowAsync<UnauthorizedAccessException>();

            // Verify node does NOT exist (per-node hub returns NotFound — ReadNode surfaces null)
            var fetched = await ReadNode(nodePath).FirstAsync();
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
    // Genuine-throw test: async + `await act.Should().ThrowAsync<T>()` (§2a). The blocking
    // `.Wait()` form deadlocked the cold-start validation pump → 60s [Fact] kill.
    [Fact(Timeout = 60000)]
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
        var act = async () => await NodeFactory.CreateNode(node);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*NodeType*");
    }

    /// <summary>
    /// ImpersonateAsHub scope sends operations with the hub's own identity.
    /// The mesh node's address is used as the AccessContext for authorization.
    /// </summary>
    // async Task + `await … FirstAsync()` (§2a): the blocking `.Should().Emit()` on a
    // CreateNode/DeleteNode round-trip starves the mesh-hub pump on the test thread under RLS
    // and deadlocks. The await yields the thread so the pump runs. The ImpersonateAsHub scope
    // still flows: the subscribe happens synchronously inside the `using` (before the first
    // await suspension), so CarryAccessContext captures the hub identity at subscribe time.
    [Fact(Timeout = 60000)]
    public async Task CreateNode_ImpersonateAsHub_UsesHubIdentity()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange — grant access to the mesh hub's address
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var meshAddress = Mesh.Address.ToFullString();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(meshAddress, "Admin", "Impersonate")).FirstAsync().ToTask(ct);
        WaitForPermission("Impersonate", meshAddress, Permission.Create);

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
            var created = await NodeFactory.CreateNode(node).FirstAsync().ToTask(ct);

            // Assert
            created.Should().NotBeNull();
            created.Path.Should().Be(nodePath);
            created.State.Should().Be(MeshNodeState.Active);

            // Cleanup — still within hub scope, so hub has permission on "Impersonate" namespace
            await NodeFactory.DeleteNode(nodePath).FirstAsync().ToTask(ct);
        }
    }

    /// <summary>
    /// Query without ImpersonateAsHub on a namespace where the current user
    /// has no read access should return no results (security filtering).
    /// </summary>
    // async Task + `await … FirstAsync()` (§2a): the blocking `.Should().Emit()` on the
    // CreateNode/DeleteNode round-trips starves the mesh-hub pump on the test thread under RLS
    // and deadlocks. The await yields the thread so the pump runs.
    [Fact(Timeout = 60000)]
    public async Task Query_WithoutImpersonation_ReturnsNoResults()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange — grant Admin to mesh hub on "Impersonate" namespace, but NOT to "no-access-user"
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var meshAddress = Mesh.Address.ToFullString();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(meshAddress, "Admin", "Impersonate")).FirstAsync().ToTask(ct);
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
            await NodeFactory.CreateNode(node).FirstAsync().ToTask(ct);
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
            var initial = await MeshQuery
                .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{nodePath}"))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .FirstAsync()
                .ToTask(ct);

            // Assert — should be empty (filtered by RLS)
            initial.Items.Should().BeEmpty("user has no read access to the Impersonate namespace");
        }
        finally
        {
            // Restore admin context for cleanup
            TestUsers.DevLogin(Mesh);
            using (accessService.ImpersonateAsHub(Mesh))
            {
                await NodeFactory.DeleteNode(nodePath).FirstAsync().ToTask(ct);
            }
        }
    }

    /// <summary>
    /// Query with ImpersonateAsHub should succeed when the hub has read access.
    /// </summary>
    // async Task + `await … FirstAsync()` (§2a): the blocking `.Should().Emit()` on the
    // CreateNode/DeleteNode round-trips starves the mesh-hub pump on the test thread under RLS
    // and deadlocks. The await yields the thread so the pump runs. The ImpersonateAsHub scope
    // still flows: each subscribe runs synchronously inside its `using` (before the first await
    // suspension), so CarryAccessContext captures the hub identity at subscribe time.
    [Fact(Timeout = 60000)]
    public async Task Query_WithImpersonation_ReturnsNode()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange — grant Admin to mesh hub on "Impersonate" namespace
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var meshAddress = Mesh.Address.ToFullString();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(meshAddress, "Admin", "Impersonate")).FirstAsync().ToTask(ct);
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
            await NodeFactory.CreateNode(node).FirstAsync().ToTask(ct);
        }

        try
        {
            // Act — query WITH impersonation scope (hub has read access)
            using (accessService.ImpersonateAsHub(Mesh))
            {
                var change = await MeshQuery
                    .ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery($"path:{nodePath}"))
                    .Where(c => c.ChangeType == QueryChangeType.Initial && c.Items.Count > 0)
                    .FirstAsync()
                    .ToTask(ct);
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
                await NodeFactory.DeleteNode(nodePath).FirstAsync().ToTask(ct);
            }
        }
    }
}
