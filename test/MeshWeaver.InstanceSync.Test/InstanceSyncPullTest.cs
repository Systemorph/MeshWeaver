using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
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
