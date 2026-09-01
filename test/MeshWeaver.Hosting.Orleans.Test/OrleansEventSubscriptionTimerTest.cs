using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// <see cref="EventSubscriptionRunner"/>'s TIMER path on the ORLEANS hosting lane — the mechanism
/// every deferred reaction rides: an email invite landing a grant on sign-up, a delegated
/// sub-thread resuming its parent, and (since the scheduler) a social post publishing at its slot.
///
/// <para>🚨 <b>Why this file exists.</b> Nothing in this assembly touched
/// <c>EventSubscriptionRunner</c> before it — the whole durable-deferred-work mechanism was
/// covered only against an in-memory monolith mesh. Production is Orleans: the mesh service, the
/// change feed and the node streams the runner reads all resolve through grains there, and the
/// runner is a hosted service outside them. "It works in the monolith" is not evidence about the
/// thing that actually runs.</para>
///
/// <para>🚨 <b>And the timer path in particular was never really exercised.</b> Every existing
/// timer test uses a <c>FireAt</c> in the PAST, which takes the startup-reconcile branch and fires
/// immediately. That proves restart-safety and nothing about scheduling: a runner that fired every
/// pending timer the instant it saw it would pass all of them, while publishing every scheduled
/// post the moment it was scheduled. <see cref="FutureTimer_DoesNotFireEarly_AndFiresAtItsSlot"/>
/// asserts the negative half first, which is the half that can actually be wrong.</para>
/// </summary>
public class OrleansEventSubscriptionTimerTest(ITestOutputHelper output) : OrleansMeshTestBase(output)
{
    private IMessageHub Mesh => Fixture.ClientMesh;

    private EventSubscriptionRunner StartRunner() =>
        StartRunner(out _);

    private EventSubscriptionRunner StartRunner(out IMeshService meshService)
    {
        var sp = Mesh.ServiceProvider;
        meshService = sp.GetRequiredService<IMeshService>();
        var runner = new EventSubscriptionRunner(
            Mesh,
            sp.GetRequiredService<IMeshChangeFeed>(),
            meshService,
            sp.GetRequiredService<AccessService>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>());
        runner.StartAsync(default).GetAwaiter().GetResult();
        return runner;
    }

    /// <summary>
    /// A timer due in the FUTURE must still be Pending well after the runner has started and has
    /// certainly seen it, and must fire once its slot passes.
    ///
    /// <para>The early-fire assertion is the point. A scheduled post whose timer fires on arrival
    /// goes out immediately instead of at 08:00, and every past-FireAt test in the codebase would
    /// still be green — the bug would reach production looking exactly like a working scheduler.</para>
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task FutureTimer_DoesNotFireEarly_AndFiresAtItsSlot()
    {
        const string space = "TimerSpace";
        var subject = $"timersubject{Guid.NewGuid():N}"[..20];
        var slot = DateTimeOffset.UtcNow.AddSeconds(12);

        using var runner = StartRunner(out var meshService);
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using (access.ImpersonateAsSystem())
            await meshService.CreateOrUpdateNode(
                new MeshNode(space) { Name = "Timer Space", NodeType = "Markdown" })
                .FirstAsync().Await();

        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.Timer,
            FireAt = slot,
            ContinuationType = EventContinuationType.GrantSpaceAccess,
            SubjectId = subject,
            TargetPath = space,
            Role = "Editor",
        };
        using (access.ImpersonateAsSystem())
            await EventSubscriptionOps.CreateSubscription(meshService, subscription).FirstAsync().Await();

        // ── the negative half ───────────────────────────────────────────────────────────────
        // Give the runner ample time to observe the new subscription and schedule it, then assert
        // it has NOT fired. This window is deliberately well inside the slot: if it were close to
        // it, a slow runner would make the test pass for the wrong reason.
        await Task.Delay(5.Seconds());
        var early = await ReadSubscription(subscription.Id);
        Assert.True(early is { Status: EventSubscriptionStatus.Pending },
            $"a timer due at {slot:o} fired early (status {early?.Status}) — it would publish a "
            + "scheduled post the moment it was scheduled rather than at its slot");

        // ── the positive half ───────────────────────────────────────────────────────────────
        var fired = await Mesh.GetWorkspace()
            .GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(n => n?.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(60.Seconds());

        Assert.True(fired!.Status == EventSubscriptionStatus.Fired,
            $"timer ended {fired.Status}: {fired.LastError}");

        // The continuation really ran — a Fired flag with no effect behind it is the failure mode
        // that makes a scheduler look healthy while nothing reaches the outside world.
        await Mesh.GetWorkspace().GetMeshNodeStream($"{space}/_Access/{subject}_Access")
            .Where(n => n?.ContentAs<AccessAssignment>(Mesh.JsonSerializerOptions) is { } a
                        && a.Roles.Any(r => r.Role == "Editor" && !r.Denied))
            .FirstAsync().Timeout(30.Seconds());
    }

    /// <summary>
    /// Restart safety on Orleans: a slot that passed while nothing was running fires on the next
    /// runner start. This is what makes a missed deploy window not silently swallow a post — the
    /// subscription node is durable, and Pending → Fired is what gates re-entry.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TimerDueWhileNothingRan_FiresOnTheNextStart()
    {
        const string space = "TimerSpaceLate";
        var subject = $"latesubject{Guid.NewGuid():N}"[..20];

        var sp = Mesh.ServiceProvider;
        var meshService = sp.GetRequiredService<IMeshService>();
        var access = sp.GetRequiredService<AccessService>();

        using (access.ImpersonateAsSystem())
            await meshService.CreateOrUpdateNode(
                new MeshNode(space) { Name = "Late Timer Space", NodeType = "Markdown" })
                .FirstAsync().Await();

        // Written with NO runner alive — the "due during downtime" shape.
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.Timer,
            FireAt = DateTimeOffset.UtcNow.AddSeconds(-30),
            ContinuationType = EventContinuationType.GrantSpaceAccess,
            SubjectId = subject,
            TargetPath = space,
            Role = "Editor",
        };
        using (access.ImpersonateAsSystem())
            await EventSubscriptionOps.CreateSubscription(meshService, subscription).FirstAsync().Await();

        using var runner = StartRunner();

        var fired = await Mesh.GetWorkspace()
            .GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(n => n?.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(60.Seconds());

        Assert.True(fired!.Status == EventSubscriptionStatus.Fired,
            $"an overdue timer ended {fired.Status}: {fired.LastError}");
    }

    /// <summary>Reads the subscription without waiting — a query, not a node stream, because a
    /// stream on a path that does not exist yet errors instead of reporting absence.</summary>
    private async Task<EventSubscription?> ReadSubscription(string id) =>
        await Mesh.GetWorkspace().GetQuery("orleans-timer-subscriptions",
                $"path:{EventSubscriptionNodeType.Namespace} scope:children "
                + $"nodeType:{EventSubscriptionNodeType.NodeType} select:path,id,namespace,name,nodeType,content")
            .Select(nodes => nodes
                .Select(n => n.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
                .FirstOrDefault(s => s?.Id == id))
            .FirstAsync().Timeout(20.Seconds());
}
