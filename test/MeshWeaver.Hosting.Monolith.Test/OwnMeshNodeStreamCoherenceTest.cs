using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Own-node read COHERENCE: every reader of a hub's own <see cref="MeshNode"/> must be served
/// by the SAME reduced stream, so all of them observe each commit once, in subscription order.
///
/// <para><b>The defect this pins.</b> <c>workspace.GetMeshNodeStream()</c> /
/// <c>workspace.GetStream(new MeshNodeReference())</c> used to MINT A NEW reduced stream on
/// every call (<c>primary.Reduce&lt;InstanceCollection&gt;(…).Reduce&lt;MeshNode&gt;(…)</c>,
/// uncached — unlike the cross-hub branch of the same reducer, which shares one stream per path
/// via <c>Workspace._remoteStreamCache</c>). Each of those streams pumps its values through its
/// OWN hosted <c>sync/&lt;id&gt;</c> hub — <c>SynchronizationStream.OnNext</c> is
/// <c>Hub.Post(SetCurrentRequest)</c>, i.e. an action-block hop — so two readers of the same
/// hub's own node saw the same commit on two independent action blocks, in ARBITRARY relative
/// order. Measured by this test before the fix: <b>10 of 40 commits (25%) reached the
/// later-subscribed reader first.</b></para>
///
/// <para><b>Why it mattered beyond the wasted streams.</b> Any component that reacts on the
/// own-node stream and then acts could still be un-run while an unrelated own-node reader had
/// already observed the same commit. Concretely: the AI submission watcher
/// (<c>ThreadSubmissionServer.InstallServerWatcher</c>) buffers mid-round follow-up messages
/// into the per-thread <c>ThreadInboxChannel</c> from its own-node subscription, and
/// <c>check_inbox</c> serves the agent purely from that channel. A follow-up that was provably
/// committed on the thread node could therefore still yield "(no new messages)" — the ~25%
/// flake in
/// <c>InboxToolIntegrationTest.CheckInbox_DrainMidExecution_DeliversInline_NoCellSplit</c>,
/// whose failure rate matches the inversion rate measured here.</para>
///
/// <para>Fix: <c>MeshDataSourceExtensions.AddMeshDataSource</c> builds the own-node reduced
/// chain once per (workspace, path) and hands the same stream to every caller.</para>
/// </summary>
public class OwnMeshNodeStreamCoherenceTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>Share Mesh/SP across [Fact]s — see MonolithMeshTestBase.ShareMeshAcrossTests.</summary>
    protected override bool ShareMeshAcrossTests => true;

    /// <summary>
    /// Two calls to <c>GetStream(new MeshNodeReference())</c> on the same workspace must return
    /// the SAME stream instance. This is the structural half of the contract — it is what makes
    /// the ordering assertion below a guarantee rather than a coin flip.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task OwnNodeStream_IsSharedAcrossCallers()
    {
        var workspace = await ActivateNodeWorkspaceAsync("shared-stream-node");

        var first = workspace.GetStream(new MeshNodeReference());
        var second = workspace.GetStream(new MeshNodeReference());

        (first is not null).Should().BeTrue("the workspace must expose an own-node MeshNode stream");
        ReferenceEquals(first, second).Should().BeTrue(
            "every own-node reader must share ONE reduced stream — a fresh stream per call gives "
            + "the callers independently-pumped views of the same node (see the ordering test), "
            + "and leaks a fresh pair of hosted sync/ hubs that nothing ever disposes.");
    }

    /// <summary>
    /// The behavioural half: with one shared stream, fan-out is a single synchronous
    /// ReplaySubject dispatch, so a reader that subscribed FIRST is served FIRST for every
    /// commit. Before the fix this inverted on ~25% of commits.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task TwoOwnStreamReaders_ObserveEveryCommitInSubscriptionOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = await ActivateNodeWorkspaceAsync("coherence-node");

        var seq = 0L;
        var aSeen = new ConcurrentDictionary<string, long>();
        var bSeen = new ConcurrentDictionary<string, long>();

        // Reader A subscribes FIRST — the role the AI submission watcher plays (installed at
        // thread-hub init, before any test/GUI reader opens its own view of the same node).
        using var subA = workspace.GetMeshNodeStream()
            .Subscribe(n => { if (n?.Name is { } name) aSeen[name] = Interlocked.Increment(ref seq); });

        // Reader B subscribes SECOND — the role the test's own-stream wait plays.
        using var subB = workspace.GetMeshNodeStream()
            .Subscribe(n => { if (n?.Name is { } name) bSeen[name] = Interlocked.Increment(ref seq); });

        // Commit a run of distinct values. Each write waits on the CONDITION that both readers
        // observed the previous marker (never a sleep — see AGENTS.md "Never Task.Delay to wait
        // for propagation"), so every marker is its own commit and the two readers' observation
        // orders are directly comparable.
        const int n = 40;
        var markers = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var marker = $"m-{i:D3}";
            markers.Add(marker);
            using var write = workspace.GetMeshNodeStream()
                .Update(node => node with { Name = marker })
                .Subscribe(_ => { }, ex => Output.WriteLine($"update error: {ex.Message}"));

            await Observable.Interval(TimeSpan.FromMilliseconds(20)).StartWith(0L)
                .Where(_ => aSeen.ContainsKey(marker) && bSeen.ContainsKey(marker))
                .Take(1).Timeout(TimeSpan.FromSeconds(30)).ToTask(ct);
        }

        var inverted = markers.Where(m => bSeen[m] < aSeen[m]).ToList();
        Output.WriteLine($"commits: {n}, observed out of subscription order: {inverted.Count}");
        if (inverted.Count > 0)
            Output.WriteLine($"  inverted: {string.Join(",", inverted.Take(20))}");

        inverted.Should().BeEmpty(
            "two readers of the SAME hub's own MeshNode must observe every commit in subscription "
            + "order. When each caller got its own independently-pumped reduced stream this "
            + "inverted on ~25% of commits, so a component that reacts on the own-node stream "
            + "(ThreadInboxChannel.OfferFromNode, fed by the AI submission watcher) could still be "
            + "un-run after another own-node reader had already observed the same commit.");
    }

    private async Task<IWorkspace> ActivateNodeWorkspaceAsync(string id)
    {
        var path = $"{TestPartition}/{id}";
        await NodeFactory.CreateNode(
            new MeshNode(id, TestPartition) { Name = "initial", NodeType = "Markdown" }).Should().Emit();
        // Activate the per-node hub — Orleans grain activation is lazy; monolith hubs activate
        // eagerly via routing, so this keeps the test portable (same as
        // WorkspaceUpdateMeshNodePropagationTest).
        await Mesh.Observe(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))
            .Should().Emit();

        var nodeHub = Mesh.GetHostedHub(new Address(path), HostedHubCreation.Never);
        nodeHub.Should().NotBeNull("hub should be activated by the GetDataRequest above");
        return nodeHub!.GetWorkspace();
    }
}
