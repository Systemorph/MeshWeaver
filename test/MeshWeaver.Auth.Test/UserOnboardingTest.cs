using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

// UserNodeType.WithPortalCreate restricts User-node creation to portal/* identities.
// Tests below set AccessContext.ObjectId = the new userId so RlsNodeValidator's
// own-scope bypass (nodePath == userId) lets the create through — the same shape
// production hits during onboarding when the user already owns their partition.

namespace MeshWeaver.Auth.Test;

/// <summary>
/// Verifies that <see cref="UserScopeGrantHandler"/> fires during onboarding:
/// when a User node is created, the user should automatically get an Admin
/// AccessAssignment on their own partition and have no access to other users' scopes.
/// </summary>
public class UserOnboardingTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>
    /// RLS-enabled mesh WITHOUT PublicAdminAccess so permissions are real.
    /// AddGraph() registers UserScopeGrantHandler via AddUserType().
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder);

    /// <summary>
    /// Pre-warm User type hub in addition to the default AccessAssignment/PartitionAccessPolicy
    /// hubs — creating a User node triggers the User type hub's post-creation pipeline.
    /// </summary>
    protected override void PreWarmNodeTypeHubs()
    {
        base.PreWarmNodeTypeHubs();
        var userTypeNode = Mesh.ServiceProvider.FindStaticNode("User");
        if (userTypeNode?.HubConfiguration is { } config)
        {
            _ = Mesh.GetHostedHub(new Address("User"), config);
        }
    }

    /// <summary>
    /// Switch the test circuit's identity to <paramref name="userId"/> so
    /// RlsNodeValidator's own-scope bypass (nodePath == userId / nodePath
    /// startsWith userId + "/") lets the User-node create through. Production
    /// onboarding hits the same own-scope shape — the user is already authenticated
    /// against their own partition when their User node is being persisted.
    /// </summary>
    private void ImpersonateAsUser(string userId)
        => Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .SetCircuitContext(new AccessContext { ObjectId = userId, Name = userId });

    [Fact(Timeout = 15000)]
    public void CreateUserNode_GrantsSelfAdminRole_OnOwnPartition()
    {
        // Use a unique userId to avoid interference with other tests or seeded data.
        const string userId = "onboard-test-user-a";

        ImpersonateAsUser(userId);

        // Act: create a User node — this triggers UserScopeGrantHandler which
        // fire-and-forget creates the self-assignment at {userId}/_Access/{userId}_Access.
        var userNode = new MeshNode(userId)
        {
            NodeType = UserNodeType.NodeType,
            Name = "Onboard Test User A",
            State = MeshNodeState.Active,
        };
        NodeFactory.CreateNode(userNode).Should().Emit();

        // Wait for the self-assignment to propagate through SecurityService's
        // synced query (100 ms debounce window). GetEffectivePermissions is a hot
        // observable; .Should().Match blocks for the first matching emission.
        var perm = Mesh.GetEffectivePermissions(userId, userId)
            .Should().Match(p => p.HasFlag(Permission.Read));

        perm.HasFlag(Permission.Read).Should().BeTrue(
            "UserScopeGrantHandler should grant Admin (which includes Read) on own partition");
        perm.HasFlag(Permission.Update).Should().BeTrue(
            "Admin role implies Update permission");
    }

    [Fact(Timeout = 15000)]
    public void CreateUserNode_OtherUserHasNoAccess_ToNewPartition()
    {
        const string userId = "onboard-test-user-b";
        const string otherUserId = "some-other-user";

        ImpersonateAsUser(userId);

        var userNode = new MeshNode(userId)
        {
            NodeType = UserNodeType.NodeType,
            Name = "Onboard Test User B",
            State = MeshNodeState.Active,
        };
        NodeFactory.CreateNode(userNode).Should().Emit();

        // Wait for the self-assignment so the partition is fully set up.
        Mesh.GetEffectivePermissions(userId, userId)
            .Should().Match(p => p.HasFlag(Permission.Read));

        // Another user (no AccessAssignment) should have no access.
        var otherPerm = Mesh.GetEffectivePermissions(userId, otherUserId).Should().Emit();
        otherPerm.HasFlag(Permission.Read).Should().BeFalse(
            "Users without an explicit AccessAssignment should not be able to read another user's partition");
        otherPerm.HasFlag(Permission.Update).Should().BeFalse(
            "Users without an explicit AccessAssignment should not be able to update another user's partition");
    }

    [Fact(Timeout = 15000)]
    public void CreateUserNode_SelfAssignmentNodeExists()
    {
        const string userId = "onboard-test-user-c";

        ImpersonateAsUser(userId);

        var userNode = new MeshNode(userId)
        {
            NodeType = UserNodeType.NodeType,
            Name = "Onboard Test User C",
            State = MeshNodeState.Active,
        };
        NodeFactory.CreateNode(userNode).Should().Emit();

        // Wait for permissions so the assignment definitely exists.
        Mesh.GetEffectivePermissions(userId, userId)
            .Should().Match(p => p.HasFlag(Permission.Read));

        // Verify the AccessAssignment node itself is readable via the mesh.
        var assignmentPath = $"{userId}/_Access/{userId}_Access";
        var assignmentNode = ReadNode(assignmentPath).Should().Emit();

        assignmentNode.Should().NotBeNull(
            $"UserScopeGrantHandler should create an AccessAssignment node at {assignmentPath}");
        assignmentNode!.NodeType.Should().Be("AccessAssignment");

        var assignment = assignmentNode.Content as AccessAssignment;
        assignment.Should().NotBeNull();
        assignment!.AccessObject.Should().Be(userId);
        assignment.Roles.Should().ContainSingle(r => r.Role == Role.Admin.Id,
            "self-assignment should carry the Admin role");
    }
}
