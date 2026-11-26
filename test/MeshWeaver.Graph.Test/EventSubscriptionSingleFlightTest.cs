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
            return Observable.Return(new MeshNode(subscription.TargetPath!));
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
                Mesh.ServiceProvider.GetService<Microsoft.Extensions.Logging.ILogger<EventSubscriptionRunner>>());
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
        Assert.Equal(EventFiringClaimNodeType.NodeType, claim!.NodeType);
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

        Assert.Equal(EventSubscriptionStatus.Fired, final!.Status);
        Assert.Equal(1, handler.Calls);
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var runner in runners)
            runner.Dispose();
        await base.DisposeAsync();
    }
}
