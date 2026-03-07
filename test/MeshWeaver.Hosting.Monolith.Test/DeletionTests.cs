using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Tests for the recursive deletion pattern:
/// - Delete request is sent to a node
/// - Handler recursively deletes all children (bottom-to-top)
/// - If any child cannot be deleted, the parent is not deleted
/// - All child responses are collected before deciding
/// </summary>
public class DeletionTests(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    [Fact]
    public async Task Delete_LeafNode_Succeeds()
    {
        // Arrange — create a single leaf node
        await NodeFactory.CreateNodeAsync(
            MeshNode.FromPath("del/leaf") with { Name = "Leaf", NodeType = "Markdown" });

        // Act
        await NodeFactory.DeleteNodeAsync("del/leaf");

        // Assert — node should be gone
        var result = await MeshQuery.QueryAsync<MeshNode>("path:del/leaf scope:exact")
            .FirstOrDefaultAsync(TestTimeout);
        result.Should().BeNull("leaf node should be deleted");
    }

    [Fact]
    public async Task Delete_ParentWithChildren_DeletesAll()
    {
        // Arrange — create a parent with two children
        await NodeFactory.CreateNodeAsync(
            MeshNode.FromPath("del2/parent") with { Name = "Parent", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(
            MeshNode.FromPath("del2/parent/child1") with { Name = "Child 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(
            MeshNode.FromPath("del2/parent/child2") with { Name = "Child 2", NodeType = "Code" });

        // Act — delete parent (should recursively delete children first)
        await NodeFactory.DeleteNodeAsync("del2/parent");

        // Assert — parent and both children should be gone
        var parent = await MeshQuery.QueryAsync<MeshNode>("path:del2/parent scope:exact")
            .FirstOrDefaultAsync(TestTimeout);
        parent.Should().BeNull("parent should be deleted");

        var children = await MeshQuery.QueryAsync<MeshNode>("path:del2/parent scope:children")
            .ToListAsync(TestTimeout);
        children.Should().BeEmpty("all children should be deleted");
    }

    [Fact]
    public async Task Delete_DeeplyNested_DeletesBottomToTop()
    {
        // Arrange — create a 3-level deep hierarchy
        await NodeFactory.CreateNodeAsync(
            MeshNode.FromPath("del3/root") with { Name = "Root", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(
            MeshNode.FromPath("del3/root/mid") with { Name = "Mid", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(
            MeshNode.FromPath("del3/root/mid/deep") with { Name = "Deep", NodeType = "Markdown" });

        // Act — delete root
        await NodeFactory.DeleteNodeAsync("del3/root");

        // Assert — all 3 levels should be gone
        var all = await MeshQuery.QueryAsync<MeshNode>("path:del3/root scope:subtree")
            .ToListAsync(TestTimeout);
        all.Should().BeEmpty("entire subtree should be deleted");
    }

    [Fact]
    public async Task Delete_NonExistentNode_Throws()
    {
        // Act & Assert — deleting a non-existent node should throw
        var act = () => NodeFactory.DeleteNodeAsync("nonexistent/path/that/does/not/exist");
        await act.Should().ThrowAsync<System.InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [Fact]
    public async Task Delete_NodeWithSiblings_OnlyDeletesTargetSubtree()
    {
        // Arrange — create two sibling subtrees
        await NodeFactory.CreateNodeAsync(
            MeshNode.FromPath("del4/parent/keep") with { Name = "Keep", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(
            MeshNode.FromPath("del4/parent/delete") with { Name = "Delete", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(
            MeshNode.FromPath("del4/parent/delete/child") with { Name = "Child", NodeType = "Code" });

        // Act — only delete one subtree
        await NodeFactory.DeleteNodeAsync("del4/parent/delete");

        // Assert — the kept sibling should still exist
        var kept = await MeshQuery.QueryAsync<MeshNode>("path:del4/parent/keep scope:exact")
            .FirstOrDefaultAsync(TestTimeout);
        kept.Should().NotBeNull("sibling node should not be affected");

        var deleted = await MeshQuery.QueryAsync<MeshNode>("path:del4/parent/delete scope:subtree")
            .ToListAsync(TestTimeout);
        deleted.Should().BeEmpty("target subtree should be fully deleted");
    }

    [Fact]
    public async Task Delete_ViaClient_WithDeleteNodeRequest()
    {
        // Arrange — create a node and use the client messaging pattern
        await NodeFactory.CreateNodeAsync(
            MeshNode.FromPath("del5/target") with { Name = "Target", NodeType = "Markdown" });

        var client = GetClient();

        // Act — send DeleteNodeRequest via client hub
        var response = await client.AwaitResponse(
            new DeleteNodeRequest("del5/target") { DeletedBy = "test-user" },
            o => o.WithTarget(Mesh.Address),
            TestTimeout);

        // Assert
        response.Message.Success.Should().BeTrue("deletion via client should succeed");

        var result = await MeshQuery.QueryAsync<MeshNode>("path:del5/target scope:exact")
            .FirstOrDefaultAsync(TestTimeout);
        result.Should().BeNull("node should be deleted");
    }
}
