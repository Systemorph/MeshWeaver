using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Graph.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Unit coverage for <see cref="PartitionRootDeletionGuard"/> — the structural guard that makes a
/// USER partition root undeletable by an interactive caller (the defence-in-depth behind the
/// node-menu-delete-wiped-my-partition incident). Pure validator logic, no mesh needed.
/// </summary>
public class PartitionRootDeletionGuardTest
{
    private static readonly PartitionRootDeletionGuard Guard = new();

    private static Task<NodeValidationResult> Validate(MeshNode node, string? userId) =>
        Guard.Validate(new NodeValidationContext
        {
            Operation = NodeOperation.Delete,
            Node = node,
            AccessContext = userId is null ? null : new AccessContext { ObjectId = userId, Name = userId },
        }).FirstAsync().ToTask();

    /// <summary>The exact shape UserOnboardingService.CreateUser writes: id={user}, namespace='', User.</summary>
    private static MeshNode UserRoot(string id) => new(id) { NodeType = "User", State = MeshNodeState.Active };

    [Fact]
    public void Guard_HandlesOnlyDelete()
        => Assert.Equal(NodeOperation.Delete, Assert.Single(Guard.SupportedOperations));

    [Fact]
    public async Task InteractiveUser_CannotDelete_UserPartitionRoot()
    {
        var result = await Validate(UserRoot("rbuergi"), "rbuergi");

        Assert.False(result.IsValid);
        Assert.Equal(NodeRejectionReason.Unauthorized, result.Reason);
        Assert.Contains("partition root", result.ErrorMessage!);
    }

    [Fact]
    public async Task Admin_OtherUser_StillCannotDelete_AUserPartitionRoot()
    {
        // Even a different (would-be admin) interactive caller is blocked — only System is exempt.
        var result = await Validate(UserRoot("rbuergi"), "someadmin");
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task System_MayDelete_UserPartitionRoot()
    {
        // Deliberate infrastructure / off-boarding runs as System and is exempt.
        var result = await Validate(UserRoot("rbuergi"), WellKnownUsers.System);
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task SpaceRoot_IsNotProtected()
    {
        // A Space owns a partition too but is deletable (it carries an explicit teardown handler).
        var result = await Validate(new MeshNode("acme") { NodeType = "Space", State = MeshNodeState.Active }, "rbuergi");
        Assert.True(result.IsValid);
    }

    [Fact]
    public async Task ChildAndSatelliteNodes_AreNotProtected()
    {
        // A namespaced node under the user's partition is NOT the root — deleting a thread/child must
        // remain possible (that is the whole point of the fix).
        Assert.True((await Validate(new MeshNode("mythread", "rbuergi/_Thread") { NodeType = "User" }, "rbuergi")).IsValid);
        Assert.True((await Validate(new MeshNode("note", "rbuergi") { NodeType = "Markdown" }, "rbuergi")).IsValid);
    }

    [Theory]
    [InlineData("rbuergi", "", "User", true)]        // canonical root
    [InlineData("rbuergi", "", "user", true)]        // NodeType case-insensitive
    [InlineData("acme", "", "Space", false)]         // Space root
    [InlineData("rbuergi", "somens", "User", false)] // namespaced → not a root
    [InlineData("thread", "rbuergi/_Thread", "User", false)] // satellite
    [InlineData("_Access", "", "User", false)]       // '_'-prefixed reserved segment
    public void IsUserPartitionRoot_ClassifiesShapes(string id, string ns, string nodeType, bool expected)
    {
        var node = new MeshNode(id, ns) { NodeType = nodeType, State = MeshNodeState.Active };
        Assert.Equal(expected, PartitionRootDeletionGuard.IsUserPartitionRoot(node));
    }

    [Fact]
    public void IsUserPartitionRoot_Null_IsFalse()
        => Assert.False(PartitionRootDeletionGuard.IsUserPartitionRoot(null));
}
