using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// The Feedback-space seed: the <see cref="FeedbackStaticRepoSource"/> that auto-provisions the
/// dedicated Feedback space on every instance. Verifies the partition, the <see cref="PartitionSyncMode.Additive"/>
/// mode (so user-filed feedback is NEVER pruned on re-import), the Space root, and the
/// <c>Public → Contributor</c> grant that makes the board readable + contributable for every user —
/// all platform admins included.
/// </summary>
public class FeedbackStaticRepoSourceTest
{
    private readonly FeedbackStaticRepoSource _source = new();

    [Fact]
    public void Partition_IsFeedback()
        => _source.Partition.Should().Be("Feedback");

    [Fact]
    public void SyncMode_IsAdditive_SoUserFeedbackIsNeverPruned()
        => _source.SyncMode.Should().Be(PartitionSyncMode.Additive);

    [Fact]
    public void PartitionRoot_IsTheFeedbackSpace()
    {
        var root = _source.PartitionRoot;
        root.Should().NotBeNull();
        root!.Id.Should().Be("Feedback");
        root.NodeType.Should().Be("Space");
    }

    [Fact]
    public void SeedsPublicContributorGrant_SoEveryUser_AndAllAdmins_CanContribute()
    {
        var grant = _source.EnumerateSourceNodes()
            .Should().ContainSingle(n => n.NodeType == "AccessAssignment").Which;

        grant.Namespace.Should().Be("Feedback/_Access");
        grant.MainNode.Should().Be("Feedback");   // scope — an empty MainNode would be silently ignored

        var assignment = grant.Content.Should().BeOfType<AccessAssignment>().Which;
        assignment.AccessObject.Should().Be(WellKnownUsers.Public);
        assignment.Roles.Should().ContainSingle(r => r.Role == "Contributor" && !r.Denied);
    }

    [Fact]
    public void LocksTheAccessSubtree_SoPublicCannotSelfEscalate()
    {
        // A BreaksInheritance policy at Feedback/_Access keeps the inheritable Contributor grant from
        // reaching the access-config subtree — otherwise a user's Create would let them write a
        // self-granting AccessAssignment.
        var policy = _source.EnumerateSourceNodes()
            .Should().ContainSingle(n => n.NodeType == "PartitionAccessPolicy").Which;

        policy.Namespace.Should().Be("Feedback/_Access");
        policy.Content.Should().BeOfType<PartitionAccessPolicy>()
            .Which.BreaksInheritance.Should().BeTrue();
    }
}
