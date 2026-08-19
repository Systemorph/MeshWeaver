using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Social;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// POST SCHEDULING on the Orleans hosting lane, end to end: a post carrying a future
/// <c>scheduledAt</c> gets a durable timer armed for it, the timer holds until its slot, and the
/// slot dispatches the publish continuation.
///
/// <para>🚨 <b>Why on Orleans specifically.</b> <see cref="ScheduledPostWatcher"/> finds its
/// candidates with a live mesh QUERY, and a query is exactly the thing that behaves differently
/// between an in-memory workspace and grain-backed storage. That is not hypothetical: the first
/// version of the watcher queried <c>status:Scheduled</c>, which matched NOTHING while looking
/// perfectly healthy — no error, no empty-result warning, just a scheduler that armed nothing.
/// A test that only ever ran in-memory is not evidence that the query works where it has to.</para>
///
/// <para>The publish leaf itself (the LinkedIn HTTP call) is stubbed out of this lane by
/// construction: no <c>IEventContinuationHandler</c> is registered in the test cluster, so the
/// fired timer dispatches and reports that the owning module is absent. That is deliberate — this
/// test is about the SCHEDULING chain reaching the publish step at the right moment; the publish
/// step's own behaviour is pinned in <c>EventContinuationHandlerTest</c> and
/// <c>LinkedInPublishServiceTest</c>.</para>
/// </summary>
public class OrleansScheduledPostTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    private IMessageHub Mesh => Fixture.ClientMesh;

    /// <summary>The whole chain: scheduled post → armed timer → held until the slot → dispatched.</summary>
    [Fact(Timeout = 180000)]
    public async Task ScheduledPost_ArmsATimer_HoldsIt_ThenDispatchesAtTheSlot()
    {
        var postPath = $"TestData/orleanspost{Guid.NewGuid():N}";
        var slot = DateTimeOffset.UtcNow.AddSeconds(15);

        await SeedPostAsync(postPath, status: "Scheduled", scheduledAt: slot.ToString("o"));

        using var watcher = StartWatcher();
        using var runner = StartRunner();

        // ── armed, with the post's OWN slot ─────────────────────────────────────────────────
        var armed = await AwaitTimer(postPath, s => s is not null);
        Assert.Equal(EventTriggerType.Timer, armed!.TriggerType);
        Assert.Equal(EventContinuationType.PublishSocialPost, armed.ContinuationType);
        Assert.Equal(postPath, armed.TargetPath);
        Assert.NotNull(armed.FireAt);
        Assert.True(Math.Abs((armed.FireAt!.Value - slot).TotalSeconds) < 3,
            $"armed for {armed.FireAt:o} but the post's slot is {slot:o}");

        // ── HELD, not fired on arrival ──────────────────────────────────────────────────────
        // The half that can actually be wrong. A timer that fires when it is armed publishes the
        // post the moment someone schedules it — and every past-FireAt test would stay green.
        Assert.Equal(EventSubscriptionStatus.Pending, armed.Status);
        await Task.Delay(5.Seconds());
        var stillPending = await ReadTimer(postPath);
        Assert.True(stillPending is { Status: EventSubscriptionStatus.Pending },
            $"the timer left Pending before its slot (status {stillPending?.Status}) — a post "
            + "would go out the instant it was scheduled");

        // ── fires at the slot, and dispatches the PUBLISH continuation ──────────────────────
        var settled = await AwaitTimer(postPath,
            s => s is not null and not { Status: EventSubscriptionStatus.Pending }, 90);

        // No handler is registered on this cluster, so a dispatched publish reports the missing
        // module. That message IS the assertion: reaching it proves the timer fired at its slot
        // and routed to the publish continuation rather than quietly expiring.
        Assert.Equal(EventSubscriptionStatus.Failed, settled!.Status);
        Assert.Contains("PublishSocialPost", settled.LastError ?? string.Empty);
        Assert.Contains("IEventContinuationHandler", settled.LastError ?? string.Empty);
    }

    /// <summary>
    /// 🚨 The double-post guard, on Orleans: a post that already went out is never armed, even
    /// when a later write puts a FUTURE slot on it. That is the exact shape seen in production on
    /// 2026-08-18 — a post published by hand, then re-slotted by an agent. With a publisher live,
    /// arming it would post it to the network a second time, irreversibly.
    /// </summary>
    [Fact(Timeout = 180000)]
    public async Task PublishedPost_IsNeverArmed_EvenWithAFutureSlot()
    {
        var published = $"TestData/orleanspub{Guid.NewGuid():N}";
        var control = $"TestData/orleansctl{Guid.NewGuid():N}";
        var slot = DateTimeOffset.UtcNow.AddSeconds(20).ToString("o");

        await SeedPostAsync(published, status: "Published", scheduledAt: slot,
            publishedUrn: "urn:li:share:4242");
        // A schedulable post alongside it, so a green result cannot mean "the watcher never ran".
        await SeedPostAsync(control, status: "Scheduled", scheduledAt: slot);

        using var watcher = StartWatcher();

        await AwaitTimer(control, s => s is not null);      // the watcher has demonstrably passed
        Assert.Null(await ReadTimer(published));
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────

    private ScheduledPostWatcher StartWatcher()
    {
        var sp = Mesh.ServiceProvider;
        var watcher = new ScheduledPostWatcher(
            Mesh,
            sp.GetRequiredService<IMeshService>(),
            sp.GetRequiredService<AccessService>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<ScheduledPostWatcher>>());
        watcher.StartAsync(default).GetAwaiter().GetResult();
        return watcher;
    }

    private EventSubscriptionRunner StartRunner()
    {
        var sp = Mesh.ServiceProvider;
        var runner = new EventSubscriptionRunner(
            Mesh,
            sp.GetRequiredService<IMeshChangeFeed>(),
            sp.GetRequiredService<IMeshService>(),
            sp.GetRequiredService<AccessService>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>());
        runner.StartAsync(default).GetAwaiter().GetResult();
        return runner;
    }

    private async Task SeedPostAsync(
        string postPath, string status, string scheduledAt, string? publishedUrn = null)
    {
        var cut = postPath.LastIndexOf('/');
        var content = new Dictionary<string, object?>
        {
            ["text"] = "Scheduled on Orleans",
            ["authorPath"] = "TestData/profile",
            ["status"] = status,
            ["scheduledAt"] = scheduledAt,
        };
        if (publishedUrn is not null)
            content["publishedUrn"] = publishedUrn;

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        using (access.ImpersonateAsSystem())
            await meshService.CreateOrUpdateNode(new MeshNode(postPath[(cut + 1)..], postPath[..cut])
            {
                Name = "Orleans scheduled post",
                NodeType = "Systemorph/Post",
                State = MeshNodeState.Active,
                Content = content,
            }).FirstAsync().ToTask();
    }

    /// <summary>The post's timer, read through a live QUERY with a CONSTANT id — a stream on a path
    /// that does not exist yet errors instead of reporting absence, and a per-call query id leaks a
    /// registry entry that keeps the mesh hub alive past disposal.</summary>
    private IObservable<EventSubscription?> Timers(string postPath)
    {
        var id = ScheduledPostWatcher.SubscriptionId(postPath);
        return Mesh.GetWorkspace().GetQuery("orleans-post-timers",
                $"path:{EventSubscriptionNodeType.Namespace} scope:children "
                + $"nodeType:{EventSubscriptionNodeType.NodeType} select:path,id,namespace,name,nodeType,content")
            .Select(nodes => nodes
                .Select(n => n.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
                .FirstOrDefault(s => s?.Id == id));
    }

    private async Task<EventSubscription?> AwaitTimer(
        string postPath, Func<EventSubscription?, bool> predicate, int seconds = 45) =>
        await Timers(postPath).Where(predicate).FirstAsync().Timeout(seconds.Seconds());

    private async Task<EventSubscription?> ReadTimer(string postPath) =>
        await Timers(postPath).FirstAsync().Timeout(20.Seconds());
}
