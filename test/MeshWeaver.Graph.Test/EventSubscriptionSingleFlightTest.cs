using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A timer slot runs ONCE across replicas. Every replica runs its own <see cref="EventSubscriptionRunner"/>
/// with its own in-memory timer over the same durable subscription, so at the slot all of them fire. Observed
/// on memex at 02:00:25Z: the nightly content re-index started once per replica (three activities), each
/// re-indexing every file. The runners here share one mesh the way replicas share one store: N runners, one
/// subscription, one due slot, ONE continuation.
/// </summary>
public class EventSubscriptionSingleFlightTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string PostPath = "SingleFlightSpace";
    private const int Replicas = 3;

    private sealed class CountingHandler : IEventContinuationHandler
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public EventContinuationType ContinuationType => EventContinuationType.PublishSocialPost;

        public IObservable<MeshNode> Execute(EventSubscription subscription, string _)
        {
            Interlocked.Increment(ref calls);
            return Observable.Return(new MeshNode(subscription.TargetPath ?? PostPath));
        }
    }

    private readonly CountingHandler handler = new();
    private readonly List<EventSubscriptionRunner> runners = [];

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddMeshNodes(new MeshNode(PostPath) { Name = "Single flight", NodeType = "Space" })
            .ConfigureServices(services => services.AddSingleton<IEventContinuationHandler>(handler));

    private async Task StartReplicas()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var changeFeed = Mesh.ServiceProvider.GetRequiredService<IMeshChangeFeed>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        for (var i = 0; i < Replicas; i++)
        {
            var runner = new EventSubscriptionRunner(Mesh, changeFeed, meshService, accessService,
                Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>())
            {
                OneShotClaimRetention = TimeSpan.FromSeconds(1),
            };
            runners.Add(runner);
            await runner.StartAsync(default);
        }
    }

    [Fact(Timeout = 60000)]
    public async Task ARepeatingSlot_DueOnEveryReplica_RunsTheContinuationOnce()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var slot = DateTimeOffset.UtcNow.AddSeconds(-1);
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.Timer,
            FireAt = slot,
            RepeatEvery = TimeSpan.FromHours(24),
            ContinuationType = EventContinuationType.PublishSocialPost,
            TargetPath = PostPath,
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit(cancellationToken: TestContext.Current.CancellationToken);

        await StartReplicas();

        // The winner records the next slot; every loser has by then read the subscription and decided.
        await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(n => n?.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(x => x?.FireAt > DateTimeOffset.UtcNow)
            .FirstAsync().Timeout(40.Seconds()).Await();

        Assert.Equal(1, handler.Calls);

        // The claim is the record of who ran the slot.
        var claim = await Mesh.GetWorkspace()
            .GetMeshNodeStream(EventFiringClaimNodeType.Path(subscription.Id, slot))
            .Where(n => n is not null)
            .FirstAsync().Timeout(10.Seconds()).Await();
        Assert.NotNull(claim);
        Assert.Equal(EventFiringClaimNodeType.NodeType, claim.NodeType);
    }

    [Fact(Timeout = 60000)]
    public async Task AOneShotSlot_DueOnEveryReplica_RunsTheContinuationOnce()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.Timer,
            FireAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            ContinuationType = EventContinuationType.PublishSocialPost,
            TargetPath = PostPath,
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit(cancellationToken: TestContext.Current.CancellationToken);

        await StartReplicas();

        var final = await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(n => n?.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(s => s is not null and not { Status: EventSubscriptionStatus.Pending })
            .FirstAsync().Timeout(40.Seconds()).Await();

        Assert.NotNull(final);
        Assert.Equal(EventSubscriptionStatus.Fired, final.Status);
        Assert.Equal(1, handler.Calls);

        // A one-shot has no later firing whose sweep would remove its claim: its winner retires it.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var remaining = await Observable.Interval(TimeSpan.FromMilliseconds(500))
            .SelectMany(_ => accessService.RunAsSystem(() => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{EventFiringClaimNodeType.Namespace} scope:children nodeType:{EventFiringClaimNodeType.NodeType}"))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .Take(1)))
            .Select(c => (c.Items ?? []).Count(n => n.Id.StartsWith(subscription.Id + "_", StringComparison.Ordinal)))
            .Where(count => count == 0)
            .FirstAsync().Timeout(30.Seconds()).Await();
        Assert.Equal(0, remaining);
    }

    /// <summary>
    /// A claim whose holder died mid-fire (older than the lease, next slot never recorded) does not end the
    /// schedule: every replica reaching the overdue slot sees it stale, and exactly ONE takes it over.
    /// </summary>
    [Fact(Timeout = 60000)]
    public async Task AnAbandonedClaim_IsTakenOverByExactlyOneReplica()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var slot = DateTimeOffset.UtcNow.AddSeconds(-1);
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.Timer,
            FireAt = slot,
            RepeatEvery = TimeSpan.FromHours(24),
            ContinuationType = EventContinuationType.PublishSocialPost,
            TargetPath = PostPath,
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit(cancellationToken: TestContext.Current.CancellationToken);

        // The dead holder's claim, two leases old.
        using (accessService.ImpersonateAsSystem())
            await meshService.CreateNode(new MeshNode(EventFiringClaimNodeType.IdOf(subscription.Id, slot), EventFiringClaimNodeType.Namespace)
            {
                NodeType = EventFiringClaimNodeType.NodeType,
                Name = "abandoned",
                Content = new EventFiringClaim
                {
                    SubscriptionId = subscription.Id,
                    Slot = slot,
                    ClaimedAt = DateTimeOffset.UtcNow - 2 * EventFiringClaimNodeType.Lease,
                    ClaimedBy = "dead-replica/1",
                },
            }).Should().Emit(cancellationToken: TestContext.Current.CancellationToken);

        await StartReplicas();

        await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(n => n?.ContentAs<EventSubscription>(Mesh.JsonSerializerOptions))
            .Where(x => x?.FireAt > DateTimeOffset.UtcNow)
            .FirstAsync().Timeout(40.Seconds()).Await();

        Assert.Equal(1, handler.Calls);
    }

    /// <summary>The claim trail is bounded: after N firings of a repeater at most two claim nodes remain.</summary>
    [Fact(Timeout = 90000)]
    public async Task ManyFirings_LeaveAtMostTwoClaimNodes()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var subscription = new EventSubscription
        {
            TriggerType = EventTriggerType.Timer,
            FireAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            RepeatEvery = TimeSpan.FromSeconds(1),
            ContinuationType = EventContinuationType.PublishSocialPost,
            TargetPath = PostPath,
        };
        await EventSubscriptionOps.CreateSubscription(meshService, subscription).Should().Emit(cancellationToken: TestContext.Current.CancellationToken);

        await StartReplicas();

        // N firings: the continuation ran at least 6 times (once per slot, not once per replica per slot).
        await Mesh.GetWorkspace().GetMeshNodeStream(EventSubscriptionNodeType.Path(subscription.Id))
            .Select(_ => handler.Calls)
            .Where(calls => calls >= 6)
            .FirstAsync().Timeout(60.Seconds()).Await();

        // The sweep follows each recorded firing, so the trail settles at one or two nodes — never six.
        var remaining = await Observable.Interval(TimeSpan.FromMilliseconds(500))
            .SelectMany(_ => accessService.RunAsSystem(() => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{EventFiringClaimNodeType.Namespace} scope:children nodeType:{EventFiringClaimNodeType.NodeType}"))
                .Where(c => c.ChangeType == QueryChangeType.Initial)
                .Take(1)))
            .Select(c => (c.Items ?? []).Count(n => n.Id.StartsWith(subscription.Id + "_", StringComparison.Ordinal)))
            .Where(count => count <= 2)
            .FirstAsync().Timeout(30.Seconds()).Await();
        Assert.InRange(remaining, 1, 2);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var runner in runners)
            runner.Dispose();
        await base.DisposeAsync();
    }
}
