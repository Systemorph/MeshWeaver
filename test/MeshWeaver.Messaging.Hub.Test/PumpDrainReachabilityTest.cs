using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// A hub's drain must be reachable by a thread OTHER than the one that posted to it (#3593).
///
/// <para><b>The production shape.</b> On 2026-09-07 a memex-cloud pod reported 47 <c>sync/*</c>
/// hubs, all at <c>RunLevel=Started</c> with <c>Disposal=Pending</c>, queue depth 1, the drain flag
/// latched and nothing dequeued — every one of them within 41 ms of the others, one cascade, each
/// reported exactly once and never again. <c>Dispose()</c> had posted its
/// <c>ShutdownRequest(Quiescing)</c> and the action block never took it off the queue.</para>
///
/// <para><b>The mechanism, measured rather than argued.</b> <c>MessageService.ScheduleDrainOne</c>
/// hands the drain to <c>Task.Factory.StartNew(…, turnScheduler)</c>. On
/// <see cref="TaskScheduler.Default"/> — which is every hosted hub — a task queued from a THREAD
/// POOL WORKER lands on that worker's LOCAL LIFO queue, and a mass disposal posts every child's
/// shutdown from one worker (<c>HostedHubsCollection.DisposeHubsReactive</c> calls
/// <c>h.Dispose()</c> sequentially inside the owner's own turn). Work parked there is reachable
/// only by the owning worker once it returns to the dispatcher, or by work-stealing — and,
/// measured on this host with every worker busy in a non-blocking loop, it is invisible to BOTH:
/// 18 drains sat unstarted for a full 20 s with <c>ThreadPool.ThreadCount</c> pinned at 18, because
/// the pool's starvation detection does not see a busy worker's local queue and therefore never
/// injects a thread. The same tasks with <see cref="TaskCreationOptions.PreferFairness"/> go to the
/// GLOBAL queue, which starvation detection does see: a thread was injected and every drain ran.
/// So the stall is not "slow under load" — it is UNBOUNDED, and it makes one hub's pump wait on an
/// unrelated hub's pump, which is exactly what the actor model forbids.</para>
///
/// <para><b>What this test builds.</b> A <see cref="TaskScheduler"/> that models those two queues
/// faithfully — a task without <c>PreferFairness</c>, queued from a thread currently running one of
/// its tasks, goes to that thread's local queue and is drained only when that thread finishes;
/// everything else goes to the shared queue and is dispatched at once. A <c>driver</c> hub's turn
/// disposes the <c>victim</c> hub and then PARKS, exactly as a mass-disposal cascade parks its
/// worker on the next child. The victim must reach <see cref="MessageHubRunLevel.Quiescing"/> while
/// that worker is still parked — i.e. its ShutdownRequest must be dequeued by somebody else.</para>
///
/// <para>🚨 <b>No gate, no bridge.</b> The park is a <c>volatile int</c> polled under a bounded
/// <see cref="SpinWait.SpinUntil(System.Func{bool}, TimeSpan)"/> and released in a <c>finally</c> —
/// the one shape the house rules sanction for a worker a test deliberately parks. Nothing here
/// waits on a <c>SemaphoreSlim</c>, a <c>TaskCompletionSource</c> or a <c>ManualResetEvent</c>.</para>
/// </summary>
public class PumpDrainReachabilityTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record ParkAfterDisposing(IMessageHub Victim) : IRequest<Parked>;

    private record Parked;

    private record Ping : IRequest<Pong>;

    private record Pong;

    /// <summary>
    /// The park's own state, shared between the driver hub's turn and the assertion. Instance
    /// fields — nothing static, nothing process-wide.
    /// </summary>
    private int release;

    private int parkEntered;

    private int parkExited;

    [Fact(Timeout = 120_000)]
    public async Task ADrainScheduledFromAnotherHubsTurn_DoesNotWaitForThatTurnToFinish()
    {
        var scheduler = new LocalAndSharedQueueScheduler();

        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "pump-reachability"), c => c
            .WithTaskScheduler(scheduler)
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<Ping>((h, d) =>
            {
                h.Post(new Pong(), o => o.ResponseFor(d));
                return d.Processed();
            }), HostedHubCreation.Always)!;

        var driver = (MessageHub)Mesh.GetHostedHub(new Address("driver", "pump-reachability"), c => c
            .WithTaskScheduler(scheduler)
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<Ping>((h, d) =>
            {
                h.Post(new Pong(), o => o.ResponseFor(d));
                return d.Processed();
            })
            .WithHandler<ParkAfterDisposing>((h, d) =>
            {
                // This is the production frame: an owner's turn disposes a child — which only
                // POSTS the ShutdownRequest and schedules the child's drain from THIS thread —
                // and then goes on doing its own work on the same worker.
                d.Message.Victim.Dispose();
                Volatile.Write(ref parkEntered, 1);
                try
                {
                    SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TestTimeouts.Convergence);
                }
                finally
                {
                    // Written in a finally so a failing assertion can never strand this worker.
                    Volatile.Write(ref parkExited, 1);
                }

                h.Post(new Parked(), o => o.ResponseFor(d));
                return d.Processed();
            }), HostedHubCreation.Always)!;

        try
        {
            // Bring both hubs up on a working scheduler first. A hub that never finished STARTING
            // takes a different disposal path (Dispose cancels its initialization outright), and a
            // test that disposed before this point would be measuring that instead.
            await victim.Observe(new Ping(), o => o.WithTarget(victim.Address))
                .Should().Within(TestTimeouts.Quick)
                .Emit("the victim must be fully started before its pump is put under test");
            victim.RunLevel.Should().Be(MessageHubRunLevel.Started,
                "the wedge under test is a STARTED hub whose queued ShutdownRequest is never "
                + "dequeued — not a hub that never finished starting");

            var reachedQuiescing = victim.RunLevelChanged
                .Where(level => level >= MessageHubRunLevel.Quiescing)
                .Replay(1);
            using var _ = reachedQuiescing.Connect();

            // Fire-and-forget: the driver's turn will not answer until the park is released, and
            // releasing it is what this test must NOT have to do to make the victim move.
            driver.Post(new ParkAfterDisposing(victim), o => o.WithTarget(driver.Address));

            await Observable.Interval(TestTimeouts.Quick / 50).StartWith(0L)
                .Where(_ => Volatile.Read(ref parkEntered) == 1)
                .FirstAsync()
                .Timeout(TestTimeouts.Quick)
                .Await(TestContext.Current.CancellationToken);
            Volatile.Read(ref parkExited).Should().Be(0,
                "PRECONDITION: the driver's worker must still be parked. If it had already been "
                + "released, the victim could be drained by the very thread that scheduled its "
                + "drain and this test would measure nothing");

            await reachedQuiescing.Should().Within(TestTimeouts.Quick)
                .Emit("Dispose() posted ShutdownRequest(Quiescing) and the pump latched its drain "
                    + "flag — so the drain MUST be reachable by a thread other than the one that "
                    + "posted it. Queued on that thread's local LIFO queue it is not: it waits for "
                    + "an unrelated hub's turn to finish, which is the 47-hub wedge of #3593");

            Volatile.Read(ref parkExited).Should().Be(0,
                "the victim advanced while the driver's worker was STILL parked — that, and not the "
                + "advance alone, is what says the drain did not ride the posting thread. A pass "
                + "with the park already over would be the unfixed behaviour wearing a green tick");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }

        await driver.Observe(new Ping(), o => o.WithTarget(driver.Address))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the driver's worker is released, so its pump keeps working normally — the wedge "
                + "under test is about WHERE the victim's drain was queued, not about the driver");

        await victim.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
            .Emit("the victim's teardown completes once its pump turns");
        victim.RunLevel.Should().Be(MessageHubRunLevel.Dead);
    }

    /// <summary>
    /// A faithful model of the two queues <see cref="TaskScheduler.Default"/> actually has.
    ///
    /// <list type="bullet">
    ///   <item><b>Local queue</b> — a task WITHOUT <see cref="TaskCreationOptions.PreferFairness"/>
    ///     queued from a thread that is currently running one of this scheduler's tasks is pushed
    ///     onto that thread's own LIFO queue, and runs only once that thread returns to the
    ///     dispatcher. This is what the real pool does, and (measured on this host) such work is
    ///     reachable by neither work-stealing nor the pool's starvation detection while every
    ///     worker is busy.</item>
    ///   <item><b>Shared queue</b> — everything else, dispatched to the thread pool at once. This
    ///     is what <see cref="TaskCreationOptions.PreferFairness"/> asks for, and the reason the
    ///     real pool can grow to service it.</item>
    /// </list>
    ///
    /// <para>🚨 All state is per-instance. A <c>[ThreadStatic]</c> or a static map would be
    /// process-wide state that outlives the mesh and bleeds into the next test.</para>
    /// </summary>
    private sealed class LocalAndSharedQueueScheduler : TaskScheduler
    {
        private readonly ConcurrentDictionary<int, ConcurrentStack<Task>> localQueues = new();

        protected override void QueueTask(Task task)
        {
            if (!task.CreationOptions.HasFlag(TaskCreationOptions.PreferFairness)
                && localQueues.TryGetValue(Environment.CurrentManagedThreadId, out var mine))
            {
                mine.Push(task);
                return;
            }

            ThreadPool.UnsafeQueueUserWorkItem(_ => RunAsWorker(task), null);
        }

        private void RunAsWorker(Task task)
        {
            var workerId = Environment.CurrentManagedThreadId;
            var mine = localQueues.GetOrAdd(workerId, _ => new ConcurrentStack<Task>());
            try
            {
                TryExecuteTask(task);
                // Back in the dispatcher: this worker drains its own local queue, LIFO, exactly as
                // a pool worker does before looking at the global queue or stealing.
                while (mine.TryPop(out var queued))
                    TryExecuteTask(queued);
            }
            finally
            {
                localQueues.TryRemove(workerId, out _);
            }
        }

        // Inlining would let the POSTING thread run the drain, which is the delivery this test
        // withholds. Always false — the hub must be given a thread of its own or get nothing.
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() =>
            localQueues.Values.SelectMany(q => q.ToArray()).ToArray();
    }
}
