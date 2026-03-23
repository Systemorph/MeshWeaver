using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.NodeOperations.Test;

/// <summary>
/// Tests for the recursive deletion pattern:
/// - Delete request is sent to a node
/// - Handler recursively deletes all children (bottom-to-top)
/// - If any child cannot be deleted, the parent is not deleted
/// - All child responses are collected before deciding
/// </summary>
public class DeletionTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(45.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    [Fact]
    public async Task Delete_LeafNode_Succeeds()
    {
        // Arrange — create a single leaf node under TestData
        await NodeFactory.CreateNodeAsync(
            new MeshNode("delleaf", TestPartition) { Name = "Leaf", NodeType = "Markdown" });

        // Act
        await NodeFactory.DeleteNodeAsync($"{TestPartition}/delleaf");

        // Assert — node should be gone
        var result = await MeshQuery.QueryAsync<MeshNode>($"path:{TestPartition}/delleaf")
            .FirstOrDefaultAsync(TestTimeout);
        result.Should().BeNull("leaf node should be deleted");
    }

    [Fact]
    public async Task Delete_ParentWithChildren_DeletesAll()
    {
        // Arrange — create a parent with two children under TestData partition
        await NodeFactory.CreateNodeAsync(
            new MeshNode("del2parent", TestPartition) { Name = "Parent", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("child1", $"{TestPartition}/del2parent") { Name = "Child 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("child2", $"{TestPartition}/del2parent") { Name = "Child 2", NodeType = "Markdown" });

        // Act — delete parent (should recursively delete children first)
        await NodeFactory.DeleteNodeAsync($"{TestPartition}/del2parent");

        // Assert — parent and both children should be gone
        var parent = await MeshQuery.QueryAsync<MeshNode>($"path:{TestPartition}/del2parent")
            .FirstOrDefaultAsync(TestTimeout);
        parent.Should().BeNull("parent should be deleted");

        var children = await MeshQuery.QueryAsync<MeshNode>($"namespace:{TestPartition}/del2parent")
            .ToListAsync(TestTimeout);
        children.Should().BeEmpty("all children should be deleted");
    }

    [Fact]
    public async Task Delete_DeeplyNested_DeletesBottomToTop()
    {
        // Arrange — create a 3-level deep hierarchy under TestData
        await NodeFactory.CreateNodeAsync(
            new MeshNode("del3root", TestPartition) { Name = "Root", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("mid", $"{TestPartition}/del3root") { Name = "Mid", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("deep", $"{TestPartition}/del3root/mid") { Name = "Deep", NodeType = "Markdown" });

        // Act — delete root
        await NodeFactory.DeleteNodeAsync($"{TestPartition}/del3root");

        // Assert — all 3 levels should be gone
        var all = await MeshQuery.QueryAsync<MeshNode>($"path:{TestPartition}/del3root scope:subtree")
            .ToListAsync(TestTimeout);
        all.Should().BeEmpty("entire subtree should be deleted");
    }

    [Fact]
    public async Task Delete_NonExistentNode_Throws()
    {
        // Act & Assert — deleting a non-existent node should throw
        var act = () => NodeFactory.DeleteNodeAsync("nonexistent/path/that/does/not/exist");
        await act.Should().ThrowAsync<System.Exception>();
    }

    [Fact]
    public async Task Delete_NodeWithSiblings_OnlyDeletesTargetSubtree()
    {
        // Arrange — create two sibling subtrees under TestData partition
        await NodeFactory.CreateNodeAsync(
            new MeshNode("del4parent", TestPartition) { Name = "Parent", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("keep", $"{TestPartition}/del4parent") { Name = "Keep", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("delete", $"{TestPartition}/del4parent") { Name = "Delete", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("child", $"{TestPartition}/del4parent/delete") { Name = "Child", NodeType = "Markdown" });

        // Act — only delete one subtree
        await NodeFactory.DeleteNodeAsync($"{TestPartition}/del4parent/delete");

        // Assert — the kept sibling should still exist
        var kept = await MeshQuery.QueryAsync<MeshNode>($"path:{TestPartition}/del4parent/keep")
            .FirstOrDefaultAsync(TestTimeout);
        kept.Should().NotBeNull("sibling node should not be affected");

        var deleted = await MeshQuery.QueryAsync<MeshNode>($"path:{TestPartition}/del4parent/delete scope:subtree")
            .ToListAsync(TestTimeout);
        deleted.Should().BeEmpty("target subtree should be fully deleted");
    }

    [Fact(Skip = "Delete via message routing needs proper node hub target")]
    public async Task Delete_ViaClient_WithDeleteNodeRequest()
    {
        // Arrange — create a node and use the client messaging pattern
        await NodeFactory.CreateNodeAsync(
            MeshNode.FromPath("del5/target") with { Name = "Target", NodeType = "Markdown" });

        var client = GetClient();

        // Act — send DeleteNodeRequest via client hub (target the parent namespace hub)
        var response = await client.AwaitResponse(
            new DeleteNodeRequest("del5/target") { DeletedBy = "test-user" },
            o => o.WithTarget(new Address("del5")),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeTrue("deletion via client should succeed");

        var result = await MeshQuery.QueryAsync<MeshNode>("path:del5/target")
            .FirstOrDefaultAsync(TestTimeout);
        result.Should().BeNull("node should be deleted");
    }
}
