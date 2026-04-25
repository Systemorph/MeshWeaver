using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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
    /// <summary>
    /// Verifies that IMeshService.DeleteNodeAsync deletes the node.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeleteNode_DeletesNode()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodePath = $"{TestPartition}/del-fandf";
        var subtreeQuery = $"path:{TestPartition} nodeType:Markdown scope:subtree";

        await NodeFactory.CreateNode(
            new MeshNode("del-fandf", TestPartition) { Name = "Fire And Forget", NodeType = "Markdown" });
        await WaitForQueryPathSetAsync(subtreeQuery, set => set.Contains(nodePath), ct);

        await NodeFactory.DeleteNode(nodePath);

        var paths = await WaitForQueryPathSetAsync(subtreeQuery, set => !set.Contains(nodePath), ct);
        paths.Should().NotContain(nodePath, "node should be deleted");
    }

    /// <summary>
    /// Verifies that delete from a node's own hub works without deadlock.
    /// This reproduces the exact production flow: DeleteLayoutArea runs on the node hub,
    /// resolves IMeshService from node hub, calls DeleteNode.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeleteNode_FromNodeHub_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodePath = $"{TestPartition}/del-nodehub";
        var subtreeQuery = $"path:{TestPartition} nodeType:Markdown scope:subtree";

        await NodeFactory.CreateNode(
            new MeshNode("del-nodehub", TestPartition) { Name = "Node Hub Delete", NodeType = "Markdown" });
        await WaitForQueryPathSetAsync(subtreeQuery, set => set.Contains(nodePath), ct);

        // Route a message to create the node hub on demand
        var client = GetClient();
        var nodeAddress = new Address(nodePath);
        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).FirstAsync().ToTask();

        var nodeHub = Mesh.GetHostedHub(nodeAddress, HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("node hub should exist after ping");

        // Resolve IMeshService from the NODE's hub (same as DeleteLayoutArea does)
        var nodeService = nodeHub!.ServiceProvider.GetRequiredService<IMeshService>();

        // Delete from node hub â€” this is the production pattern
        await nodeService.DeleteNode(nodePath);

        var paths = await WaitForQueryPathSetAsync(subtreeQuery, set => !set.Contains(nodePath), ct);
        paths.Should().NotContain(nodePath, "deletion from node hub should work");
    }

    /// <summary>
    /// Verifies that recursive delete (parent with children) works.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeleteNode_WithChildren_DeletesAll()
    {
        var ct = TestContext.Current.CancellationToken;
        var parentPath = $"{TestPartition}/del-parent";
        var subtreeQuery = $"path:{TestPartition} scope:subtree";

        await NodeFactory.CreateNode(
            new MeshNode("del-parent", TestPartition) { Name = "Parent", NodeType = "Group" });
        await NodeFactory.CreateNode(
            new MeshNode("child1", parentPath) { Name = "Child 1", NodeType = "Markdown" });
        await NodeFactory.CreateNode(
            new MeshNode("child2", parentPath) { Name = "Child 2", NodeType = "Markdown" });
        await WaitForQueryPathSetAsync(subtreeQuery,
            set => set.Contains(parentPath) && set.Contains($"{parentPath}/child1") && set.Contains($"{parentPath}/child2"), ct);

        await NodeFactory.DeleteNode(parentPath);

        var paths = await WaitForQueryPathSetAsync(subtreeQuery,
            set => !set.Contains(parentPath)
                && !set.Contains($"{parentPath}/child1")
                && !set.Contains($"{parentPath}/child2"), ct);
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
    public async Task DeleteNode_PostRegisterCallback_DoesNotDeadlock()
    {
        var ct = TestContext.Current.CancellationToken;
        var nodePath = $"{TestPartition}/del-reactive";
        var subtreeQuery = $"path:{TestPartition} nodeType:Markdown scope:subtree";

        await NodeFactory.CreateNode(
            new MeshNode("del-reactive", TestPartition) { Name = "Reactive Delete", NodeType = "Markdown" });
        await WaitForQueryPathSetAsync(subtreeQuery, set => set.Contains(nodePath), ct);

        var client = GetClient();
        var nodeAddress = new Address(nodePath);
        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).FirstAsync().ToTask();

        var nodeHub = Mesh.GetHostedHub(nodeAddress, HostedHubCreation.Never)!;

        // Production pattern: hub.Observe + Subscribe. No await on the hub-bound path.
        var responseDelivery = await AwaitResponseAsync(
            new DeleteNodeRequest(nodePath) { Recursive = true },
            hub: nodeHub,
            ct: ct);
        var response = responseDelivery.Message;
        response.Success.Should().BeTrue($"delete should succeed (error: {response.Error})");

        var paths = await WaitForQueryPathSetAsync(subtreeQuery, set => !set.Contains(nodePath), ct);
        paths.Should().NotContain(nodePath, "node should be gone after Post+RegisterCallback delete");
    }

    /// <summary>
    /// Recursive delete via Post + RegisterCallback. Verifies DeleteSelfFromStorage
    /// posts the success response BEFORE issuing the storage write, so a hub that
    /// gets torn down by the storage delete still delivers its reply to the caller.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeleteNode_PostRegisterCallback_Recursive_DoesNotDeadlock()
    {
        var ct = TestContext.Current.CancellationToken;
        var parentPath = $"{TestPartition}/del-rec-parent";
        var subtreeQuery = $"path:{TestPartition} scope:subtree";

        await NodeFactory.CreateNode(
            new MeshNode("del-rec-parent", TestPartition) { Name = "Parent", NodeType = "Group" });
        await NodeFactory.CreateNode(
            new MeshNode("c1", parentPath) { Name = "C1", NodeType = "Markdown" });
        await NodeFactory.CreateNode(
            new MeshNode("c2", parentPath) { Name = "C2", NodeType = "Markdown" });
        await WaitForQueryPathSetAsync(subtreeQuery,
            set => set.Contains(parentPath) && set.Contains($"{parentPath}/c1") && set.Contains($"{parentPath}/c2"), ct);

        var client = GetClient();
        await client.Observe(new PingRequest(), o => o.WithTarget(new Address(parentPath))).FirstAsync().ToTask();

        var parentHub = Mesh.GetHostedHub(new Address(parentPath), HostedHubCreation.Never)!;

        var responseDelivery = await AwaitResponseAsync(
            new DeleteNodeRequest(parentPath) { Recursive = true },
            hub: parentHub,
            ct: ct);
        var response = responseDelivery.Message;
        response.Success.Should().BeTrue($"recursive delete should succeed (error: {response.Error})");

        var paths = await WaitForQueryPathSetAsync(subtreeQuery,
            set => !set.Contains(parentPath)
                && !set.Contains($"{parentPath}/c1")
                && !set.Contains($"{parentPath}/c2"), ct);
        paths.Should().NotContain(parentPath);
        paths.Should().NotContain($"{parentPath}/c1");
        paths.Should().NotContain($"{parentPath}/c2");
    }
}
