using System;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.DependencyInjection;
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
public class DisposalDrainLaneTest : HubTestBase
{
    public DisposalDrainLaneTest(ITestOutputHelper output) : base(output)
    {
        // The mesh's teardown services — including the disposal lane — on the root container, where
        // MeshBuilder puts them; hosted hubs resolve it from there.
        Services.AddIoPools();
    }

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

    /// <summary>
    /// The recurrence: the lane started with 16 threads, and a mass teardown whose turns BLOCK (a
    /// disposal action waiting on a database timeout) parked all sixteen, so the next disposing hub's
    /// first turn — <c>RunLevel=Started</c>, queue depth 1 — queued behind them. Here more lane threads
    /// than the old cap are held blocked, and the pool is saturated too; a started hub must still reach
    /// Quiescing. Fails with <c>DisposalDrainLane.MaxThreads == 16</c>.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABlockedLaneBeyondTheOldCap_DoesNotStarveADisposingHub()
    {
        var hub = (MessageHub)Mesh.GetHostedHub(new Address("victim", "disposal-lane-saturated"), c => c
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

        var lane = (DisposalDrainLane)Mesh.ServiceProvider.GetRequiredService<IDisposalDrainScheduler>();
        var blockers = 40; // far above the old 16-thread cap
        using var gate = new ManualResetEventSlim(false);
        var running = new CountdownEvent(16); // the old cap: enough to saturate it
        try
        {
            for (var i = 0; i < blockers; i++)
                _ = Task.Factory.StartNew(() =>
                {
                    running.Signal();
                    gate.Wait(TestTimeouts.Convergence);
                }, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    lane.Scheduler);
            running.Wait(TestTimeouts.Quick, TestContext.Current.CancellationToken)
                .Should().BeTrue("the old cap's worth of blockers must be holding lane threads before the hub is disposed");

            hub.Dispose();

            await reachedQuiescing.Should().Within(TestTimeouts.Quick)
                .Emit("a disposing hub's first turn must get a lane thread even while "
                    + blockers + " lane turns are blocked (#5799 recurrence)",
                    cancellationToken: TestContext.Current.CancellationToken);
            gate.IsSet.Should().BeFalse("the hub advanced while the lane's blockers were STILL held");
        }
        finally
        {
            gate.Set();
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
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, lane.Scheduler);
        var (isPool, name) = await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        isPool.Should().BeFalse();
        name.Should().Be(DisposalDrainLane.ThreadName);
    }
}
