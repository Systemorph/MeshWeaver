using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Deterministic repros for issue #839 — recursive delete reported success while leaving
/// descendants alive in storage. Three mechanisms are pinned:
/// <list type="number">
/// <item><description><b>Stale / gap-blind plan.</b> The plan now comes from AUTHORITATIVE
/// storage enumeration (<see cref="IStorageAdapter.ListDescendantPaths"/>), and
/// <see cref="HierarchicalPathDeletion"/> traverses node-less intermediate levels
/// (<c>{root}/nt/Release/{v}</c>, <c>{root}/sub/deep/…</c>) instead of silently skipping
/// every branch anchored under them.</description></item>
/// <item><description><b>Mid-flight creation from another process.</b> The commit is followed
/// by a storage-verified drain loop: after each pass the subtree is re-enumerated and
/// survivors are deleted too; success is only reported once the enumeration is empty.</description></item>
/// <item><description><b>Mid-flight creation in-process.</b> While the delete is in flight
/// the outermost storage decorator (<c>SubtreeDeletionGuardStorageAdapter</c>) refuses every
/// write under the subtree — a hard invariant scoped to the operation, not a timer.</description></item>
/// </list>
/// All post-delete assertions go against STORAGE (adapter reads / descendant enumeration),
/// never the eventually-consistent query catalog.
/// </summary>
public class RecursiveDeleteAuthoritativeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IMessageHub Client => _client ??= GetClient();
    private IMessageHub? _client;

    /// <summary>The decorated storage chain (deletion guard outermost) — what the mesh writes through.</summary>
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private async Task<DeleteNodeResponse> Delete(DeleteNodeRequest req)
        => (await Client.Observe(req, o => o.WithTarget(Mesh.Address))
            .Should().Within(60.Seconds()).Emit()).Message;

    // ─── 1. Authoritative plan + gap traversal ────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task RecursiveDelete_DescendantsBehindNodelessSegments_AreDeleted_AndStorageVerifiedEmpty()
    {
        var root = $"{TestPartition}/authdel1";
        await NodeFactory.CreateNode(new MeshNode("authdel1", TestPartition)
        { Name = "Root", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("nt", root)
        { Name = "NT", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        // Satellite-shaped rows behind NODE-LESS intermediate segments, written the way
        // satellite writers produce them (straight through storage): no node exists at
        // {root}/nt/Release or {root}/sub/deep. The old catalog-driven plan + set-connected
        // traversal skipped exactly these — they survived a "successful" recursive delete.
        var options = Client.JsonSerializerOptions;
        await Storage.Write(new MeshNode("1", $"{root}/nt/Release")
        { Name = "Release 1", NodeType = "Markdown" }, options).Should().Within(30.Seconds()).Emit();
        await Storage.Write(new MeshNode("leaf", $"{root}/sub/deep")
        { Name = "Deep leaf", NodeType = "Markdown" }, options).Should().Within(30.Seconds()).Emit();

        var response = await Delete(new DeleteNodeRequest(root) { Recursive = true });

        response.Success.Should().BeTrue($"expected OK, got: {response.Error}");
        response.Log!.AffectedPaths.Should().Contain($"{root}/nt/Release/1");
        response.Log.AffectedPaths.Should().Contain($"{root}/sub/deep/leaf");

        // AUTHORITATIVE verification — zero descendants in storage, not "query says gone".
        var survivors = await Storage.ListDescendantPaths(root).Should().Within(30.Seconds()).Emit();
        survivors.Should().BeEmpty(
            $"recursive delete must drain the subtree; still present: {string.Join(", ", survivors)}");
        (await Storage.Read(root, options).Should().Within(30.Seconds()).Emit()).Should().BeNull();
        (await Storage.Read($"{root}/nt/Release/1", options).Should().Within(30.Seconds()).Emit()).Should().BeNull();
        (await Storage.Read($"{root}/sub/deep/leaf", options).Should().Within(30.Seconds()).Emit()).Should().BeNull();
    }

    // ─── 2. Mid-flight creation bypassing the in-process guard → drain loop ───

    [Fact(Timeout = 30_000)]
    public async Task RecursiveDelete_NodeCreatedMidFlightBypassingGuard_IsDrainedBeforeSuccess()
    {
        var root = $"{TestPartition}/authdel2";
        await NodeFactory.CreateNode(new MeshNode("authdel2", TestPartition)
        { Name = "Root", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("c1", root)
        { Name = "C1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("c2", root)
        { Name = "C2", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        // Simulate a CROSS-PROCESS writer (a second silo sharing the store): the first
        // Deleted commit under the root triggers a write through the UNDECORATED inner
        // adapter — bypassing the in-process SubtreeDeletionGuard exactly like another
        // process would. Only the storage-verified drain loop can catch this one.
        var inner = Mesh.ServiceProvider.GetRequiredKeyedService<IStorageAdapter>("inner");
        var options = Client.JsonSerializerOptions;
        var midflight = $"{root}/midflight";
        var wrote = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hook = Storage.Changes
            .Where(c => c.Kind == DataChangeKind.Deleted
                && c.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .Take(1)
            .SelectMany(_ => inner.Write(new MeshNode("midflight", root)
            { Name = "Mid-flight", NodeType = "Markdown" }, options))
            .Subscribe(
                node => wrote.TrySetResult(node!.Path),
                ex => wrote.TrySetException(ex));

        var response = await Delete(new DeleteNodeRequest(root) { Recursive = true });

        response.Success.Should().BeTrue($"expected OK, got: {response.Error}");
        // The racing write really landed mid-delete (synchronously inside the first
        // Deleted commit — strictly before the first pass completed).
        (await wrote.Task).Should().Be(midflight);
        // The drain pass found it in the post-pass re-enumeration and deleted it too.
        response.Log!.AffectedPaths.Should().Contain(midflight);

        var survivors = await Storage.ListDescendantPaths(root).Should().Within(30.Seconds()).Emit();
        survivors.Should().BeEmpty(
            $"a node created mid-delete must not survive; still present: {string.Join(", ", survivors)}");
        (await Storage.Read(midflight, options).Should().Within(30.Seconds()).Emit()).Should().BeNull();
    }

    // ─── 3. Mid-flight creation in-process → refused by the deletion guard ────

    [Fact(Timeout = 30_000)]
    public async Task Write_UnderSubtreeBeingDeleted_IsRefused_AndNodeDoesNotSurvive()
    {
        var root = $"{TestPartition}/authdel3";
        await NodeFactory.CreateNode(new MeshNode("authdel3", TestPartition)
        { Name = "Root", NodeType = "Group" }).Should().Within(30.Seconds()).Emit();
        await NodeFactory.CreateNode(new MeshNode("c1", root)
        { Name = "C1", NodeType = "Markdown" }).Should().Within(30.Seconds()).Emit();

        // In-process mid-flight creation goes through the DECORATED chain — the
        // SubtreeDeletionGuard must refuse it while the delete scope is open.
        var options = Client.JsonSerializerOptions;
        var refusal = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hook = Storage.Changes
            .Where(c => c.Kind == DataChangeKind.Deleted
                && c.Path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .Take(1)
            .SelectMany(_ => Storage.Write(new MeshNode("blocked", root)
            { Name = "Blocked", NodeType = "Markdown" }, options))
            .Subscribe(
                node => refusal.TrySetException(new InvalidOperationException(
                    $"write unexpectedly succeeded for '{node!.Path}' while its subtree was being deleted")),
                ex => refusal.TrySetResult(ex));

        var response = await Delete(new DeleteNodeRequest(root) { Recursive = true });
        response.Success.Should().BeTrue($"expected OK, got: {response.Error}");

        var ex = await refusal.Task;
        ex.Message.Should().Contain("currently being deleted");

        (await Storage.Read($"{root}/blocked", options).Should().Within(30.Seconds()).Emit()).Should().BeNull();
        var survivors = await Storage.ListDescendantPaths(root).Should().Within(30.Seconds()).Emit();
        survivors.Should().BeEmpty(
            $"the refused write must leave nothing behind; still present: {string.Join(", ", survivors)}");
    }
}
