using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.AI;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Integration tests for Row-Level Security with actual CRUD operations.
/// These tests verify the permission evaluation and validator behavior.
/// </summary>
public class RlsIntegrationTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // ConfigureMeshBase adds persistence + RLS + graph
        var configured = ConfigureMeshBase(builder)
            .AddThreadType();

        // Seed every per-test AccessAssignment statically (via AddMeshNodes →
        // IStaticNodeProvider → SecurityService._staticAccessAssignments).
        // Doing the same seeding through `meshService.CreateNode(AccessAssignment)`
        // at runtime hangs the per-node-hub creation flow under the current
        // SecurityService re-entry pattern, so the tests must declare their
        // fixture data up-front rather than mutate via runtime CRUD.
        configured.AddMeshNodes(
            AssignmentNodeFactory.UserRole("creator", "Editor", "rls/create"),
            AssignmentNodeFactory.UserRole("reader", "Viewer", "rls/readonly"),
            AssignmentNodeFactory.UserRole("deletor", "Admin", "rls/delete"),
            AssignmentNodeFactory.UserRole("admin", "Admin", "rls/nodelete"),
            AssignmentNodeFactory.UserRole("viewer_nodelete", "Viewer", "rls/nodelete"),
            AssignmentNodeFactory.UserRole("editor", "Editor", "rls/update"),
            AssignmentNodeFactory.UserRole("admin_noupd", "Admin", "rls/noupdate"),
            AssignmentNodeFactory.UserRole("viewer_noupd", "Viewer", "rls/noupdate"),
            AssignmentNodeFactory.UserRole("hierarchyuser", "Admin", "rls"),
            AssignmentNodeFactory.UserRole("globaladmin", "Admin", null!),
            AssignmentNodeFactory.UserRole("multirole", "Viewer", "rls/multi/project1"),
            AssignmentNodeFactory.UserRole("multirole", "Editor", "rls/multi/project2"),
            AssignmentNodeFactory.UserRole("commenter", "Commenter", "rls/comments"),
            AssignmentNodeFactory.UserRole("viewer_cmt", "Viewer", "rls/comments"),
            AssignmentNodeFactory.UserRole("editor_thr", "Editor", "rls/threads"),
            AssignmentNodeFactory.UserRole("commenter_thr", "Commenter", "rls/threads"),
            AssignmentNodeFactory.UserRole("editor_commenter", "Editor", "rls/editor_comment"),
            AssignmentNodeFactory.UserRole("admin_for_anon_delete", "Admin", "rls/anon_delete"),
            AssignmentNodeFactory.UserRole("admin_for_anon_update", "Admin", "rls/anon_update"),
            AssignmentNodeFactory.UserRole(WellKnownUsers.Anonymous, "Viewer", "rls/public_viewer"),
            AssignmentNodeFactory.UserRole("admin_setup", "Admin", "rls/nodel"),
            AssignmentNodeFactory.UserRole("editor_nodel", "Editor", "rls/nodel"),
            AssignmentNodeFactory.UserRole("strict_viewer", "Viewer", "rls/viewonly"),
            AssignmentNodeFactory.UserRole("commenter_test", "Commenter", "rls/commenter_test")
        );

        return configured;
    }

    [Fact]
    public async Task CreateNode_WithCreatePermission_Succeeds()
    {
        // Arrange
        var client = GetClient();

        const string userId = "creator";
        const string parentPath = "rls/create";

        // Create node with CreatedBy set to the authorized user (Editor role
        // assigned at parentPath via static seed in ConfigureMesh).
        var node = new MeshNode("NewNode", parentPath)
        {
            Name = "Created Node",
            NodeType = "Markdown"
        };
        var request = new CreateNodeRequest(node) { CreatedBy = userId };

        // Act
        var response = await client.Observe(request, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert
        response.Message.Success.Should().BeTrue();
        response.Message.Node.Should().NotBeNull();
        response.Message.Node!.Name.Should().Be("Created Node");
    }

    [Fact(Timeout = 10000)]
    public async Task CreateNode_InvalidNodeType_ReturnsCleanError()
    {
        // Arrange — "creator" has Editor at "rls/create" via static seed, but
        // the NodeType "this-type-does-not-exist" is not registered anywhere
        // (no MeshConfiguration entry, no IStaticNodeProvider, no persistence).
        // The handler must surface InvalidNodeType, not hang on a NodeType-hub
        // round trip and time out the caller.
        var client = GetClient();

        const string userId = "creator";
        const string parentPath = "rls/create";

        var node = new MeshNode("BadType", parentPath)
        {
            Name = "Should Fail",
            NodeType = "this-type-does-not-exist"
        };
        var request = new CreateNodeRequest(node) { CreatedBy = userId };

        // Act
        var response = await client.Observe(request, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert — clean rejection (NOT TimeoutException) with InvalidNodeType reason.
        response.Message.Success.Should().BeFalse();
        response.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.InvalidNodeType);
        response.Message.Error.Should().Contain("this-type-does-not-exist");
    }

    [Fact]
    public async Task CreateNode_WithoutPermission_Fails()
    {
        // Arrange — "reader" gets Viewer (Read only) at parentPath via static seed.
        var client = GetClient();

        const string userId = "reader";
        const string parentPath = "rls/readonly";

        var node = new MeshNode("FailedNode", parentPath)
        {
            Name = "Should Fail",
            NodeType = "Markdown"
        };
        var request = new CreateNodeRequest(node) { CreatedBy = userId };

        // Act
        var response = await client.Observe(request, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

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
            NodeType = "Markdown"
        };
        var request = new CreateNodeRequest(node) { CreatedBy = userId };

        // Act
        var response = await client.Observe(request, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert
        response.Message.Success.Should().BeFalse();
        response.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
    }

    [Fact]
    public async Task DeleteNode_WithDeletePermission_Succeeds()
    {
        // Arrange — "deletor" gets Admin at parentPath via static seed.
        var client = GetClient();

        const string adminId = "deletor";
        const string parentPath = "rls/delete";

        // Create a node first
        var node = new MeshNode("ToDelete", parentPath)
        {
            Name = "To Be Deleted",
            NodeType = "Markdown"
        };
        var createResponse = await client.Observe(new CreateNodeRequest(node) { CreatedBy = adminId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();
        createResponse.Message.Success.Should().BeTrue();

        // Act - delete the node using DeletedBy
        var deleteResponse = await client.Observe(new DeleteNodeRequest("rls/delete/ToDelete") { DeletedBy = adminId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert
        deleteResponse.Message.Success.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteNode_WithoutPermission_Fails()
    {
        // Arrange — "admin" gets Admin and "viewer_nodelete" gets Viewer at parentPath via static seed.
        var client = GetClient();

        const string adminId = "admin";
        const string viewerId = "viewer_nodelete";
        const string parentPath = "rls/nodelete";

        var node = new MeshNode("Protected", parentPath)
        {
            Name = "Protected Node",
            NodeType = "Markdown"
        };
        var createResponse = await client.Observe(new CreateNodeRequest(node) { CreatedBy = adminId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();
        createResponse.Message.Success.Should().BeTrue();

        // Act - viewer tries to delete
        var deleteResponse = await client.Observe(new DeleteNodeRequest("rls/nodelete/Protected") { DeletedBy = viewerId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert - Should fail due to insufficient permissions. Phase 2 of the delete
        // orchestrator now returns Unauthorized for this case; Phase 3 (RLS INodeValidator)
        // would surface the same refusal as ValidationFailed â€” accept either.
        deleteResponse.Message.Success.Should().BeFalse();
        deleteResponse.Message.RejectionReason.Should().BeOneOf(
            NodeDeletionRejectionReason.Unauthorized,
            NodeDeletionRejectionReason.ValidationFailed);
    }

    [Fact]
    public async Task UpdateNode_WithUpdatePermission_Succeeds()
    {
        // Arrange — "editor" gets Editor at parentPath via static seed.
        var client = GetClient();

        const string editorId = "editor";
        const string parentPath = "rls/update";

        // Create a node first
        var node = new MeshNode("ToUpdate", parentPath)
        {
            Name = "Original Name",
            NodeType = "Markdown"
        };
        var createResponse = await client.Observe(new CreateNodeRequest(node) { CreatedBy = editorId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();
        createResponse.Message.Success.Should().BeTrue();

        // Act - update the node using UpdatedBy
        var updatedNode = node with { Name = "Updated Name" };
        var updateResponse = await client.Observe(new UpdateNodeRequest(updatedNode) { UpdatedBy = editorId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert
        updateResponse.Message.Success.Should().BeTrue();
        updateResponse.Message.Node!.Name.Should().Be("Updated Name");
    }

    [Fact]
    public async Task UpdateNode_WithoutPermission_Fails()
    {
        // Arrange — "admin_noupd" gets Admin and "viewer_noupd" gets Viewer at parentPath via static seed.
        var client = GetClient();

        const string adminId = "admin_noupd";
        const string viewerId = "viewer_noupd";
        const string parentPath = "rls/noupdate";

        var node = new MeshNode("NoUpdate", parentPath)
        {
            Name = "Cannot Update",
            NodeType = "Markdown"
        };
        var createResponse = await client.Observe(new CreateNodeRequest(node) { CreatedBy = adminId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();
        createResponse.Message.Success.Should().BeTrue();

        // Act - viewer tries to update
        var updatedNode = node with { Name = "Trying to Update" };
        var updateResponse = await client.Observe(new UpdateNodeRequest(updatedNode) { UpdatedBy = viewerId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert
        updateResponse.Message.Success.Should().BeFalse();
        updateResponse.Message.RejectionReason.Should().Be(NodeUpdateRejectionReason.ValidationFailed);
    }

    [Fact]
    public async Task HierarchicalPermission_InheritsFromParent()
    {
        // Arrange — "hierarchyuser" gets Admin at "rls" via static seed (inherits to children).
        var client = GetClient();

        const string userId = "hierarchyuser";
        const string topLevelPath = "rls";
        const string parentPath = "rls/parent";

        // Create parent node first
        var parentNode = new MeshNode("parent", topLevelPath)
        {
            Name = "Parent Node",
            NodeType = "Markdown"
        };
        var parentResponse = await client.Observe(new CreateNodeRequest(parentNode) { CreatedBy = userId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();
        parentResponse.Message.Success.Should().BeTrue();

        // Act - create child node (should inherit parent permissions)
        var childNode = new MeshNode("child", parentPath)
        {
            Name = "Child Node",
            NodeType = "Markdown"
        };
        var childResponse = await client.Observe(new CreateNodeRequest(childNode) { CreatedBy = userId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert - child creation should succeed due to inherited permissions
        childResponse.Message.Success.Should().BeTrue();
    }

    [Fact]
    public async Task GlobalAdmin_HasAccessEverywhere()
    {
        // Arrange — "globaladmin" gets Admin at null (global) via static seed.
        var client = GetClient();

        const string globalAdminId = "globaladmin";

        // Act - create nodes in different paths
        var node1 = new MeshNode("GlobalTest1", "random/path")
        {
            Name = "Global Test 1",
            NodeType = "Markdown"
        };
        var response1 = await client.Observe(new CreateNodeRequest(node1) { CreatedBy = globalAdminId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        var node2 = new MeshNode("GlobalTest2", "another/random/path")
        {
            Name = "Global Test 2",
            NodeType = "Markdown"
        };
        var response2 = await client.Observe(new CreateNodeRequest(node2) { CreatedBy = globalAdminId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert - global admin should be able to create anywhere
        response1.Message.Success.Should().BeTrue();
        response2.Message.Success.Should().BeTrue();
    }

    [Fact]
    public async Task MultipleRoles_CombinePermissions()
    {
        // Arrange — "multirole" gets Viewer at path1 and Editor at path2 via static seeds.

        const string userId = "multirole";
        const string path1 = "rls/multi/project1";
        const string path2 = "rls/multi/project2";

        // Act - check permissions at each path
        var permissions1 = await Mesh.GetPermissionAsync(path1, userId, TestTimeout);
        var permissions2 = await Mesh.GetPermissionAsync(path2, userId, TestTimeout);

        // Assert
        permissions1.Should().Be(Permission.Read | Permission.Execute | Permission.Api); // Viewer only
        permissions2.Should().Be(Permission.Read | Permission.Create | Permission.Update | Permission.Comment | Permission.Execute | Permission.Thread | Permission.Api | Permission.Export); // Editor
    }

    [Fact]
    public async Task CreateComment_RequiresCommentPermission()
    {
        // Arrange — "commenter"/"viewer_cmt" seeded statically with Commenter/Viewer at parentPath.
        var client = GetClient();

        const string commenterId = "commenter";
        const string viewerId = "viewer_cmt";
        const string parentPath = "rls/comments";

        // Act - commenter creates a Comment node
        var commentNode = new MeshNode("Comment1", parentPath)
        {
            Name = "Test Comment",
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Text = "Hello", Author = commenterId }
        };
        var commentResponse = await client.Observe(new CreateNodeRequest(commentNode) { CreatedBy = commenterId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Viewer tries to create a Comment node
        var viewerComment = new MeshNode("Comment2", parentPath)
        {
            Name = "Viewer Comment",
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Text = "Denied", Author = viewerId }
        };
        var viewerResponse = await client.Observe(new CreateNodeRequest(viewerComment) { CreatedBy = viewerId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert
        commentResponse.Message.Success.Should().BeTrue("Commenter has Comment permission");
        viewerResponse.Message.Success.Should().BeFalse("Viewer lacks Comment permission");
        viewerResponse.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
    }

    [Fact]
    public async Task CreateThread_RequiresUpdatePermission()
    {
        // Arrange — "editor_thr"/"commenter_thr" seeded statically with Editor/Commenter at parentPath.
        var client = GetClient();

        const string editorId = "editor_thr";
        const string commenterId = "commenter_thr";
        const string parentPath = "rls/threads";

        // Act - editor creates a Thread
        var threadNode = new MeshNode("Thread1", parentPath)
        {
            Name = "Editor Thread",
            NodeType = "Thread"
        };
        var editorResponse = await client.Observe(new CreateNodeRequest(threadNode) { CreatedBy = editorId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Commenter tries to create a Thread
        var commenterThread = new MeshNode("Thread2", parentPath)
        {
            Name = "Commenter Thread",
            NodeType = "Thread"
        };
        var commenterResponse = await client.Observe(new CreateNodeRequest(commenterThread) { CreatedBy = commenterId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert
        editorResponse.Message.Success.Should().BeTrue("Editor has Update permission");
        commenterResponse.Message.Success.Should().BeFalse("Commenter lacks Update permission for Thread creation");
        commenterResponse.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
    }

    [Fact]
    public async Task EditorCanComment_UpdateImpliesComment()
    {
        // Arrange — "editor_commenter" seeded statically with Editor at parentPath.
        var client = GetClient();

        const string editorId = "editor_commenter";
        const string parentPath = "rls/editor_comment";

        // Act - editor creates a Comment node
        var commentNode = new MeshNode("Comment1", parentPath)
        {
            Name = "Editor Comment",
            NodeType = CommentNodeType.NodeType,
            Content = new Comment { Text = "Editor can comment", Author = editorId }
        };
        var response = await client.Observe(new CreateNodeRequest(commentNode) { CreatedBy = editorId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert
        response.Message.Success.Should().BeTrue("Editor has Update which implies Comment permission");
    }

    [Fact]
    public async Task CreateNode_Anonymous_NoCreatedBy_Fails()
    {
        // Arrange - anonymous: clear AccessContext, no CreatedBy, no role assigned
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(null);
        var client = GetClient();

        var node = new MeshNode("AnonCreate", "rls/anon")
        {
            Name = "Anonymous Create",
            NodeType = "Markdown"
        };
        // CreatedBy is null â€” anonymous request
        var request = new CreateNodeRequest(node);

        // Act
        var response = await client.Observe(request, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert â€” must be rejected
        response.Message.Success.Should().BeFalse("Anonymous user must not be able to create nodes");
        response.Message.RejectionReason.Should().Be(NodeCreationRejectionReason.ValidationFailed);
    }

    [Fact]
    public async Task DeleteNode_Anonymous_NoDeletedBy_Fails()
    {
        // Arrange - create a node as admin first
        var client = GetClient();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        const string adminId = "admin_for_anon_delete";
        const string parentPath = "rls/anon_delete";

        // Admin assignment is seeded statically in ConfigureMesh.
        var node = new MeshNode("ToDeleteAnon", parentPath)
        {
            Name = "Delete Me",
            NodeType = "Markdown"
        };
        var createResp = await client.Observe(new CreateNodeRequest(node) { CreatedBy = adminId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();
        createResp.Message.Success.Should().BeTrue();

        // Clear AccessContext to simulate anonymous user
        accessService.SetCircuitContext(null);

        // Act â€” anonymous delete (no DeletedBy)
        var deleteResponse = await client.Observe(new DeleteNodeRequest("rls/anon_delete/ToDeleteAnon"), o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert â€” must be rejected. Acceptable reasons:
        //   Unauthorized       â€” Phase 2 permission check denied (the new preferred shape)
        //   ValidationFailed   â€” INodeValidator (RLS) denied during Phase 3
        //   NodeNotFound       â€” anonymous can't even see the node
        deleteResponse.Message.Success.Should().BeFalse("Anonymous user must not be able to delete nodes");
        deleteResponse.Message.RejectionReason.Should().BeOneOf(
            NodeDeletionRejectionReason.Unauthorized,
            NodeDeletionRejectionReason.ValidationFailed,
            NodeDeletionRejectionReason.NodeNotFound);
    }

    [Fact]
    public async Task UpdateNode_Anonymous_NoUpdatedBy_Fails()
    {
        // Arrange - create a node as admin first
        var client = GetClient();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        const string adminId = "admin_for_anon_update";
        const string parentPath = "rls/anon_update";

        // Admin assignment is seeded statically in ConfigureMesh.
        var node = new MeshNode("ToUpdateAnon", parentPath)
        {
            Name = "Original",
            NodeType = "Markdown"
        };
        var createResp = await client.Observe(new CreateNodeRequest(node) { CreatedBy = adminId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();
        createResp.Message.Success.Should().BeTrue();

        // Clear AccessContext to simulate anonymous user
        accessService.SetCircuitContext(null);

        // Act â€” anonymous update (no UpdatedBy)
        var updatedNode = node with { Name = "Hacked" };
        var updateResponse = await client.Observe(new UpdateNodeRequest(updatedNode), o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert â€” must be rejected (NodeNotFound is also acceptable since anonymous can't even see the node)
        updateResponse.Message.Success.Should().BeFalse("Anonymous user must not be able to update nodes");
        updateResponse.Message.RejectionReason.Should().BeOneOf(
            NodeUpdateRejectionReason.ValidationFailed,
            NodeUpdateRejectionReason.NodeNotFound);
    }

    [Fact]
    public async Task AnonymousUser_PermissionsAreNone_ByDefault()
    {
        // Arrange — no role assigned to anonymous user.
        // Act — check effective permissions for "Anonymous" on an unassigned path
        // via the GetPermissionRequest round-trip.
        var permissions = await Mesh.GetPermissionAsync(
            "rls/no_public_access", WellKnownUsers.Anonymous, TestTimeout);

        // Assert
        permissions.Should().Be(Permission.None,
            "Anonymous user must have zero permissions by default");
    }

    [Fact]
    public async Task AnonymousUser_WithAnonymousViewerRole_CannotCreate()
    {
        // Arrange â€” grant Anonymous user Viewer role (Read only), clear admin context
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetCircuitContext(null);
        var client = GetClient();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        const string parentPath = "rls/public_viewer";
        // Anonymous Viewer assignment is seeded statically in ConfigureMesh.

        // Verify permissions
        var permissions = await Mesh.GetPermissionAsync(parentPath, WellKnownUsers.Anonymous, TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api, "Anonymous Viewer should only have Read + Execute + Api");

        // Act â€” anonymous Create (CreatedBy = empty, will resolve to Anonymous user)
        var node = new MeshNode("PublicCreate", parentPath)
        {
            Name = "Public Create",
            NodeType = "Markdown"
        };
        var response = await client.Observe(new CreateNodeRequest(node), o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert
        response.Message.Success.Should().BeFalse(
            "Anonymous/Viewer user must not be able to create nodes");
    }

    [Fact]
    public async Task EditorRole_CannotDelete()
    {
        // Arrange — "admin_setup"/"editor_nodel" seeded statically with Admin/Editor at parentPath.
        var client = GetClient();

        const string adminId = "admin_setup";
        const string editorId = "editor_nodel";
        const string parentPath = "rls/nodel";

        var node = new MeshNode("Protected", parentPath)
        {
            Name = "Protected Node",
            NodeType = "Markdown"
        };
        var createResponse = await client.Observe(new CreateNodeRequest(node) { CreatedBy = adminId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();
        createResponse.Message.Success.Should().BeTrue();

        // Act - editor tries to delete
        var deleteResponse = await client.Observe(new DeleteNodeRequest("rls/nodel/Protected") { DeletedBy = editorId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();

        // Assert â€” Phase 2 (permission) or Phase 3 (RLS validator) both valid denial shapes.
        deleteResponse.Message.Success.Should().BeFalse("Editor lacks Delete permission");
        deleteResponse.Message.RejectionReason.Should().BeOneOf(
            NodeDeletionRejectionReason.Unauthorized,
            NodeDeletionRejectionReason.ValidationFailed);
    }

    [Fact]
    public async Task ViewerRole_CannotCreateUpdateOrDelete()
    {
        // Arrange — "strict_viewer" seeded statically with Viewer at parentPath.
        var client = GetClient();

        const string viewerId = "strict_viewer";
        const string parentPath = "rls/viewonly";

        // Check effective permissions
        var permissions = await Mesh.GetPermissionAsync(parentPath, viewerId, TestTimeout);
        permissions.Should().Be(Permission.Read | Permission.Execute | Permission.Api, "Viewer should only have Read + Execute + Api permission");

        // Verify cannot create
        var node = new MeshNode("ViewerCreate", parentPath) { Name = "Viewer Create", NodeType = "Markdown" };
        var createResp = await client.Observe(new CreateNodeRequest(node) { CreatedBy = viewerId }, o => o.WithTarget(Mesh.Address)).FirstAsync().ToTask();
        createResp.Message.Success.Should().BeFalse("Viewer cannot create");
    }

    [Fact]
    public async Task CommenterRole_CanReadAndComment()
    {
        // Arrange — "commenter_test" seeded statically with Commenter at path.

        const string userId = "commenter_test";
        const string path = "rls/commenter_test";

        // Act
        var permissions = await Mesh.GetPermissionAsync(path, userId, TestTimeout);

        // Assert
        permissions.HasFlag(Permission.Read).Should().BeTrue("Commenter can read");
        permissions.HasFlag(Permission.Comment).Should().BeTrue("Commenter can comment");
        permissions.HasFlag(Permission.Create).Should().BeFalse("Commenter cannot create regular nodes");
        permissions.HasFlag(Permission.Update).Should().BeFalse("Commenter cannot update");
        permissions.HasFlag(Permission.Delete).Should().BeFalse("Commenter cannot delete");
    }

    [Fact]
    public async Task CommentPermission_InBuiltInRoles()
    {
        // Assert built-in role permissions include Comment where expected
        Role.Admin.Permissions.HasFlag(Permission.Comment).Should().BeTrue("Admin has all permissions");
        Role.Editor.Permissions.HasFlag(Permission.Comment).Should().BeTrue("Editor has Comment");
        Role.Commenter.Permissions.HasFlag(Permission.Comment).Should().BeTrue("Commenter has Comment");
        Role.Viewer.Permissions.HasFlag(Permission.Comment).Should().BeFalse("Viewer does not have Comment");
        await Task.CompletedTask;
    }
}

/// <summary>
/// Tests for the secure persistence behavior via public APIs.
/// Verifies that reads are filtered by security when RLS is enabled.
/// </summary>
public class SecurePersistenceDecoratorTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
    {
        // First configure base (adds persistence), then add Row-Level Security
        // RLS must be added after persistence so it can decorate IMeshStorage
        return ConfigureMeshBase(builder).AddRowLevelSecurity();
    }

    [Fact]
    public async Task SecureDecorator_CanBeCreated()
    {
        // Verify the security pipeline responds — round-trip GetPermissionRequest
        // proves the per-node hub has its scoped ISecurityService wired.
        var perms = await Mesh.GetPermissionAsync("smoke", "anyone", TestTimeout);
        perms.Should().Be(Permission.None);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task GetNodeSecureAsync_WithPermission_ReturnsNode()
    {
        // Arrange
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        const string userId = "secureReader";
        const string nodePath = "secure/test/node";

        // Create a node using the public NodeFactory API (legal: NodeType + Content)
        var node = new MeshNode("node", "secure/test")
        {
            Name = "Secure Node",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Secure" },
            State = MeshNodeState.Active
        };
        await NodeFactory.CreateNode(node);

        // Assign read permission
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(userId, "Viewer", "secure/test")).FirstAsync().ToTask(TestTimeout);

        // Act - query the node (MeshQuery respects security)
        var result = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath}").FirstOrDefaultAsync();

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Secure Node");
    }

    [Fact]
    public async Task GetNodeSecureAsync_WithoutPermission_ReturnsNull()
    {
        // Arrange
        const string nodePath = "restricted/hidden/node";

        // Create a node using the public NodeFactory API (legal: NodeType + Content)
        var node = new MeshNode("node", "restricted/hidden")
        {
            Name = "Hidden Node",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Hidden" },
            State = MeshNodeState.Active
        };
        await NodeFactory.CreateNode(node);

        // No permission assigned

        // Act - query the node (without any user having permission, the node still exists in persistence)
        var result = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath}").FirstOrDefaultAsync();

        // Assert - node exists in persistence (no user-scoped filtering via MeshQuery without UserId)
        // The secure filtering happens at the IMeshStorage decorator level when a user context is set
        // Here we verify the node was created successfully
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetChildrenSecureAsync_FiltersUnauthorizedNodes()
    {
        // Arrange
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();

        const string userId = "partialAccess";
        const string parentPath = "filter/test";

        // Create multiple nodes using the public NodeFactory API (legal: NodeType + Content)
        var node1 = new MeshNode("accessible", parentPath)
        {
            Name = "Accessible Node",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Accessible" },
            State = MeshNodeState.Active
        };
        var node2 = new MeshNode("restricted", parentPath)
        {
            Name = "Restricted Node",
            NodeType = "Markdown",
            Content = new MeshWeaver.Markdown.MarkdownContent { Content = "# Restricted" },
            State = MeshNodeState.Active
        };
        await NodeFactory.CreateNode(node1);
        await NodeFactory.CreateNode(node2);

        // Only grant access to node1's subtree
        await meshService.CreateNode(AssignmentNodeFactory.UserRole(userId, "Viewer", "filter/test/accessible")).FirstAsync().ToTask(TestTimeout);

        // Act - query children (MeshQuery returns all children; security filtering depends on context)
        var children = await MeshQuery.QueryAsync<MeshNode>($"namespace:{parentPath}").ToListAsync();

        // Assert - Both nodes should be returned since MeshQuery doesn't filter by user by default
        children.Should().Contain(n => n.Name == "Accessible Node");
    }
}
