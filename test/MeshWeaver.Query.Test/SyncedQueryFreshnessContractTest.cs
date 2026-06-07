using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Query.Test;

/// <summary>
/// Dedicated suite for the <b>freshness contract</b> of the synced mesh node query
/// (<c>workspace.GetQuery</c>) that the NodeType <c>IsDirty</c> / recompile-on-edit
/// pipeline depends on.
///
/// <para><see cref="SyncedQueryTest"/> already covers membership (add / remove) and
/// content (<c>Name</c>) propagation. This suite pins the two guarantees the compile
/// watcher relies on and that a content-only assertion would miss:</para>
/// <list type="number">
///   <item>After an edit, the live synced query re-emits the node with a
///         <b>strictly-greater <see cref="MeshNode.LastModified"/></b> — not just
///         fresh <c>Content</c>. <c>NodeTypeDefinition.CurrentSourceVersions</c> is
///         keyed on <c>LastModified.UtcTicks</c>, so a stale <c>LastModified</c>
///         (even with fresh content) leaves <c>IsDirty=false</c> and the V2 compile
///         never fires — the <c>CodeEditRecompileTest</c> recompile-on-edit failure
///         in isolation.</item>
///   <item>A <b>live</b> subscription (no <c>.Take(1)</c>, the shape the
///         <c>InstallSourcesWatcher</c> uses) observes the edit on its own — the
///         consumer should not need a fresh re-query to learn a source changed.</item>
/// </list>
/// </summary>
public class SyncedQueryFreshnessContractTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    // Isolated mesh per test method — each test owns its nodes + query id.
    protected override bool ShareMeshAcrossTests => false;

    private const string Ns = $"{TestPartition}/FreshnessContract";

    private static MeshNode Subject(string id, string name)
        => new(id, Ns) { Name = name, NodeType = "Markdown", State = MeshNodeState.Active };

    private IObservable<IEnumerable<MeshNode>> Query(object id)
        => Mesh.GetWorkspace().GetQuery(id, $"namespace:{Ns} scope:subtree nodeType:Markdown");

    /// <summary>
    /// After an edit the live synced query re-emits the node with BOTH fresh
    /// <c>Name</c> AND a strictly-greater <see cref="MeshNode.LastModified"/>. The
    /// LastModified half is the <c>IsDirty</c> contract — see the class summary.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void Edit_LiveQuery_ReEmitsWithFreshContentAndLastModified()
    {
        var q = Query("$fresh-contract").Replay(1).RefCount();
        using var keepAlive = q.Subscribe();

        var node = Subject("doc", "V1");
        NodeFactory.CreateNode(node).Should().Emit();
        var v1 = q.Where(a => a.Any(n => n.Path == node.Path && n.Name == "V1"))
            .Should().Within(15.Seconds()).Emit()
            .Single(n => n.Path == node.Path);

        NodeFactory.UpdateNode(v1 with { Name = "V2" }).Should().Emit();

        var v2 = q.Where(a => a.Any(n => n.Path == node.Path && n.Name == "V2"))
            .Should().Within(15.Seconds()).Emit()
            .Single(n => n.Path == node.Path);

        v2.Name.Should().Be("V2",
            "the live synced query must surface the edited content");
        v2.LastModified.Should().BeAfter(v1.LastModified,
            "the synced query must surface a FRESH LastModified after an edit — IsDirty / "
            + "recompile-on-edit keys CurrentSourceVersions on LastModified.UtcTicks, so a "
            + "stale LastModified leaves the NodeType clean and the V2 compile never fires");
    }

    /// <summary>
    /// The watcher shape: a SINGLE live subscription (no fresh re-query) observes a
    /// create THEN an edit — it must surface the edited content on its own. This is
    /// exactly how <c>InstallSourcesWatcher</c> learns a source changed; it must not
    /// need a fresh subscription to see the edit. Asserted on the live observable
    /// itself (not an async-collected list) so it's deterministic.
    /// </summary>
    [Fact(Timeout = 30000)]
    public void LiveSubscription_ObservesEdit_WithoutReQuery()
    {
        var node = Subject("watched", "V1");

        // ONE live subscription, kept alive — the watcher's exact shape.
        var live = Query("$fresh-watch")
            .Select(a => a.FirstOrDefault(n => n.Path == node.Path)?.Name)
            .Where(name => name is not null)
            .Replay(1).RefCount();
        using var keepAlive = live.Subscribe();

        NodeFactory.CreateNode(node).Should().Emit();
        live.Where(name => name == "V1").Should().Within(15.Seconds()).Emit();

        // Read the live node to carry the current version, then edit it.
        var v1 = Query("$fresh-watch-read")
            .Where(a => a.Any(n => n.Path == node.Path && n.Name == "V1"))
            .Should().Within(15.Seconds()).Emit()
            .Single(n => n.Path == node.Path);
        NodeFactory.UpdateNode(v1 with { Name = "V2" }).Should().Emit();

        // The SAME live subscription must surface V2 — no fresh query needed.
        live.Where(name => name == "V2").Should().Within(15.Seconds()).Emit(
            "a single live synced-query subscription must observe the edit on its own — "
            + "the watcher gets dirty by observing, not by re-querying");
    }
}
