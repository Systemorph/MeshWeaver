using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #6751 — <see cref="DeleteNodeRequest.KeepDescendants"/> removes a row alone, which is sound
/// only for a NodeType definition (whose path prefixes records that are not its lifecycle
/// children). On any other node it would skip the has-children guard and orphan the children, and
/// on a partition root the partition-drop handler would drop the store the request asked to keep.
/// </summary>
public class RowOnlyDeleteIsForNodeTypeRowsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private Task Write(MeshNode node, System.Threading.CancellationToken ct) =>
        Storage.Write(node, Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.WriteConvergence)
            .Emit($"'{node.Path}' must be stored before the delete", ct);

    private Task<bool> Exists(string path, System.Threading.CancellationToken ct) =>
        Storage.Exists(path).Should().Within(TestTimeouts.Convergence)
            .Emit($"storage must answer whether '{path}' exists", ct);

    private async Task<DeleteNodeResponse> Delete(DeleteNodeRequest request, System.Threading.CancellationToken ct) =>
        (await ObserveNodeOperation(request).Should().Within(TestTimeouts.Convergence)
            .Emit("a delete always answers", ct)).Message;

    /// <summary>THE FALSIFIER: a row-only delete of an ordinary parent. Without the refusal the
    /// parent is removed and its child is left orphaned under a path with no node.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ARowOnlyDelete_OfAnOrdinaryParent_IsRefused_AndRemovesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var parent = new MeshNode("row-only-parent", TestPartition) { Name = "parent", NodeType = "Markdown" };
        await Write(parent, ct);
        await Write(new MeshNode("child", parent.Path) { Name = "child", NodeType = "Markdown" }, ct);

        var response = await Delete(new DeleteNodeRequest(parent.Path) { KeepDescendants = true }, ct);
        Output.WriteLine($"[delete] success={response.Success} reason={response.RejectionReason} error={response.Error}");

        response.Success.Should().BeFalse("only a NodeType definition may be removed without its subtree");
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.ValidationFailed);
        (await Exists(parent.Path, ct)).Should().BeTrue("nothing was deleted");
        (await Exists($"{parent.Path}/child", ct)).Should().BeTrue("the child is not orphaned");
    }

    /// <summary>The positive control: the refusal is not blanket — a NodeType row IS removed alone,
    /// and the record under its path stays.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ARowOnlyDelete_OfANodeTypeRow_RemovesTheRowAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new MeshNode("RowOnlyType", TestPartition)
        {
            Name = "RowOnlyType",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
        };
        await Write(type, ct);
        await Write(new MeshNode("Note", type.Path) { Name = "Note", NodeType = "Markdown" }, ct);

        var response = await Delete(new DeleteNodeRequest(type.Path) { KeepDescendants = true }, ct);
        Output.WriteLine($"[delete] success={response.Success} reason={response.RejectionReason} error={response.Error}");

        response.Success.Should().BeTrue(response.Error ?? "a NodeType row may be removed alone");
        (await Exists(type.Path, ct)).Should().BeFalse("the row is gone");
        (await Exists($"{type.Path}/Note", ct)).Should().BeTrue("what lives under it is kept");
    }

    /// <summary>THE FALSIFIER for a top-level type: a namespace-less, one-segment NodeType row is a
    /// partition ROOT (<see cref="PartitionDefinition.IsPartitionRoot"/>), so a row-only delete of
    /// it would run the partition-drop handler over the store the request asked to keep. Refused
    /// before any side effect; the record under it stays.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ARowOnlyDelete_OfATopLevelNodeTypeRow_IsRefused_AsAPartitionRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new MeshNode($"RowOnlyTopType{System.Guid.NewGuid():N}"[..24])
        {
            Name = "RowOnlyTopType",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
        };
        PartitionDefinition.IsPartitionRoot(type).Should().BeTrue("the fixture must be the shape under test");
        MeshExtensions.RowOnlyDeleteRefusal(type.Path, type, new DeleteNodeRequest(type.Path) { KeepDescendants = true })!
            .MessageKey.Should().Be("activity.delete.keepDescendantsPartitionRoot");
        await Write(type, ct);
        await Write(new MeshNode("Note", type.Path) { Name = "Note", NodeType = "Markdown" }, ct);

        var response = await Delete(new DeleteNodeRequest(type.Path) { KeepDescendants = true }, ct);
        Output.WriteLine($"[delete] success={response.Success} reason={response.RejectionReason} error={response.Error}");

        response.Success.Should().BeFalse("a partition root is never removed alone");
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.ValidationFailed);
        (await Exists(type.Path, ct)).Should().BeTrue("nothing was deleted");
        (await Exists($"{type.Path}/Note", ct)).Should().BeTrue("the partition's content is kept");
    }

    /// <summary>Recursive and KeepDescendants contradict each other; the refusal carries its own
    /// localization key, never an English sentence inside another key's argument.</summary>
    [Fact(Timeout = 120_000)]
    public async Task ARecursiveRowOnlyDelete_IsRefused_WithItsOwnKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = new MeshNode("ContradictoryType", TestPartition)
        {
            Name = "ContradictoryType",
            NodeType = MeshNode.NodeTypePath,
            State = MeshNodeState.Active,
        };
        await Write(type, ct);

        var response = await Delete(
            new DeleteNodeRequest(type.Path) { KeepDescendants = true, Recursive = true }, ct);

        response.Success.Should().BeFalse("a delete cannot both remove the subtree and keep it");
        response.RejectionReason.Should().Be(NodeDeletionRejectionReason.ValidationFailed);
        (await Exists(type.Path, ct)).Should().BeTrue("nothing was deleted");
        MeshExtensions.RowOnlyDeleteRefusal(type.Path, type,
                new DeleteNodeRequest(type.Path) { KeepDescendants = true, Recursive = true })!
            .MessageKey.Should().Be("activity.delete.keepDescendantsContradiction");
    }
}
