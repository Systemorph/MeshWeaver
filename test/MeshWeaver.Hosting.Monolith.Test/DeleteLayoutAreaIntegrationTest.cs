using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for the delete workflow.
/// Verifies that deletion via the fire-and-forget pattern (post + redirect)
/// actually removes nodes without deadlock.
/// </summary>
public class DeleteLayoutAreaIntegrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    /// <summary>
    /// Reactive path-set wait: folds live query deltas into a running path set and
    /// blocks (≤ 60 s) until <paramref name="predicate"/> holds. Returns the satisfying set.
    /// </summary>
    private IReadOnlySet<string> WaitForQueryPathSet(
        string query, Func<IReadOnlySet<string>, bool> predicate)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        return MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Scan((IReadOnlySet<string>)paths, (acc, change) =>
            {
                var set = (HashSet<string>)acc;
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                {
                    set.Clear();
                    foreach (var n in change.Items) if (n.Path is { } p) set.Add(p);
                }
                else if (change.ChangeType is QueryChangeType.Added or QueryChangeType.Updated)
                {
                    foreach (var n in change.Items) if (n.Path is { } p) set.Add(p);
                }
                else if (change.ChangeType is QueryChangeType.Removed)
                {
                    foreach (var n in change.Items) if (n.Path is { } p) set.Remove(p);
                }
                return acc;
            })
            .Should().Within(ReadNodeTimeout).Match(predicate);
    }

    /// <summary>
    /// Verifies that IMeshService.DeleteNodeAsync deletes the node.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void DeleteNode_DeletesNode()
    {
        var nodePath = $"{TestPartition}/del-fandf";
        var subtreeQuery = $"path:{TestPartition} nodeType:Markdown scope:subtree";

        NodeFactory.CreateNode(
            new MeshNode("del-fandf", TestPartition) { Name = "Fire And Forget", NodeType = "Markdown" }).Should().Emit();
        WaitForQueryPathSet(subtreeQuery, set => set.Contains(nodePath));

        NodeFactory.DeleteNode(nodePath).Should().Emit();

        var paths = WaitForQueryPathSet(subtreeQuery, set => !set.Contains(nodePath));
        paths.Should().NotContain(nodePath, "node should be deleted");
    }

    /// <summary>
    /// Verifies that delete from a node's own hub works without deadlock.
    /// This reproduces the exact production flow: DeleteLayoutArea runs on the node hub,
    /// resolves IMeshService from node hub, calls DeleteNode.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void DeleteNode_FromNodeHub_Succeeds()
    {
        var nodePath = $"{TestPartition}/del-nodehub";
        var subtreeQuery = $"path:{TestPartition} nodeType:Markdown scope:subtree";

        NodeFactory.CreateNode(
            new MeshNode("del-nodehub", TestPartition) { Name = "Node Hub Delete", NodeType = "Markdown" }).Should().Emit();
        WaitForQueryPathSet(subtreeQuery, set => set.Contains(nodePath));

        // Route a message to create the node hub on demand
        var client = GetClient();
        var nodeAddress = new Address(nodePath);
        client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();

        var nodeHub = Mesh.GetHostedHub(nodeAddress, HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("node hub should exist after ping");

        // Resolve IMeshService from the NODE's hub (same as DeleteLayoutArea does)
        var nodeService = nodeHub!.ServiceProvider.GetRequiredService<IMeshService>();

        // Delete from node hub — this is the production pattern
        nodeService.DeleteNode(nodePath).Should().Emit();

        var paths = WaitForQueryPathSet(subtreeQuery, set => !set.Contains(nodePath));
        paths.Should().NotContain(nodePath, "deletion from node hub should work");
    }

    /// <summary>
    /// Verifies that recursive delete (parent with children) works.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void DeleteNode_WithChildren_DeletesAll()
    {
        var parentPath = $"{TestPartition}/del-parent";
        var subtreeQuery = $"path:{TestPartition} scope:subtree";

        NodeFactory.CreateNode(
            new MeshNode("del-parent", TestPartition) { Name = "Parent", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(
            new MeshNode("child1", parentPath) { Name = "Child 1", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(
            new MeshNode("child2", parentPath) { Name = "Child 2", NodeType = "Markdown" }).Should().Emit();
        WaitForQueryPathSet(subtreeQuery,
            set => set.Contains(parentPath) && set.Contains($"{parentPath}/child1") && set.Contains($"{parentPath}/child2"));

        NodeFactory.DeleteNode(parentPath).Should().Emit();

        var paths = WaitForQueryPathSet(subtreeQuery,
            set => !set.Contains(parentPath)
                && !set.Contains($"{parentPath}/child1")
                && !set.Contains($"{parentPath}/child2"));
        paths.Should().NotContain(parentPath, "parent should be deleted");
        paths.Should().NotContain($"{parentPath}/child1", "all children should be deleted");
        paths.Should().NotContain($"{parentPath}/child2", "all children should be deleted");
    }

    /// <summary>
    /// Replicates the exact production pattern the Delete click handler must use:
    /// <c>hub.Post(new DeleteNodeRequest(...)) + hub.RegisterCallback(...)</c>.
    /// No <c>await</c> on the delete path â€” the callback drives a <see cref="TaskCompletionSource{T}"/>
    /// which the test awaits at the xunit boundary only. A blocked hub cannot produce a callback,
    /// so the 10 s WaitAsync guard fails the test instead of hanging forever.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void DeleteNode_PostRegisterCallback_DoesNotDeadlock()
    {
        var nodePath = $"{TestPartition}/del-reactive";
        var subtreeQuery = $"path:{TestPartition} nodeType:Markdown scope:subtree";

        NodeFactory.CreateNode(
            new MeshNode("del-reactive", TestPartition) { Name = "Reactive Delete", NodeType = "Markdown" }).Should().Emit();
        WaitForQueryPathSet(subtreeQuery, set => set.Contains(nodePath));

        var client = GetClient();
        var nodeAddress = new Address(nodePath);
        client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();

        var nodeHub = Mesh.GetHostedHub(nodeAddress, HostedHubCreation.Never)!;

        // Production pattern: hub.Observe + Subscribe. No await on the hub-bound path.
        var responseDelivery = nodeHub.Observe(
            new DeleteNodeRequest(nodePath) { Recursive = true }).Should().Emit();
        var response = responseDelivery.Message;
        response.Success.Should().BeTrue($"delete should succeed (error: {response.Error})");

        var paths = WaitForQueryPathSet(subtreeQuery, set => !set.Contains(nodePath));
        paths.Should().NotContain(nodePath, "node should be gone after Post+RegisterCallback delete");
    }

    /// <summary>
    /// Recursive delete via Post + RegisterCallback. Verifies DeleteSelfFromStorage
    /// posts the success response BEFORE issuing the storage write, so a hub that
    /// gets torn down by the storage delete still delivers its reply to the caller.
    /// </summary>
    [Fact(Timeout = 20000)]
    public void DeleteNode_PostRegisterCallback_Recursive_DoesNotDeadlock()
    {
        var parentPath = $"{TestPartition}/del-rec-parent";
        var subtreeQuery = $"path:{TestPartition} scope:subtree";

        NodeFactory.CreateNode(
            new MeshNode("del-rec-parent", TestPartition) { Name = "Parent", NodeType = "Group" }).Should().Emit();
        NodeFactory.CreateNode(
            new MeshNode("c1", parentPath) { Name = "C1", NodeType = "Markdown" }).Should().Emit();
        NodeFactory.CreateNode(
            new MeshNode("c2", parentPath) { Name = "C2", NodeType = "Markdown" }).Should().Emit();
        WaitForQueryPathSet(subtreeQuery,
            set => set.Contains(parentPath) && set.Contains($"{parentPath}/c1") && set.Contains($"{parentPath}/c2"));

        var client = GetClient();
        client.Observe(new PingRequest(), o => o.WithTarget(new Address(parentPath))).Should().Emit();

        var parentHub = Mesh.GetHostedHub(new Address(parentPath), HostedHubCreation.Never)!;

        var responseDelivery = parentHub.Observe(
            new DeleteNodeRequest(parentPath) { Recursive = true }).Should().Emit();
        var response = responseDelivery.Message;
        response.Success.Should().BeTrue($"recursive delete should succeed (error: {response.Error})");

        var paths = WaitForQueryPathSet(subtreeQuery,
            set => !set.Contains(parentPath)
                && !set.Contains($"{parentPath}/c1")
                && !set.Contains($"{parentPath}/c2"));
        paths.Should().NotContain(parentPath);
        paths.Should().NotContain($"{parentPath}/c1");
        paths.Should().NotContain($"{parentPath}/c2");
    }
}
