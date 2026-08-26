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

        using var watcher = await StartWatcherAsync();

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

        using var watcher = await StartWatcherAsync();

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

        using var watcher = await StartWatcherAsync();
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


    /// <summary>
    /// 🚨 A timer armed while the post was Scheduled is CANCELLED once the post stops asking to be
    /// published. Publish by hand at 07:00 and the 08:00 timer would otherwise still fire — the
    /// 2026-08-18 incident with the order reversed, and the re-arm guard does not cover it because
    /// the timer already exists.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task PublishingAPostByHand_CancelsItsArmedTimer()
    {
        var postPath = $"TestData/sched_{Guid.NewGuid():N}/post1";
        await SeedPostAsync(postPath, status: "Scheduled",
            scheduledAt: DateTimeOffset.UtcNow.AddHours(6).ToString("o"));

        using var watcher = await StartWatcherAsync();
        var armed = await AwaitSubscription(postPath);
        Assert.Equal(EventSubscriptionStatus.Pending, armed.Status);

        // Someone hits Publish. The post is live; its timer must not still fire.
        await Mesh.GetWorkspace().GetMeshNodeStream(postPath)
            .Update(node => node with
            {
                Content = new Dictionary<string, object?>
                {
                    ["body"] = "Scheduled body",
                    ["authorPath"] = "TestData/profile",
                    ["status"] = "Published",
                    ["publishedUrn"] = "urn:li:share:777",
                },
            })
            .Should().Emit();

        var cancelled = await Timers(postPath)
            .Where(t => t is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds());
        Assert.Equal(EventSubscriptionStatus.Cancelled, cancelled!.Status);
    }

    /// <summary>
    /// The armed timer names WHO it publishes as. Without it the handler refuses rather than
    /// publishing as system — the credential is chosen by the post's own authorPath, so an un-gated
    /// timed publish could go out through a profile the scheduler may not use.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task ArmedTimer_RecordsTheIdentityThatScheduledThePost()
    {
        var postPath = $"TestData/sched_{Guid.NewGuid():N}/post1";
        await SeedPostAsync(postPath, status: "Scheduled",
            scheduledAt: DateTimeOffset.UtcNow.AddHours(6).ToString("o"));

        using var watcher = await StartWatcherAsync();
        var armed = await AwaitSubscription(postPath);

        // The identity the publish will run as. Asserted exactly, not merely "not blank".
        //
        // 🚨 This assertion CANNOT catch the projection bug it looks like it covers, and saying so
        // is the point. In production the watcher's query projected no lastModifiedBy, so CreatedBy
        // came back null and every publish was refused with "names no CreatedBy" — hours later, at
        // the slot, on a post that looked perfectly scheduled (2026-08-19). This test passed
        // throughout: the IN-MEMORY query provider ignores `select:` and hands back the whole node,
        // so the projection is only real on the Postgres/Orleans path. ProjectionCarriesTheIdentity
        // below guards the string itself, which is the only part a test here can actually hold.
        Assert.Equal(SeededBy, armed.CreatedBy);
    }

    /// <summary>
    /// The candidate query must PROJECT the field the watcher reads. A guard on the string, not on
    /// behaviour — and deliberately so: the in-memory query provider ignores <c>select:</c>, so no
    /// test in this suite can observe a missing projection. What this does catch is the change that
    /// actually caused the outage — someone editing the select and dropping a field the code below
    /// still reads. In production that is silent: the field is null, the timer arms with no
    /// identity, and every publish is refused at its slot.
    /// </summary>
    [Fact]
    public void ProjectionCarriesTheIdentity()
    {
        var field = typeof(ScheduledPostWatcher)
            .GetField("Query", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.True(field is not null,
            "ScheduledPostWatcher.Query (private const string) is gone or was renamed — this test reads it by "
            + "reflection, so update the name here rather than deleting the guard: the projection dropping "
            + "lastModifiedBy is what armed every timer with no identity.");
        var query = field!.GetRawConstantValue() as string;
        Assert.True(query is not null, "ScheduledPostWatcher.Query is no longer a compile-time string const.");
        Assert.Contains("lastModifiedBy", query!, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 A post marked Scheduled but naming NO author profile gets no timer.
    ///
    /// <para>It cannot publish — the credential is chosen by the post's own <c>authorPath</c>, so
    /// <c>LinkedInPublishService</c> refuses it with <c>profile-path-missing</c>. Arming a timer
    /// anyway buys nothing and costs the worst failure mode available: the calendar shows the post
    /// as scheduled, the slot passes, and nothing happens or is said. Posts/RobertHaircuts sat in
    /// exactly that shape (Approved, no author, slot already past) — written straight onto the
    /// content field, which is the only way to reach it, since the workflow button refuses to
    /// approve a post with no profile.</para>
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task APostWithNoAuthorProfile_IsNotArmed()
    {
        var postPath = $"TestData/sched_{Guid.NewGuid():N}/post1";
        await SeedPostAsync(postPath, status: "Scheduled",
            scheduledAt: DateTimeOffset.UtcNow.AddHours(6).ToString("o"),
            authorPath: null);

        // A well-formed post beside it gives the watcher something it MUST arm, so this proves the
        // authorless one was SKIPPED rather than that the watcher simply never ran.
        var controlPath = $"TestData/sched_{Guid.NewGuid():N}/post1";
        await SeedPostAsync(controlPath, status: "Scheduled",
            scheduledAt: DateTimeOffset.UtcNow.AddHours(6).ToString("o"));

        using var watcher = await StartWatcherAsync();

        await AwaitSubscription(controlPath);   // the watcher has demonstrably done a pass

        Assert.Null(await ReadSubscription(postPath));
    }

    // ---- helpers ----

    // 🚨 async, awaited from the (already-async) [Fact]s below — never
    // `.GetAwaiter().GetResult()`. StartAsync's continuations can, in principle, be posted back
    // onto the calling thread; blocking that thread waiting for them is the exact self-deadlock
    // shape #2013 tracks (xUnit's single-threaded sync context self-deadlocking a native wait).
    // Awaiting suspends the test instead of parking its thread, so it cannot self-deadlock.
    private async Task<ScheduledPostWatcher> StartWatcherAsync()
    {
        var watcher = new ScheduledPostWatcher(
            Mesh,
            Mesh.ServiceProvider.GetRequiredService<IMeshService>(),
            Mesh.ServiceProvider.GetRequiredService<AccessService>(),
            Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<ScheduledPostWatcher>>());
        await watcher.StartAsync(default);
        return watcher;
    }

    /// <summary>The identity the seeded posts are written by — what the watcher must carry onto the
    /// timer as the identity the publish will run as. Derived from the DevLogin harness identity
    /// rather than repeated as a literal, so it follows the login context instead of drifting from
    /// it.</summary>
    private static readonly string SeededBy = TestUsers.Admin.Name!;

    private Task SeedPostAsync(
        string postPath, string status, string scheduledAt, string? publishedUrn = null,
        string? authorPath = "TestData/profile")
    {
        var (id, ns) = LinkedInPublishServiceTest.SplitPath(postPath);
        var content = new Dictionary<string, object?>
        {
            ["body"] = "Scheduled body",
            ["status"] = status,
            ["scheduledAt"] = scheduledAt,
        };
        // Omitted entirely, not set to null — an absent key is the shape a real authorless post has.
        if (authorPath is not null)
            content["authorPath"] = authorPath;
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
