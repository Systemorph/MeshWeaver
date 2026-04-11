using System;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration tests for DeleteLayoutArea — the node deletion workflow.
/// Verifies:
/// 1. The Delete layout area renders the confirmation form
/// 2. Deletion via IMeshService.DeleteNode (fire-and-forget) actually removes the node
/// 3. The redirect-to-parent pattern works without deadlock
/// </summary>
public class DeleteLayoutAreaIntegrationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureDefaultNodeHub(config => config.AddDefaultLayoutAreas());

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>
    /// Verifies the Delete layout area renders on an existing node.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeleteArea_RendersConfirmationForm()
    {
        var nodePath = $"{TestPartition}/del-render";
        await NodeFactory.CreateNodeAsync(
            new MeshNode("del-render", TestPartition) { Name = "Delete Render Test", NodeType = "Markdown" });

        var client = GetClient();
        var nodeAddress = new Address(nodePath);

        // Initialize the hub
        await client.AwaitResponse(
            new PingRequest(),
            o => o.WithTarget(nodeAddress),
            TestContext.Current.CancellationToken);

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.DeleteArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress, reference);

        var value = await stream.Timeout(TimeSpan.FromSeconds(10)).FirstAsync();

        value.Should().NotBe(default(JsonElement),
            "Delete area should render the confirmation form");
    }

    /// <summary>
    /// Verifies that IMeshService.DeleteNode (fire-and-forget) actually deletes the node.
    /// This is the exact pattern used by DeleteLayoutArea after the fix.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeleteNode_FireAndForget_DeletesNode()
    {
        var nodePath = $"{TestPartition}/del-fandf";
        await NodeFactory.CreateNodeAsync(
            new MeshNode("del-fandf", TestPartition) { Name = "Fire And Forget", NodeType = "Markdown" });

        // Fire-and-forget delete — same pattern as the fixed DeleteLayoutArea
        NodeFactory.DeleteNode(nodePath).Subscribe();

        // Give the delete time to propagate
        await Task.Delay(2.Seconds());

        var result = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath}")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        result.Should().BeNull("node should be deleted by fire-and-forget pattern");
    }

    /// <summary>
    /// Verifies that delete from a node's own hub works without deadlock.
    /// This reproduces the exact production flow: DeleteLayoutArea runs on the node hub,
    /// resolves IMeshService from node hub, calls DeleteNode.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeleteNode_FromNodeHub_FireAndForget_Succeeds()
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

        // Fire-and-forget delete
        nodeService.DeleteNode(nodePath).Subscribe();

        await Task.Delay(2.Seconds());

        var result = await MeshQuery.QueryAsync<MeshNode>($"path:{nodePath}")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        result.Should().BeNull("fire-and-forget delete from node hub should work");
    }

    /// <summary>
    /// Verifies that recursive delete (parent with children) works via fire-and-forget.
    /// </summary>
    [Fact(Timeout = 20000)]
    public async Task DeleteNode_WithChildren_FireAndForget_DeletesAll()
    {
        await NodeFactory.CreateNodeAsync(
            new MeshNode("del-parent", TestPartition) { Name = "Parent", NodeType = "Group" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("child1", $"{TestPartition}/del-parent") { Name = "Child 1", NodeType = "Markdown" });
        await NodeFactory.CreateNodeAsync(
            new MeshNode("child2", $"{TestPartition}/del-parent") { Name = "Child 2", NodeType = "Markdown" });

        // Fire-and-forget recursive delete
        NodeFactory.DeleteNode($"{TestPartition}/del-parent").Subscribe();

        await Task.Delay(3.Seconds());

        var parent = await MeshQuery.QueryAsync<MeshNode>($"path:{TestPartition}/del-parent")
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
        parent.Should().BeNull("parent should be deleted");

        var children = await MeshQuery.QueryAsync<MeshNode>($"namespace:{TestPartition}/del-parent")
            .ToListAsync(TestContext.Current.CancellationToken);
        children.Should().BeEmpty("all children should be deleted");
    }
}
