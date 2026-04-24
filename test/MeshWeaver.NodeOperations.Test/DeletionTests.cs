using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
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
        await NodeFactory.CreateNode(
            new MeshNode("delleaf", TestPartition) { Name = "Leaf", NodeType = "Markdown" });

        // Act
        await NodeFactory.DeleteNode($"{TestPartition}/delleaf");

        // Assert — node should be gone
        var result = await ReadNodeAsync($"{TestPartition}/delleaf", TestTimeout);
        result.Should().BeNull("leaf node should be deleted");
    }

    [Fact]
    public async Task Delete_ParentWithChildren_DeletesAll()
    {
        // Arrange — create a parent with two children under TestData partition
        await NodeFactory.CreateNode(
            new MeshNode("del2parent", TestPartition) { Name = "Parent", NodeType = "Group" });
        await NodeFactory.CreateNode(
            new MeshNode("child1", $"{TestPartition}/del2parent") { Name = "Child 1", NodeType = "Markdown" });
        await NodeFactory.CreateNode(
            new MeshNode("child2", $"{TestPartition}/del2parent") { Name = "Child 2", NodeType = "Markdown" });

        // Act — delete parent (should recursively delete children first)
        await NodeFactory.DeleteNode($"{TestPartition}/del2parent");

        // Assert — parent and both children should be gone
        var parent = await ReadNodeAsync($"{TestPartition}/del2parent", TestTimeout);
        parent.Should().BeNull("parent should be deleted");

        // Listing children: queries are correct here — set existence, not single-node content.
        var children = await MeshQuery.QueryAsync<MeshNode>($"namespace:{TestPartition}/del2parent")
            .ToListAsync(TestTimeout);
        children.Should().BeEmpty("all children should be deleted");
    }

    [Fact]
    public async Task Delete_DeeplyNested_DeletesBottomToTop()
    {
        // Arrange — create a 3-level deep hierarchy under TestData
        await NodeFactory.CreateNode(
            new MeshNode("del3root", TestPartition) { Name = "Root", NodeType = "Group" });
        await NodeFactory.CreateNode(
            new MeshNode("mid", $"{TestPartition}/del3root") { Name = "Mid", NodeType = "Group" });
        await NodeFactory.CreateNode(
            new MeshNode("deep", $"{TestPartition}/del3root/mid") { Name = "Deep", NodeType = "Markdown" });

        // Act — delete root
        await NodeFactory.DeleteNode($"{TestPartition}/del3root");

        // Assert — all 3 levels should be gone
        var all = await MeshQuery.QueryAsync<MeshNode>($"path:{TestPartition}/del3root scope:subtree")
            .ToListAsync(TestTimeout);
        all.Should().BeEmpty("entire subtree should be deleted");
    }

    [Fact]
    public async Task Delete_NonExistentNode_Throws()
    {
        // Act & Assert — deleting a non-existent node should throw
        var act = () => NodeFactory.DeleteNode("nonexistent/path/that/does/not/exist").ToTask();
        await act.Should().ThrowAsync<System.Exception>();
    }

    [Fact]
    public async Task Delete_NodeWithSiblings_OnlyDeletesTargetSubtree()
    {
        // Arrange — create two sibling subtrees under TestData partition
        await NodeFactory.CreateNode(
            new MeshNode("del4parent", TestPartition) { Name = "Parent", NodeType = "Group" });
        await NodeFactory.CreateNode(
            new MeshNode("keep", $"{TestPartition}/del4parent") { Name = "Keep", NodeType = "Markdown" });
        await NodeFactory.CreateNode(
            new MeshNode("delete", $"{TestPartition}/del4parent") { Name = "Delete", NodeType = "Markdown" });
        await NodeFactory.CreateNode(
            new MeshNode("child", $"{TestPartition}/del4parent/delete") { Name = "Child", NodeType = "Markdown" });

        // Act — only delete one subtree
        await NodeFactory.DeleteNode($"{TestPartition}/del4parent/delete");

        // Assert — the kept sibling should still exist
        var kept = await ReadNodeAsync($"{TestPartition}/del4parent/keep", TestTimeout);
        kept.Should().NotBeNull("sibling node should not be affected");

        // Subtree existence: query is appropriate here (set, not specific content read)
        var deleted = await MeshQuery.QueryAsync<MeshNode>($"path:{TestPartition}/del4parent/delete scope:subtree")
            .ToListAsync(TestTimeout);
        deleted.Should().BeEmpty("target subtree should be fully deleted");
    }

    [Fact(Skip = "Delete via message routing needs proper node hub target")]
    public async Task Delete_ViaClient_WithDeleteNodeRequest()
    {
        // Arrange — create a node and use the client messaging pattern
        await NodeFactory.CreateNode(
            MeshNode.FromPath("del5/target") with { Name = "Target", NodeType = "Markdown" });

        var client = GetClient();

        // Act — send DeleteNodeRequest via client hub (target the parent namespace hub)
        var response = await client.AwaitResponse(
            new DeleteNodeRequest("del5/target") { DeletedBy = "test-user" },
            o => o.WithTarget(new Address("del5")));

        // Assert
        response.Message.Success.Should().BeTrue("deletion via client should succeed");

        var result = await ReadNodeAsync("del5/target", TestTimeout);
        result.Should().BeNull("node should be deleted");
    }

    /// <summary>
    /// Reproduces the production delete flow where DeleteLayoutArea calls
    /// IMeshService.DeleteNodeAsync from the NODE's hub, not the mesh hub.
    /// In production, DeleteLayoutArea does:
    ///   var nodeFactory = host.Hub.ServiceProvider.GetRequiredService&lt;IMeshService&gt;();
    ///   await nodeFactory.DeleteNode(nodePath);
    /// </summary>
    [Fact]
    public async Task Delete_FromNodeHub_Succeeds()
    {
        // Arrange — create a node and get its hub via the routing service
        var nodePath = $"{TestPartition}/del6target";
        await NodeFactory.CreateNode(
            new MeshNode("del6target", TestPartition) { Name = "Target", NodeType = "Markdown" });

        // Get the node's hosted hub by routing a message to it (creates the hub on demand)
        var nodeAddress = new Address(nodePath);
        var client = GetClient();
        var delivery = client.Post(
            new Data.DataChangeRequest(),
            o => o.WithTarget(nodeAddress));

        // Small delay for hub creation to complete
        await Task.Delay(500);

        var nodeHub = Mesh.GetHostedHub(nodeAddress, HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("node hub should exist after message delivery");

        // Resolve IMeshService from the NODE's hub (reproducing DeleteLayoutArea pattern)
        var nodeService = nodeHub!.ServiceProvider.GetRequiredService<IMeshService>();

        // Act — delete from the node's hub (same as DeleteLayoutArea does)
        await nodeService.DeleteNode(nodePath);

        // Assert — node should be deleted
        var result = await ReadNodeAsync(nodePath, TestTimeout);
        result.Should().BeNull("deletion from node hub should work");
    }
}
