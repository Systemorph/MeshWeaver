using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Integration test for <see cref="ScheduledPostWatcher"/> against a REAL monolith mesh — the half of
/// scheduling that turns a post's <c>scheduledAt</c> into an armed timer.
///
/// <para>🚨 <b>The query is the point.</b> The watcher finds its candidates with a live
/// <c>status:Scheduled</c> query, and a query that matches nothing fails EXACTLY like the bug this
/// whole change fixes: no error, no log line anyone reads, and posts that quietly never go out. A unit
/// test over the predicate would not catch that — only a real mesh answering a real query does. So the
/// mesh here is never mocked.</para>
/// </summary>
public class ScheduledPostWatcherTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(SocialTestNodeTypes.PostNodeType);

    /// <summary>A post sitting at <c>Scheduled</c> with a slot gets a durable Timer subscription
    /// pointing at it, carrying that exact slot.</summary>
    [Fact(Timeout = 60000)]
    public async Task ScheduledPost_getsATimerArmedForItsSlot()
    {
        var postPath = $"TestData/sched_{Guid.NewGuid():N}/post1";
        var slot = DateTimeOffset.UtcNow.AddHours(6);
        await SeedPostAsync(postPath, status: "Scheduled", scheduledAt: slot.ToString("o"));

        using var watcher = StartWatcher();

        var armed = await AwaitSubscription(postPath);
        Assert.Equal(EventTriggerType.Timer, armed.TriggerType);
        Assert.Equal(EventContinuationType.PublishSocialPost, armed.ContinuationType);
        Assert.Equal(postPath, armed.TargetPath);
        Assert.Equal(EventSubscriptionStatus.Pending, armed.Status);
        Assert.NotNull(armed.FireAt);
        // Round-tripped through JSON storage — compare the instant, not the offset representation.
        Assert.True(Math.Abs((armed.FireAt!.Value - slot).TotalSeconds) < 2,
            $"armed for {armed.FireAt:o}, expected {slot:o}");
    }

    /// <summary>
    /// 🚨 A post that already went out is never armed. This is the production shape from 2026-08-18: a
    /// post was published by hand and an agent then wrote a FUTURE slot onto the same node. With a
    /// publisher wired up, arming that would post it to LinkedIn a second time — irreversibly.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AlreadyPublishedPost_isNeverArmed_evenWithAFutureSlot()
    {
        var postPath = $"TestData/sched_{Guid.NewGuid():N}/post1";
        await SeedPostAsync(postPath, status: "Published",
            scheduledAt: DateTimeOffset.UtcNow.AddHours(6).ToString("o"),
            publishedUrn: "urn:li:share:4242");

        // A scheduled post seeded alongside it gives the watcher something it MUST arm — so this test
        // proves the published one was skipped, not merely that the watcher never ran.
        var controlPath = $"TestData/sched_{Guid.NewGuid():N}/post1";
        await SeedPostAsync(controlPath, status: "Scheduled",
            scheduledAt: DateTimeOffset.UtcNow.AddHours(6).ToString("o"));

        using var watcher = StartWatcher();

        await AwaitSubscription(controlPath);   // the watcher has demonstrably done a pass

        var published = await ReadSubscription(postPath);
        Assert.Null(published);
    }

    /// <summary>Re-scheduling MOVES the post's timer instead of stacking a second one beside it — the
    /// id is derived from the post path for exactly this reason.</summary>
    [Fact(Timeout = 60000)]
    public async Task ReschedulingAPost_movesItsTimer_ratherThanAddingASecond()
    {
        var postPath = $"TestData/sched_{Guid.NewGuid():N}/post1";
        var first = DateTimeOffset.UtcNow.AddHours(6);
        await SeedPostAsync(postPath, status: "Scheduled", scheduledAt: first.ToString("o"));

        using var watcher = StartWatcher();
        await AwaitSubscription(postPath);

        // Move the slot the way an agent or an MCP patch does — straight onto the content field.
        var moved = DateTimeOffset.UtcNow.AddHours(30);
        await Mesh.GetWorkspace().GetMeshNodeStream(postPath)
            .Update(node => node with
            {
                Content = new Dictionary<string, object?>
                {
                    ["body"] = "Scheduled body",
                    ["authorPath"] = "TestData/profile",
                    ["status"] = "Scheduled",
                    ["scheduledAt"] = moved.ToString("o"),
                },
            })
            .Should().Emit();

        var rearmed = await Timers(postPath)
            .Where(s => s?.FireAt is { } f && Math.Abs((f - moved).TotalSeconds) < 2)
            .FirstAsync().Timeout(40.Seconds());

        Assert.Equal(postPath, rearmed!.TargetPath);
    }

    // ---- helpers ----

    private ScheduledPostWatcher StartWatcher()
    {
        var watcher = new ScheduledPostWatcher(
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh.ServiceProvider.GetRequiredService<AccessService>(),
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<ScheduledPostWatcher>>());
        watcher.StartAsync(default).GetAwaiter().GetResult();
        return watcher;
    }

    private Task SeedPostAsync(
        string postPath, string status, string scheduledAt, string? publishedUrn = null)
    {
        var (id, ns) = LinkedInPublishServiceTest.SplitPath(postPath);
        var content = new Dictionary<string, object?>
        {
            ["body"] = "Scheduled body",
            ["authorPath"] = "TestData/profile",
            ["status"] = status,
            ["scheduledAt"] = scheduledAt,
        };
        if (publishedUrn is not null)
            content["publishedUrn"] = publishedUrn;
        return NodeFactory.CreateNode(new MeshNode(id, ns)
        {
            Name = "Scheduled post",
            NodeType = "Systemorph/Post",
            State = MeshNodeState.Active,
            Content = content,
        }).Should().Emit();
    }

    /// <summary>The live subscription set. A QUERY, not a node stream on the expected path: a stream
    /// opened on a node that does not exist yet errors immediately instead of waiting for it to
    /// appear, which is precisely what we are waiting for here.</summary>
    private IObservable<EventSubscription?> Timers(string postPath)
    {
        var id = ScheduledPostWatcher.SubscriptionId(postPath);
        // 🚨 CONSTANT query id, filtered in code — never $"timers-{id}". A per-call id mints a new
        // workspace query-registry entry for every post, and those entries outlive the test that
        // made them: they keep the workspace, and through it the mesh hub, reachable after
        // disposal. That is precisely what MeshHubDisposalLeakTest catches, and it catches it in
        // whatever class happens to run next rather than here. Same rule the runner states for its
        // own pending-subscription query.
        return Mesh.GetWorkspace().GetQuery("test-publish-timers",
                $"path:{EventSubscriptionNodeType.Namespace} scope:children "
                + $"nodeType:{EventSubscriptionNodeType.NodeType} select:path,id,namespace,name,nodeType,content")
            .Select(nodes => nodes
                .Select(n => n.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
                .FirstOrDefault(s => s?.Id == id));
    }

    private async Task<EventSubscription> AwaitSubscription(string postPath) =>
        (await Timers(postPath).Where(s => s is not null).FirstAsync().Timeout(40.Seconds()))!;

    private async Task<EventSubscription?> ReadSubscription(string postPath) =>
        await Timers(postPath).FirstAsync().Timeout(10.Seconds());
}
