using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using System.Reactive.Linq;
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
    // 90 s: Linux CI's per-message-hub activation routinely takes >45 s when
    // the suite is mid-run with many AccessAssignment / PartitionAccessPolicy
    // synced queries firing in the background — Delete_FromNodeHub_Succeeds
    // hits a STALE-CALLBACK at GetDataRequest@{nodePath}(44+s) in CI. The
    // earlier revert of this bump (195d1b6d1) flipped the test back to red.
    // Locally the test completes in ~10 s; the higher ceiling absorbs the
    // slow path without masking a genuine hang.
    private CancellationToken TestTimeout => new CancellationTokenSource(90.Seconds()).Token;

    // The base class watchdog defaults are 30 s soft / 60 s hard. CI's slow
    // hub-activation pushes Delete_FromNodeHub_Succeeds past the 60 s hard
    // deadline (run 26557749128) even though the test's own [Fact(Timeout)]
    // and TestTimeout were generously sized. Override both so the watchdog
    // tracks the same ceiling the test budgets allow.
    protected override TimeSpan TestSoftDeadline => TimeSpan.FromSeconds(60);
    protected override TimeSpan TestHardDeadline => TimeSpan.FromSeconds(120);

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder);

    [Fact]
    public async Task Delete_LeafNode_Succeeds()
    {
        // Arrange â€” create a single leaf node under TestData
        await NodeFactory.CreateNode(
            new MeshNode("delleaf", TestPartition) { Name = "Leaf", NodeType = "Markdown" }).Should().Emit();

        // Act
        await NodeFactory.DeleteNode($"{TestPartition}/delleaf").Should().Emit();

        // Assert — poll until cache invalidation propagates. A single one-shot
        // ReadNode races the delete fan-out under CI load.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => ReadNode($"{TestPartition}/delleaf"))
            .Should().Within(10.Seconds()).Match(n => n is null);
    }

    [Fact]
    public async Task Delete_ParentWithChildren_DeletesAll()
    {
        // Arrange â€” create a parent with two children under TestData partition
        await NodeFactory.CreateNode(
            new MeshNode("del2parent", TestPartition) { Name = "Parent", NodeType = "Group" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("child1", $"{TestPartition}/del2parent") { Name = "Child 1", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("child2", $"{TestPartition}/del2parent") { Name = "Child 2", NodeType = "Markdown" }).Should().Emit();

        // Act — delete parent (should recursively delete children first)
        await NodeFactory.DeleteNode($"{TestPartition}/del2parent").Should().Emit();

        // Assert — parent and all children should be gone. Poll because the
        // recursive delete fan-out doesn't complete synchronously with the
        // root response; the per-leaf cache.Invalidate + per-hub disposal ripples
        // through the children afterwards.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => ReadNode($"{TestPartition}/del2parent")
                .SelectMany(parent => MeshQuery
                    .Query<MeshNode>(MeshQueryRequest.FromQuery($"namespace:{TestPartition}/del2parent"))
                    .Where(c => c.ChangeType == QueryChangeType.Initial)
                    .Take(1)
                    .Select(c => (parent, count: c.Items.Count))))
            .Should().Within(15.Seconds()).Match(t => t.parent is null && t.count == 0);
    }

    [Fact]
    public async Task Delete_DeeplyNested_DeletesBottomToTop()
    {
        // Arrange â€” create a 3-level deep hierarchy under TestData
        await NodeFactory.CreateNode(
            new MeshNode("del3root", TestPartition) { Name = "Root", NodeType = "Group" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("mid", $"{TestPartition}/del3root") { Name = "Mid", NodeType = "Group" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("deep", $"{TestPartition}/del3root/mid") { Name = "Deep", NodeType = "Markdown" }).Should().Emit();

        // Act — delete root
        await NodeFactory.DeleteNode($"{TestPartition}/del3root").Should().Emit();

        // Assert — entire subtree should be gone. Poll the AUTHORITATIVE per-node read
        // (owner-hub round-trip via ReadNode, which emits null on NotFound) for every
        // node in the hierarchy, not the lagged `QueryAsync scope:subtree` catalog index. The
        // recursive delete writes bottom-to-top (deep → mid → root); a query against the
        // eventually-consistent catalog can lag the actual per-node-hub disposal under CI load
        // and trip the deadline even though the nodes are gone. This is the same authoritative
        // poll the sibling Delete_ParentWithChildren_DeletesAll uses.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => ReadNode($"{TestPartition}/del3root")
                .SelectMany(root => ReadNode($"{TestPartition}/del3root/mid")
                    .SelectMany(mid => ReadNode($"{TestPartition}/del3root/mid/deep")
                        .Select(deep => root is null && mid is null && deep is null))))
            .Should().Within(30.Seconds()).Match(allGone => allGone);
    }

    [Fact]
    public async Task Delete_NonExistentNode_Throws()
    {
        // Act & Assert â€” deleting a non-existent node should throw
        Func<Task> act = async () => await NodeFactory.DeleteNode("nonexistent/path/that/does/not/exist").FirstAsync().ToTask();
        await act.Should().ThrowAsync<System.Exception>();
    }

    [Fact]
    public async Task Delete_NodeWithSiblings_OnlyDeletesTargetSubtree()
    {
        // Arrange â€” create two sibling subtrees under TestData partition
        await NodeFactory.CreateNode(
            new MeshNode("del4parent", TestPartition) { Name = "Parent", NodeType = "Group" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("keep", $"{TestPartition}/del4parent") { Name = "Keep", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("delete", $"{TestPartition}/del4parent") { Name = "Delete", NodeType = "Markdown" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("child", $"{TestPartition}/del4parent/delete") { Name = "Child", NodeType = "Markdown" }).Should().Emit();

        // Act â€” only delete one subtree
        await NodeFactory.DeleteNode($"{TestPartition}/del4parent/delete").Should().Emit();

        // Assert â€” the kept sibling should still exist
        var kept = await ReadNode($"{TestPartition}/del4parent/keep").Should().Emit();
        kept.Should().NotBeNull("sibling node should not be affected");

        // Subtree existence: query is appropriate here (set, not specific content read)
        var deleted = (await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"path:{TestPartition}/del4parent/delete scope:subtree"))
            .Should().Match(c => c.ChangeType == QueryChangeType.Initial)).Items;
        deleted.Should().BeEmpty("target subtree should be fully deleted");
    }

    [Fact]
    public async Task Delete_ViaClient_WithDeleteNodeRequest()
    {
        // Arrange — create a node and use the client messaging pattern. Use a
        // root path under TestPartition so the parent hub exists (the routing
        // service refuses to route to "del5" otherwise — there's no node there).
        var nodePath = $"{TestPartition}/del5target";
        await NodeFactory.CreateNode(
            new MeshNode("del5target", TestPartition) { Name = "Target", NodeType = "Markdown" }).Should().Emit();

        var client = GetClient();

        // Act — send DeleteNodeRequest via client hub. Target the mesh hub
        // (CRUD handlers are registered there); the mesh hub forwards to the
        // owning per-node hub via the standard HandleDeleteNodeRequest pipeline.
        var response = await client.Observe(
                new DeleteNodeRequest(nodePath) { DeletedBy = "test-user" },
                o => o.WithTarget(Mesh.Address))
            .Should().Emit();

        // Assert
        response.Message.Success.Should().BeTrue("deletion via client should succeed");

        // Poll the authoritative per-node read until the delete fan-out settles.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => ReadNode(nodePath))
            .Should().Within(10.Seconds()).Match(n => n is null, "node should be deleted");
    }

    /// <summary>
    /// Reproduces the production delete flow where DeleteLayoutArea calls
    /// IMeshService.DeleteNodeAsync from the NODE's hub, not the mesh hub.
    /// In production, DeleteLayoutArea does:
    ///   var nodeFactory = host.Hub.ServiceProvider.GetRequiredService&lt;IMeshService&gt;();
    ///   await nodeFactory.DeleteNode(nodePath);
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task Delete_FromNodeHub_Succeeds()
    {
        // Arrange â€” create a node and get its hub via the routing service
        var nodePath = $"{TestPartition}/del6target";
        await NodeFactory.CreateNode(
            new MeshNode("del6target", TestPartition) { Name = "Target", NodeType = "Markdown" }).Should().Emit();

        // Get the node's hosted hub by routing a message to it (creates the hub on demand)
        var nodeAddress = new Address(nodePath);
        var client = GetClient();
        client.Post(
            new Data.DataChangeRequest(),
            o => o.WithTarget(nodeAddress));

        // Stream-poll for hub creation to complete — replaces a fixed
        // Task.Delay(500). GetHostedHub(...HostedHubCreation.Never) returns
        // null until the per-node hub is activated, which happens lazily off
        // the first inbound message (the Post above). Polling absorbs the
        // activation latency without racing.
        var nodeHub = await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Select(_ => Mesh.GetHostedHub(nodeAddress, HostedHubCreation.Never))
            .Where(h => h is not null)
            .Should().Within(10.Seconds()).Emit();
        nodeHub.Should().NotBeNull("node hub should exist after message delivery");

        // Resolve IMeshService from the NODE's hub (reproducing DeleteLayoutArea pattern)
        var nodeService = nodeHub!.ServiceProvider.GetRequiredService<IMeshService>();

        // Act â€” delete from the node's hub (same as DeleteLayoutArea does)
        await nodeService.DeleteNode(nodePath).Should().Emit();

        // Assert â€” node should be deleted. Poll the authoritative per-node read.
        await Observable.Interval(TimeSpan.FromMilliseconds(50))
            .StartWith(0L)
            .SelectMany(_ => ReadNode(nodePath))
            .Should().Within(10.Seconds()).Match(n => n is null, "deletion from node hub should work");
    }
}
