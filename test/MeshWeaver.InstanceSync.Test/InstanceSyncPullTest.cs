using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.InstanceSync.Test;

/// <summary>
/// The pull direction: the reconciliation sweep applies remote changes locally, never echoes a
/// pulled change back to the remote (the loop-prevention core), and respects the direction
/// gating (PushOnly never pulls, PullOnly never pushes).
/// </summary>
public class InstanceSyncPullTest(ITestOutputHelper output) : InstanceSyncTestBase(output)
{
    [Fact]
    public async Task Remote_change_is_pulled_into_the_local_space()
    {
        await CreateSpace("pull1");
        await AddConfiguredSource("pull1");
        await WaitForConfig("pull1", "partner", c => c.InitialSyncAt is not null);

        // A remote-side user adds a node (newer than anything local at that path).
        Remote.Seed(MeshNode.FromPath("pull1/remote-doc") with
        {
            NodeType = "Markdown",
            Name = "Remote Doc",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = "written remotely" },
        });

        var local = await WaitForNode("pull1/remote-doc");
        MarkdownBody(local).Should().Be("written remotely");
    }

    [Fact]
    public async Task Pulled_change_is_never_echoed_back_to_the_remote()
    {
        await CreateSpace("pull2");
        await AddConfiguredSource("pull2");
        await WaitForConfig("pull2", "partner", c => c.InitialSyncAt is not null);

        Remote.Seed(MeshNode.FromPath("pull2/echo-check") with
        {
            NodeType = "Markdown",
            Name = "Echo Check",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = "remote origin" },
        });
        await WaitForNode("pull2/echo-check");

        // Negative assertion: give the (tight, 50ms-debounce) drain ample time to misbehave,
        // then check the pulled path never came back as a remote write. Sanctioned fixed wait —
        // there is no positive signal for "nothing happened".
        await Task.Delay(1500, TestContext.Current.CancellationToken);
        Remote.WriteCount("pull2/echo-check").Should().Be(0,
            "a pulled change is suppressed from the manifest and must not ping-pong");
        var cfg = await Sync.ReadConfig("pull2", "partner").Timeout(10.Seconds()).ToTask();
        cfg!.PendingChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task Remote_update_wins_only_when_newer()
    {
        await CreateSpace("pull3");
        await CreateMarkdown("pull3/doc", "Doc", "local v1");
        await AddConfiguredSource("pull3");
        await WaitForConfig("pull3", "partner", c => c.InitialSyncAt is not null);

        // Remote edit with a NEWER stamp than the replicated copy → pulled over the local one.
        Remote.Seed(Remote.Node("pull3/doc")! with
        {
            Content = new MarkdownContent { Content = "remote v2" },
        }, DateTimeOffset.UtcNow.AddMinutes(1));

        await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => ReadNode("pull3/doc"))
            .Where(n => MarkdownBody(n) == "remote v2")
            .FirstAsync().Timeout(30.Seconds()).ToTask();

        // An OLDER remote stamp must never overwrite the (newer) local content.
        Remote.Seed(Remote.Node("pull3/doc")! with
        {
            Content = new MarkdownContent { Content = "remote stale" },
        }, DateTimeOffset.UtcNow.AddMinutes(-30));

        await Task.Delay(1000, TestContext.Current.CancellationToken);
        MarkdownBody(await ReadNode("pull3/doc").Timeout(10.Seconds()).ToTask())
            .Should().Be("remote v2", "an older remote stamp never overwrites newer local content");
    }

    [Fact]
    public async Task PushOnly_direction_never_pulls()
    {
        await CreateSpace("pull4");
        await AddConfiguredSource("pull4", direction: InstanceSyncDirection.PushOnly);
        await WaitForConfig("pull4", "partner", c => c.InitialSyncAt is not null);

        Remote.Seed(MeshNode.FromPath("pull4/remote-only") with
        {
            NodeType = "Markdown",
            Name = "Remote Only",
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = "must stay remote" },
        });

        await Task.Delay(1000, TestContext.Current.CancellationToken);
        (await ReadNode("pull4/remote-only").Timeout(10.Seconds()).ToTask())
            .Should().BeNull("PushOnly sources must not pull remote changes");
    }

    /// <summary>
    /// 🚨 THE RESURRECTION GUARD (#1471). A local node whose DELETE is in flight must not be
    /// re-applied by the pull sweep — not even when the remote copy is strictly newer.
    ///
    /// <para><c>PullOne</c> used to read the local side with
    /// <c>GetMeshNode(...).Catch(_ =&gt; null)</c> and treat any <c>null</c> as "not there ⇒ create
    /// it". That single <c>null</c> meant three things: genuine absence, the owner's
    /// delete-in-progress TOMBSTONE (null by design), and "the read failed". So a delete in flight
    /// was answered by re-creating the node — and, because the apply registers the path in the
    /// consume-once echo-suppression slot, it could swallow the very <c>Deleted</c> event the push
    /// side needs, leaving the remote holding a node the user deleted here
    /// (<c>Local_delete_propagates_to_remote</c>'s CI timeout).</para>
    ///
    /// <para>The window is the production one, created the production way: the delete handler marks
    /// <c>RecentlyDeletedRegistry</c> SYNCHRONOUSLY before the row goes, so "marked, node still
    /// there" IS the in-flight state. The sweep is then observed by its own remote calls — a
    /// <c>get</c> of the moved hit proves the sweep picked it up, and the NEXT <c>search</c> proves
    /// that sweep ran to completion (sweeps never overlap: Defer + Repeat).</para>
    /// </summary>
    [Fact]
    public async Task Pull_does_not_resurrect_a_node_whose_delete_is_in_flight()
    {
        await CreateSpace("pull6");
        await CreateMarkdown("pull6/doc", "Doc", "local v1");
        await AddConfiguredSource("pull6");
        await WaitForConfig("pull6", "partner", c => c.InitialSyncAt is not null);
        await WaitForRemote(r => r.Node("pull6/doc") is not null);

        // 🔻 Enter the delete-in-flight window BEFORE the remote moves, so the sweep that picks the
        // hit up can only ever see the tombstone.
        var tombstones = Mesh.ServiceProvider.GetRequiredService<RecentlyDeletedRegistry>();
        tombstones.MarkDeleted("pull6/doc");

        // A strictly NEWER remote edit: without the fix this is applied unconditionally, because a
        // null local read short-circuits both the content and the newest-writer comparison.
        Remote.Seed(Remote.Node("pull6/doc")! with
        {
            Content = new MarkdownContent { Content = "remote v2" },
        }, DateTimeOffset.UtcNow.AddMinutes(1));

        // Positive signal #1: the sweep fetched the moved hit.
        var getsBefore = Remote.Calls.Count(c => c is { Op: "get", Path: "pull6/doc" });
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Where(_ => Remote.Calls.Count(c => c is { Op: "get", Path: "pull6/doc" }) > getsBefore)
            .FirstAsync().Timeout(30.Seconds()).ToTask();
        // Positive signal #2: that sweep then RAN OUT — the next sweep's listing can only start
        // after the previous one completed, so PullOne has finished deciding about this hit.
        var searchesAfterGet = Remote.Calls.Count(c => c.Op == "search");
        await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .Where(_ => Remote.Calls.Count(c => c.Op == "search") > searchesAfterGet)
            .FirstAsync().Timeout(30.Seconds()).ToTask();

        // Read through the node's own stream, NOT GetMeshNode: the tombstone is still up (the
        // delete is still "in flight" for the whole test), and that is exactly what the read path
        // is supposed to report. The stream is the authoritative view of what the workspace holds.
        var local = await Mesh.GetHostedHub(new Address("test-reader-pull6"), c => c.AddData())!
            .GetMeshNodeStream("pull6/doc")
            .Where(n => n is not null)
            .FirstAsync().Timeout(30.Seconds()).ToTask();

        MarkdownBody(local).Should().Be("local v1",
            "the local delete is in flight — re-applying the remote copy resurrects content the "
            + "user is deleting. 'remote v2' here means PullOne read the delete tombstone as "
            + "'absent, therefore create'");

        var cfg = await Sync.ReadConfig("pull6", "partner").Timeout(10.Seconds()).ToTask();
        cfg!.LastError.Should().BeNull(
            "skipping a hit whose local delete is in flight is a normal, expected decision — not a "
            + "sync error, and not a reason to abort the sweep");
    }

    [Fact]
    public async Task PullOnly_direction_never_pushes_local_changes()
    {
        await CreateSpace("pull5");
        await AddConfiguredSource("pull5", direction: InstanceSyncDirection.PullOnly);

        await CreateMarkdown("pull5/local-only", "Local Only", "must stay local");

        await Task.Delay(1000, TestContext.Current.CancellationToken);
        Remote.Node("pull5/local-only").Should().BeNull("PullOnly sources must not push");
        var cfg = await Sync.ReadConfig("pull5", "partner").Timeout(10.Seconds()).ToTask();
        cfg!.PendingChanges.Should().BeEmpty("PullOnly sources do not accumulate a push manifest");
    }
}
