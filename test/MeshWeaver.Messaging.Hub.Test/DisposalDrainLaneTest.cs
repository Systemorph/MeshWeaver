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
    /// A dispose action whose store I/O is slow must not hold a lane thread: its leaf runs on the I/O
    /// pool (<c>IIoPool.Invoke</c>), so the lane's drain ends and the thread is free while the I/O is
    /// still outstanding (#5799 recurrence — memex-cloud's 30 s Npgsql timeout on a dispose action).
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ADisposeActionDoingSlowIo_DoesNotHoldALaneThread()
    {
        var lane = (DisposalDrainLane)Mesh.ServiceProvider.GetRequiredService<IDisposalDrainScheduler>();
        var pool = Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>().Get(IoPoolNames.Blob);
        var ioStarted = new ManualResetEventSlim(false);
        using var ioGate = new ManualResetEventSlim(false);

        var hub = (MessageHub)Mesh.GetHostedHub(new Address("victim", "disposal-lane-slow-io"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<Ping>((h, d) =>
            {
                h.Post(new Pong(), o => o.ResponseFor(d));
                return d.Processed();
            }), HostedHubCreation.Always)!;
        hub.RegisterForDisposal(_ => pool.Invoke(async ct =>
        {
            ioStarted.Set();
            // The slow store call: holds a POOL task for as long as the gate is shut, never a lane thread.
            await Task.Run(() => ioGate.Wait(TestTimeouts.Convergence, ct), ct);
        }));

        await hub.Observe(new Ping(), o => o.WithTarget(hub.Address))
            .Should().Within(TestTimeouts.Quick)
            .Emit("the hub must be fully started before its disposal is put under test",
                cancellationToken: TestContext.Current.CancellationToken);

        try
        {
            hub.Dispose();
            ioStarted.Wait(TestTimeouts.Quick, TestContext.Current.CancellationToken)
                .Should().BeTrue("the dispose action's I/O must have started");

            SpinWait.SpinUntil(() => lane.ActiveDrains == 0, TestTimeouts.Quick)
                .Should().BeTrue("with the dispose action's I/O still outstanding (" + ioGate.IsSet
                    + "), no drain may still be running on the disposal lane: the I/O leaf runs on the pool, "
                    + "so a lane turn never blocks on the network");
            ioGate.IsSet.Should().BeFalse("the lane was free while the I/O was STILL slow");
        }
        finally
        {
            ioGate.Set();
        }

        await hub.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
            .Emit("teardown completes", cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A saturated lane is LOUD: when a drain is queued while every lane thread runs a drain, a Warning
    /// names the turns holding them. A drain that blocks is reported with the message it is executing.
    /// </summary>
    [Fact]
    public void ASaturatedLane_WarnsNamingTheBlockingTurns()
    {
        var sink = new CapturingLoggerFactory();
        using var lane = new DisposalDrainLane(sink);
        var tracked = Enumerable.Range(0, DisposalDrainLane.MaxThreads)
            .Select(i => lane.TrackDrain("victim/blocked-" + i, () => "ShutdownRequest")).ToArray();

        lane.NoteQueued("victim/starved");

        foreach (var t in tracked) t.Dispose();
        sink.Warnings.Should().ContainSingle()
            .Which.Should().Contain("victim/starved").And.Contain("ShutdownRequest")
            .And.Contain("victim/blocked-0").And.Contain("saturated");
    }

    [Fact]
    public void ALaneWithFreeThreads_DoesNotWarn()
    {
        var sink = new CapturingLoggerFactory();
        using var lane = new DisposalDrainLane(sink);
        using var one = lane.TrackDrain("victim/a", () => null);
        lane.NoteQueued("victim/b");
        sink.Warnings.Should().BeEmpty();
    }

    private sealed class CapturingLoggerFactory : Microsoft.Extensions.Logging.ILoggerFactory
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Warnings { get; } = new();
        public void AddProvider(Microsoft.Extensions.Logging.ILoggerProvider provider) { }
        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Capture(Warnings);
        public void Dispose() { }

        private sealed class Capture(System.Collections.Concurrent.ConcurrentQueue<string> sink)
            : Microsoft.Extensions.Logging.ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
                TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                    sink.Enqueue(formatter(state, exception));
            }
        }
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
