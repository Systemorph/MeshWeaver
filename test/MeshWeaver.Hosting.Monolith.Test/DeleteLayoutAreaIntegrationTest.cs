using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
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
        var nodePath = $"{TestPartition}/del-fandf";
        await NodeFactory.CreateNodeAsync(
            new MeshNode("del-fandf", TestPartition) { Name = "Fire And Forget", NodeType = "Markdown" });

        await NodeFactory.DeleteNodeAsync(nodePath);

        var result = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath}")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        result.Should().BeNull("node should be deleted");
    }

    /// <summary>
    /// Verifies that delete from a node's own hub works without deadlock.
    /// This reproduces the exact production flow: DeleteLayoutArea runs on the node hub,
    /// resolves IMeshService from node hub, calls DeleteNode.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeleteNode_FromNodeHub_Succeeds()
    {
        var nodePath = $"{TestPartition}/del-nodehub";
        await NodeFactory.CreateNodeAsync(
            new MeshNode("del-nodehub", TestPartition) { Name = "Node Hub Delete", NodeType = "Markdown" });

        // Route a message to create the node hub on demand
        var client = GetClient();
        var nodeAddress = new Address(nodePath);
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        var nodeHub = Mesh.GetHostedHub(nodeAddress, HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("node hub should exist after ping");

        // Resolve IMeshService from the NODE's hub (same as DeleteLayoutArea does)
        var nodeService = nodeHub!.ServiceProvider.GetRequiredService<IMeshService>();

        // Delete from node hub — this is the production pattern
        await nodeService.DeleteNodeAsync(nodePath);

        var result = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath}")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        result.Should().BeNull("deletion from node hub should work");
    }

    /// <summary>
    /// Verifies that recursive delete (parent with children) works.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeleteNode_WithChildren_DeletesAll()
    {
        await NodeFactory.CreateNodeAsync(
            new MeshNode("del-parent", TestPartition) { Name = "Parent", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("child1", $"{TestPartition}/del-parent") { Name = "Child 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("child2", $"{TestPartition}/del-parent") { Name = "Child 2", NodeType = "Markdown" });

        await NodeFactory.DeleteNodeAsync($"{TestPartition}/del-parent");

        var parent = await MeshQuery.QueryAsync<MeshNode>($"path:{TestPartition}/del-parent")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        parent.Should().BeNull("parent should be deleted");

        var children = await MeshQuery.QueryAsync<MeshNode>($"namespace:{TestPartition}/del-parent")
            .ToListAsync(TestContext.Current.CancellationToken);
        children.Should().BeEmpty("all children should be deleted");
    }

    /// <summary>
    /// Replicates the exact production pattern the Delete click handler must use:
    /// <c>hub.Post(new DeleteNodeRequest(...)) + hub.RegisterCallback(...)</c>.
    /// No <c>await</c> on the delete path — the callback drives a <see cref="TaskCompletionSource{T}"/>
    /// which the test awaits at the xunit boundary only. A blocked hub cannot produce a callback,
    /// so the 10 s WaitAsync guard fails the test instead of hanging forever.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeleteNode_PostRegisterCallback_DoesNotDeadlock()
    {
        var nodePath = $"{TestPartition}/del-reactive";
        await NodeFactory.CreateNodeAsync(
            new MeshNode("del-reactive", TestPartition) { Name = "Reactive Delete", NodeType = "Markdown" });

        var client = GetClient();
        var nodeAddress = new Address(nodePath);
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        var nodeHub = Mesh.GetHostedHub(nodeAddress, HostedHubCreation.Never)!;

        // Production pattern: Post + RegisterCallback. No await on the hub-bound path.
        var tcs = new TaskCompletionSource<DeleteNodeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = nodeHub.Post(new DeleteNodeRequest(nodePath) { Recursive = true })!;
        _ = nodeHub.RegisterCallback(delivery, (d, _) =>
        {
            tcs.TrySetResult(((IMessageDelivery<DeleteNodeResponse>)d).Message);
            return Task.FromResult<IMessageDelivery>(d);
        });

        var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        response.Success.Should().BeTrue($"delete should succeed (error: {response.Error})");

        var lookup = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath}")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        lookup.Should().BeNull("node should be gone after Post+RegisterCallback delete");
    }

    /// <summary>
    /// Recursive delete via Post + RegisterCallback. Verifies DeleteSelfFromStorage
    /// posts the success response BEFORE issuing the storage write, so a hub that
    /// gets torn down by the storage delete still delivers its reply to the caller.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeleteNode_PostRegisterCallback_Recursive_DoesNotDeadlock()
    {
        await NodeFactory.CreateNodeAsync(
            new MeshNode("del-rec-parent", TestPartition) { Name = "Parent", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("c1", $"{TestPartition}/del-rec-parent") { Name = "C1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("c2", $"{TestPartition}/del-rec-parent") { Name = "C2", NodeType = "Markdown" });

        var parentPath = $"{TestPartition}/del-rec-parent";
        var client = GetClient();
        await client.AwaitResponse(new PingRequest(), o => o.WithTarget(new Address(parentPath)),
            TestContext.Current.CancellationToken);

        var parentHub = Mesh.GetHostedHub(new Address(parentPath), HostedHubCreation.Never)!;

        var tcs = new TaskCompletionSource<DeleteNodeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = parentHub.Post(new DeleteNodeRequest(parentPath) { Recursive = true })!;
        _ = parentHub.RegisterCallback(delivery, (d, _) =>
        {
            tcs.TrySetResult(((IMessageDelivery<DeleteNodeResponse>)d).Message);
            return Task.FromResult<IMessageDelivery>(d);
        });

        var response = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        response.Success.Should().BeTrue($"recursive delete should succeed (error: {response.Error})");

        var parent = await MeshQuery.QueryAsync<MeshNode>($"path:{parentPath}")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        parent.Should().BeNull();
        var children = await MeshQuery.QueryAsync<MeshNode>($"namespace:{parentPath}")
            .ToListAsync(TestContext.Current.CancellationToken);
        children.Should().BeEmpty();
    }
}
