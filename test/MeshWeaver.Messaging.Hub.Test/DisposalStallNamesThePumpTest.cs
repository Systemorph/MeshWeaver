using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The stall verdict must MEASURE what it asserts (#3593).
///
/// <para><b>The shape that had no verdict.</b> On 2026-09-06 a memex-cloud pod produced 47
/// simultaneous <c>DISPOSAL DEADLOCK DETECTED</c> lines, every one of them ending
/// <i>"No turn is executing on this hub, so the stall is in a hosted hub or a join it is waiting
/// on"</i> — for hubs still at <c>RunLevel=Started</c>, which have not reached the child-disposal
/// phase and are therefore waiting on no child and no join. The taxonomy had a hole (queue
/// non-empty, drain latched, nothing dequeued) and the hole fell through into the "stalled below"
/// bucket, so 47 reports sent every reader to children that were not the problem.</para>
///
/// <para><b>Why the neighbouring suites could not catch it.</b> Every wedge
/// <see cref="DisposalStallWatchdogTest"/> and <see cref="DisposalDeadlockDiagnosticsTest"/> build
/// is a HANDLER holding the action block — <c>CurrentMessage != null</c> — so the branch whose
/// message this test pins (<c>CurrentMessage == null</c> while the drain flag is latched) had never
/// been exercised by anything. This test builds that state directly: a hub whose
/// <see cref="TaskScheduler"/> stops delivering threads after the hub has finished starting, so the
/// drain that <c>Dispose()</c>'s own <c>ShutdownRequest</c> schedules is queued and never runs.</para>
///
/// <para><b>What is NOT claimed.</b> This does not assert that disposal completes — it cannot, and
/// asserting it would be asserting the wedge is fixed. The subject is the VERDICT: with the pump
/// frozen, the report must name the pump and must not attribute the stall to a child.</para>
/// </summary>
public class DisposalStallNamesThePumpTest : HubTestBase
{
    private readonly VerdictCapture capture = new();

    public DisposalStallNamesThePumpTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(capture));
    }

    private record Ping : IRequest<Pong>;

    private record Pong;

    [Fact(Timeout = 120_000)]
    public async Task ADrainThatIsScheduledAndNeverRuns_IsNamedAsThePump_NotAsAChild()
    {
        var scheduler = new PausableTaskScheduler();
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "pump-never-drains"), c => c
            .WithTaskScheduler(scheduler)
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<Ping>((h, d) =>
            {
                h.Post(new Pong(), o => o.ResponseFor(d));
                return d.Processed();
            }), HostedHubCreation.Always)!;

        try
        {
            // Bring-up must COMPLETE first. Dispose() cancels an unfinished initialization outright
            // (RunLevel < Started), which is a different path and a different verdict — a test that
            // froze the scheduler before this point would be measuring that instead.
            await victim.Observe(new Ping(), o => o.WithTarget(victim.Address))
                .Should().Within(TestTimeouts.Quick)
                .Emit("the hub must answer once, on its own scheduler, before that scheduler is frozen");
            victim.RunLevel.Should().Be(MessageHubRunLevel.Started,
                "the wedge under test is a STARTED hub whose pump stops turning — not a hub that "
                + "never finished starting");

            // From here the scheduler accepts tasks and runs none. Dispose() posts its
            // ShutdownRequest, the turn loop latches `draining` and schedules exactly one DrainOne
            // onto this scheduler — which holds it. Queue depth 1, drain latched, nothing dequeued,
            // nothing on the block: the production shape, reproduced.
            scheduler.Pause();
            victim.Dispose();

            var verdict = await FirstVerdict(TestTimeouts.Convergence);
            Output.WriteLine(verdict);

            verdict.Should().Contain("THE PUMP IS NOT TURNING",
                "the queued ShutdownRequest was never handed to the pipeline, so the finding is "
                + "THIS hub's turn scheduling — the verdict has to say so");
            verdict.Should().Contain("drainsInFlight=0",
                "`exec` used to be a hard-coded literal 0 that no reader could distinguish from a "
                + "measurement; the field now counts drain bodies actually executing, and reading "
                + "zero here is what says the scheduled drain never ran");
            // 🚨 The second half of #3593. drainsInFlight=0 alone does not say WHY nothing is
            // running: "the scheduler took a drain and never ran it" and "the latch is set and
            // nothing was ever scheduled" both read zero, and they have opposite owners. This
            // scheduler ACCEPTED the drain — GetScheduledTasks holds it — so the report must say
            // so as a measurement rather than assert it.
            verdict.Should().Contain("drainsAwaitingScheduler=1",
                "exactly one drain was handed to this scheduler and it is still holding it, which "
                + "is the fact that distinguishes a dead scheduler from a leaked latch");
            verdict.Should().Contain("the turn scheduler ACCEPTED",
                "the MECHANISM clause must name the scheduler as the owner of this stall — the "
                + "line used to assert 'a drain is scheduled on this hub's TaskScheduler' without "
                + "anything measuring it");
            verdict.Should().NotContain("defect in MessageService.ScheduleDrainOne",
                "the pump did its job here: it scheduled the drain. Blaming the pump for a "
                + "scheduler that will not run work is the same misattribution as blaming a child");
            verdict.Should().Contain("NO turn was dequeued",
                "turnsCompleted counts HANDLER completions and is blind to a turn that never "
                + "reached a handler — the dequeue counter is what separates a pump that never "
                + "started from one wedged inside a handler");
            verdict.Should().NotContain("the stall is in a hosted hub or a join it is waiting on",
                "this hub has not reached the child-disposal phase, so it is waiting on no child "
                + "and no join. That sentence was printed 47 times about exactly this state "
                + "(#3593) and is the misattribution under repair");
            verdict.Should().Contain(victim.Address.ToString(),
                "the verdict must name the hub, so a reproduction knows where to look");
        }
        finally
        {
            // Releasing the scheduler lets the held drain run, so the ShutdownRequest is processed
            // and the hub tears down through its ordinary phases. Without this the fixture's own
            // dispose would hang on a hub this test deliberately froze.
            scheduler.Resume();
        }

        await victim.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(TestTimeouts.Convergence);
        victim.RunLevel.Should().Be(MessageHubRunLevel.Dead,
            "the wedge is in the scheduler, not in the hub — once turns are delivered again the "
            + "teardown completes normally, which is why this PR claims a nameable wedge and not a "
            + "fixed one");
    }

    /// <summary>
    /// 🚨 <b>A schedule that FAILS must release the drain latch (#3593).</b>
    ///
    /// <para><c>draining == true</c> is read by every disposal verdict as <i>"a drain is running or
    /// queued"</i>. It is set inside the turn gate and the schedule happens outside it, so a throw
    /// from <c>Task.Factory.StartNew</c> used to leave the flag set with nothing outstanding — and
    /// because <c>KickDrain</c> returns immediately whenever the flag is set, the pump was then
    /// frozen for the life of the hub, silently. The fingerprint is exactly the one #3593 reports
    /// (queue non-empty, <c>draining=true</c>, <c>drainsInFlight=0</c>, nothing dequeued), with the
    /// verdict blaming a scheduler that had never been asked.</para>
    ///
    /// <para><b>Fails on unfixed code:</b> the pump stays latched after the refusal, so the message
    /// posted once the scheduler recovers is never dequeued and the response never comes.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ASchedulerThatRefusesADrain_DoesNotLeaveThePumpLatched()
    {
        var scheduler = new RefusingTaskScheduler();
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "pump-refused-schedule"), c => c
            .WithTaskScheduler(scheduler)
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<Ping>((h, d) =>
            {
                h.Post(new Pong(), o => o.ResponseFor(d));
                return d.Processed();
            }), HostedHubCreation.Always)!;

        // Bring-up on a working scheduler, for the same reason as the test above.
        await victim.Observe(new Ping(), o => o.WithTarget(victim.Address))
            .Should().Within(TestTimeouts.Quick)
            .Emit("the hub must answer once before its scheduler starts refusing");

        // Every schedule from here throws. The post latches `draining`, the schedule fails, and the
        // latch must come back off — otherwise nothing this hub is ever sent can be processed again.
        scheduler.Refuse();
        victim.Post(new Ping(), o => o.WithTarget(victim.Address));

        scheduler.Refusals.Should().BeGreaterThan(0,
            "PRECONDITION: the scheduler must actually have refused — otherwise this test measures "
            + "an ordinary post and would pass with the latch leak still present");

        // The recovery, and the whole point: a pump whose latch was released can be driven again.
        scheduler.Accept();
        await victim.Observe(new Ping(), o => o.WithTarget(victim.Address))
            .Should().Within(TestTimeouts.Convergence)
            .Emit("a refused schedule must not latch the pump: once the scheduler accepts work "
                + "again the hub processes messages normally. A leaked latch makes every later "
                + "KickDrain return immediately, so this request would never be dequeued");

        victim.Dispose();
        await victim.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(TestTimeouts.Convergence);
        victim.RunLevel.Should().Be(MessageHubRunLevel.Dead);
    }

    /// <summary>
    /// A <see cref="TaskScheduler"/> that can be told to THROW on queue rather than to hold —
    /// the second failure mode of a real scheduler (a completed
    /// <c>ConcurrentExclusiveSchedulerPair</c>, a torn-down activation) and the one that used to
    /// leak the drain latch.
    /// </summary>
    private sealed class RefusingTaskScheduler : TaskScheduler
    {
        private readonly Lock gate = new();
        private bool refusing;
        private int refusals;

        /// <summary>How many schedules were refused — the test's precondition.</summary>
        public int Refusals => Volatile.Read(ref refusals);

        public void Refuse()
        {
            lock (gate)
                refusing = true;
        }

        public void Accept()
        {
            lock (gate)
                refusing = false;
        }

        protected override void QueueTask(Task task)
        {
            lock (gate)
            {
                if (refusing)
                {
                    Interlocked.Increment(ref refusals);
                    throw new InvalidOperationException(
                        "this scheduler is not accepting work (models a completed scheduler pair "
                        + "or a torn-down Orleans activation)");
                }
            }
            ThreadPool.UnsafeQueueUserWorkItem(_ => TryExecuteTask(task), null);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }

    private async Task<string> FirstVerdict(TimeSpan within) =>
        await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .Where(_ => !capture.Entries.IsEmpty)
            .Select(_ => capture.Entries.First())
            .FirstAsync()
            .Timeout(within)
            .Await(TestContext.Current.CancellationToken);

    /// <summary>
    /// A <see cref="TaskScheduler"/> that can be told to stop delivering threads.
    ///
    /// <para>While running it hands every queued task straight to the thread pool. Once
    /// <see cref="Pause"/> is called it keeps accepting tasks and executes none of them, which is
    /// the whole lever: <c>MessageService.ScheduleDrainOne</c> is the ONLY user of a hub's
    /// configured scheduler, so a paused scheduler means "a drain was scheduled and has not run" —
    /// nothing else about the hub is altered.</para>
    ///
    /// <para>🚨 No <c>SemaphoreSlim</c>, no <c>ManualResetEventSlim</c>, no <c>Task.Delay</c>, no
    /// bridge: the pause is a <c>volatile int</c> read under the same plain lock that guards the
    /// held queue, so <see cref="Pause"/> cannot race a concurrent <see cref="QueueTask"/> into
    /// running-anyway or into being dropped. Nothing here waits on anything.</para>
    /// </summary>
    private sealed class PausableTaskScheduler : TaskScheduler
    {
        private readonly Lock gate = new();
        private readonly Queue<Task> held = new();
        private bool paused;

        /// <summary>Stop delivering threads. Tasks already executing run to completion.</summary>
        public void Pause()
        {
            lock (gate)
                paused = true;
        }

        /// <summary>Deliver threads again, flushing everything held while paused.</summary>
        public void Resume()
        {
            Task[] flush;
            lock (gate)
            {
                paused = false;
                flush = held.ToArray();
                held.Clear();
            }
            foreach (var task in flush)
                Dispatch(task);
        }

        protected override void QueueTask(Task task)
        {
            lock (gate)
            {
                if (paused)
                {
                    held.Enqueue(task);
                    return;
                }
            }
            Dispatch(task);
        }

        // Inlining would let the CALLER's thread run the drain, which is precisely the delivery
        // this test withholds. Always false — the hub must be given a thread or get nothing.
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks()
        {
            lock (gate)
                return held.ToArray();
        }

        private void Dispatch(Task task) =>
            ThreadPool.UnsafeQueueUserWorkItem(_ => TryExecuteTask(task), null);
    }

    /// <summary>Captures every Error-level stall verdict, whatever shape it takes.</summary>
    private sealed class VerdictCapture : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capturing(Entries);

        public void Dispose() { }

        private sealed class Capturing(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Error)
                    return;
                var message = formatter(state, exception);
                if (message.Contains("DISPOSAL DEADLOCK DETECTED", StringComparison.Ordinal)
                    || message.Contains("[DISPOSE-WEDGE]", StringComparison.Ordinal))
                    sink.Enqueue(message);
            }
        }
    }
}
