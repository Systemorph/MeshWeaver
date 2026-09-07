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
