using MeshWeaver.Approvals;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for the Approvals module (MeshWeaver.Approvals): node type registration, extension
/// configuration, and the module shape. The <c>Approval</c> data record itself stays
/// platform-level (MeshWeaver.Mesh.Contract) and is covered in ApprovalAndNotificationTest.
/// </summary>
public class ApprovalModuleTest
{
    [Fact]
    public void ApprovalNodeType_HasCorrectNodeType()
    {
        ApprovalNodeType.NodeType.Should().Be("Approval");
    }

    [Fact]
    public void ApprovalNodeType_CreateMeshNode_HasCorrectProperties()
    {
        var node = ApprovalNodeType.CreateMeshNode();

        node.Name.Should().Be("Approval");
        node.Icon.Should().Contain("checkmark.svg");
        node.ExcludeFromContext.Should().Contain("search");
        node.ExcludeFromContext.Should().Contain("create");
        node.HubConfiguration.Should().NotBeNull();
    }

    [Fact]
    public void MeshNode_WithMainNode_GetPrimaryPath_ReturnsMainNode()
    {
        var node = new MeshNode("approval1", "org/project/doc/_approvals")
        {
            MainNode = "org/project/doc",
            NodeType = ApprovalNodeType.NodeType
        };

        node.GetPrimaryPath().Should().Be("org/project/doc");
    }

    [Fact]
    public void ApprovalExtensions_ApprovalPartition_HasCorrectValue()
    {
        ApprovalExtensions.ApprovalPartition.Should().Be("_Approval");
    }

    [Fact]
    public void ConfigureHub_SetsTheApprovalsEnabledMarker_TheOverviewGuardReads()
    {
        // The markdown overview embeds its Approvals section only when HasApprovals() is true —
        // the marker stays in Graph, the module sets it. One drifting apart of the two would
        // silently blank the section, so pin the pair here.
        var configuration = ApprovalExtensions.ConfigureHub(
            new MessageHubConfiguration(null, new Address("test", "approvals")));
        configuration.HasApprovals().Should().BeTrue();
    }
}
