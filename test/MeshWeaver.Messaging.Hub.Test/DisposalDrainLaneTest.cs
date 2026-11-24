using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// A hub's teardown must not depend on the thread pool's queue (#5799).
///
/// <para>A hosted hub's pump drains on <see cref="TaskScheduler.Default"/>, so its
/// <c>ShutdownRequest</c> waited behind every work item the pool had queued. On a backlogged pool
/// (memex-cloud heartbeats read <c>poolPending=2865</c>, <c>69,012</c>, <c>113,021</c>) the drain sat
/// unstarted for the whole stall budget — <c>THE PUMP IS NOT TURNING</c>, 1,224 times. Making the pool
/// faster (seven earlier changes) could not remove that dependency; running the teardown on the mesh's
/// own <see cref="DisposalDrainLane"/> does.</para>
///
/// <para><b>What this builds.</b> The pool's global queue is filled with far more spinning work items
/// than it has workers (the pool injects a thread only every second or so, so none of them can reach
/// the back of that queue during the test), then a started hub is disposed. Its shutdown must still run
/// — on a lane thread — while every one of those items is still queued or spinning.</para>
/// </summary>
public class DisposalDrainLaneTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Ping : IRequest<Pong>;

    private record Pong;

    private int release;

    [Fact(Timeout = 120_000)]
    public async Task ADisposingHub_ReachesQuiescing_WhileThePoolIsBacklogged()
    {
        var hub = (MessageHub)Mesh.GetHostedHub(new Address("victim", "disposal-lane"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<Ping>((h, d) =>
            {
                h.Post(new Pong(), o => o.ResponseFor(d));
                return d.Processed();
            }), HostedHubCreation.Always)!;

        await hub.Observe(new Ping(), o => o.WithTarget(hub.Address))
            .Should().Within(TestTimeouts.Quick)
            .Emit("the hub must be fully started before its disposal is put under test",
                cancellationToken: TestContext.Current.CancellationToken);

        var reachedQuiescing = hub.RunLevelChanged
            .Where(level => level >= MessageHubRunLevel.Quiescing)
            .Replay(1);
        using var connection = reachedQuiescing.Connect();

        // Far more spinners than the pool will ever inject threads for during the test: FIFO order
        // keeps anything queued after them waiting. Bounded spin, released in the finally.
        var spinners = Math.Max(500, Environment.ProcessorCount * 100);
        try
        {
            for (var i = 0; i < spinners; i++)
                ThreadPool.QueueUserWorkItem(_ =>
                    SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence));
            // Let the workers the pool has actually be occupied.
            await Task.Delay(300, TestContext.Current.CancellationToken);

            hub.Dispose();

            await reachedQuiescing.Should().Within(TestTimeouts.Quick)
                .Emit("Dispose() must not queue the hub's shutdown behind the thread pool's backlog — it "
                    + "runs on the mesh's dedicated disposal lane (#5799)",
                    cancellationToken: TestContext.Current.CancellationToken);
            Volatile.Read(ref release).Should().Be(0,
                "the hub advanced while the pool was STILL saturated — that, and not the advance alone, "
                + "says its drain did not ride the pool");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        await hub.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
            .Emit("teardown completes", cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheLane_RunsTasksOnItsOwnNamedThreads_NotThePool()
    {
        using var lane = new DisposalDrainLane();
        var task = Task.Factory.StartNew(
            () => (Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.Name),
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, lane);
        var (isPool, name) = await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        isPool.Should().BeFalse();
        name.Should().Be("mw-disposal-lane");
    }
}
