using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// End-to-end test for the Delete → redirect-to-parent GUI flow (<see cref="DeleteLayoutArea"/>).
///
/// The bug (atioz, 2026-06-22): deleting a node whose immediate parent PATH is a VIRTUAL grouping
/// (its children exist, but the segment itself is not a node — e.g. <c>AgenticPension/Script</c>)
/// redirected straight to that virtual path, landing the user on another
/// "No node found at '…'. Closest ancestor is '…'" page. The fix redirects to the nearest ancestor
/// that is an ACTUAL mesh node. This reproduces the virtual-parent shape against the real mesh +
/// query engine and drives the very delete the UI issues from the node's own hub.
/// </summary>
public class DeleteRedirectToExistingParentTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMeshService MeshSvc => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    /// <summary>
    /// Reactive path-set wait: folds live query deltas into a running path set and
    /// blocks (≤ ReadNodeTimeout) until <paramref name="predicate"/> holds.
    /// </summary>
    private async Task<IReadOnlySet<string>> WaitForQueryPathSet(
        string query, Func<IReadOnlySet<string>, bool> predicate)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        return await MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
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

    [Fact(Timeout = 30000)]
    public async Task Redirect_skips_virtual_parent_and_targets_nearest_existing_ancestor()
    {
        // The real ancestor that must be the redirect target.
        var rootPath = $"{TestPartition}/redir-root";
        // Leaf whose immediate parent PATH segment "Virtual" is NOT a node (only its child exists) —
        // exactly the AgenticPension/Script/ExportPdfBase64 shape.
        var virtualParent = $"{rootPath}/Virtual";
        var leafPath = $"{virtualParent}/leaf";
        var subtreeQuery = $"path:{TestPartition} scope:subtree";

        await NodeFactory.CreateNode(
            new MeshNode("redir-root", TestPartition) { Name = "Redirect Root", NodeType = "Group" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("leaf", virtualParent) { Name = "Leaf Under Virtual Group", NodeType = "Markdown" }).Should().Emit();
        await WaitForQueryPathSet(subtreeQuery, set => set.Contains(rootPath) && set.Contains(leafPath));

        // Sanity: the middle path segment is genuinely a VIRTUAL group, not a node.
        var virtualIsNode = await MeshSvc.Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{virtualParent}"))
            .Take(1).Select(c => c.Items.Any(n => n.Path == virtualParent)).ToTask();
        virtualIsNode.Should().BeFalse("the middle path segment must be a virtual group, not a node");

        // BEFORE delete the redirect target already skips the virtual parent → the real ancestor.
        var targetBefore = await DeleteLayoutArea.ResolveNearestExistingAncestor(MeshSvc, leafPath).Take(1).ToTask();
        targetBefore.Should().Be(rootPath, "redirect must skip the virtual parent and land on a real node");

        // Run the real delete the UI issues, from the node's own hub.
        var client = GetClient();
        await client.Observe(new PingRequest(), o => o.WithTarget(new Address(leafPath))).Should().Emit();
        var nodeHub = Mesh.GetHostedHub(new Address(leafPath), HostedHubCreation.Never)!;
        var resp = (await nodeHub.Observe(new DeleteNodeRequest(leafPath) { Recursive = true }).Should().Emit()).Message;
        resp.Success.Should().BeTrue($"delete should succeed (error: {resp.Error})");
        await WaitForQueryPathSet(subtreeQuery, set => !set.Contains(leafPath));

        // AFTER delete the redirect still resolves to the real ancestor — never the deleted or virtual path.
        var targetAfter = await DeleteLayoutArea.ResolveNearestExistingAncestor(MeshSvc, leafPath).Take(1).ToTask();
        targetAfter.Should().Be(rootPath, "post-delete redirect must target the nearest existing mesh node");
    }

    [Fact(Timeout = 30000)]
    public async Task Redirect_targets_real_immediate_parent_when_it_exists()
    {
        var parentPath = $"{TestPartition}/redir-parent";
        var childPath = $"{parentPath}/child";
        var subtreeQuery = $"path:{TestPartition} scope:subtree";

        await NodeFactory.CreateNode(
            new MeshNode("redir-parent", TestPartition) { Name = "Parent", NodeType = "Group" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("child", parentPath) { Name = "Child", NodeType = "Markdown" }).Should().Emit();
        await WaitForQueryPathSet(subtreeQuery, set => set.Contains(parentPath) && set.Contains(childPath));

        var target = await DeleteLayoutArea.ResolveNearestExistingAncestor(MeshSvc, childPath).Take(1).ToTask();
        target.Should().Be(parentPath, "an existing immediate parent is the redirect target");
    }

    [Fact(Timeout = 30000)]
    public async Task Redirect_goes_home_for_top_level_node()
    {
        // A node with no parent segment has no ancestor → redirect home (null), never an error page.
        var target = await DeleteLayoutArea.ResolveNearestExistingAncestor(MeshSvc, "SomeTopLevelNode").Take(1).ToTask();
        target.Should().BeNull("a top-level node has no ancestor; the flow redirects home");
    }
}
