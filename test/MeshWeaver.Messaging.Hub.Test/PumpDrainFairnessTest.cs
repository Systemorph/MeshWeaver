using System.Collections.Concurrent;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// A busy hub must return its worker to the scheduler between batches of synchronous turns.
/// Otherwise a self-posting hub monopolizes a worker indefinitely, even though peer drains are
/// queued globally with PreferFairness (#3593's remaining scheduler-starvation shape, #4847).
/// </summary>
public class PumpDrainFairnessTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record NextTurn(int Index);

    [Fact]
    public async Task SelfPostingHub_YieldsToAPeersShutdown_BeforeItsBacklogEnds()
    {
        const int turnCount = 1024;
        var schedulerPair = new ConcurrentExclusiveSchedulerPair(
            TaskScheduler.Default, maxConcurrencyLevel: 1, maxItemsPerTask: 1);
        var scheduler = schedulerPair.ExclusiveScheduler;
        var processed = new ConcurrentQueue<int>();
        using var finished = new AsyncSubject<Unit>();
        var processedWhenPeerQuiesced = -1;
        var wrongScheduler = 0;

        var peer = (MessageHub)Mesh.GetHostedHub(new Address("peer", "pump-fairness"), c => c
            .WithTaskScheduler(scheduler)
            .WithPostingIdentity(PostingIdentity.System), HostedHubCreation.Always)!;
        var busy = (MessageHub)Mesh.GetHostedHub(new Address("busy", "pump-fairness"), c => c
            .WithTaskScheduler(scheduler)
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<NextTurn>((h, delivery) =>
            {
                if (TaskScheduler.Current != scheduler)
                    Interlocked.Increment(ref wrongScheduler);
                processed.Enqueue(delivery.Message.Index);
                if (delivery.Message.Index == 0)
                    peer.Dispose();

                if (delivery.Message.Index + 1 < turnCount)
                    h.Post(new NextTurn(delivery.Message.Index + 1), o => o.WithTarget(h.Address));
                else
                {
                    finished.OnNext(Unit.Default);
                    finished.OnCompleted();
                }
                return delivery.Processed();
            }), HostedHubCreation.Always)!;

        await peer.RunLevelChanged.Where(level => level == MessageHubRunLevel.Started)
            .Should().Within(TestTimeouts.Quick).Emit("the peer must finish initialization");
        await busy.RunLevelChanged.Where(level => level == MessageHubRunLevel.Started)
            .Should().Within(TestTimeouts.Quick).Emit("the busy hub must finish initialization");
        using var observation = peer.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Quiescing).Take(1)
            .Subscribe(_ => Volatile.Write(ref processedWhenPeerQuiesced, processed.Count));

        try
        {
            busy.Post(new NextTurn(0), o => o.WithTarget(busy.Address));
            await finished.Should().Within(TestTimeouts.Convergence)
                .Emit("every synchronous turn must still run, in FIFO order");
            await peer.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
                .Emit("the peer's shutdown must finish on the shared scheduler");

            processedWhenPeerQuiesced.Should().BeInRange(1, turnCount - 1,
                "a queued peer must get a turn before the busy hub exhausts its own work; "
                + "an unbounded drain trampoline owns the worker for the entire self-post chain");
            processed.Should().Equal(Enumerable.Range(0, turnCount),
                "yielding must preserve the busy hub's FIFO order without dropping or duplicating turns");
            wrongScheduler.Should().Be(0, "every resumed batch must use the original turn scheduler");
        }
        finally
        {
            busy.Dispose();
            peer.Dispose();
        }

        await busy.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
            .Emit("the busy hub must also dispose after its last turn");
        schedulerPair.Complete();
        await schedulerPair.Completion;
    }
}
