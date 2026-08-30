using System;
using System.Collections.Concurrent;
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
/// 🚨 #1701 — <b>an owner must not out-run the mechanism that answers it.</b>
///
/// <para><b>The inversion.</b> The disposal watchdog is armed per hub, at that hub's OWN
/// <c>Dispose()</c>, for a flat 8 s at every depth. A child's <c>Dispose()</c> is NOT called at the
/// parent's t=0 — it is called in the parent's <c>DisposeHostedHubs</c> phase, i.e. strictly AFTER
/// the parent's quiesce budget. So a child's watchdog is armed strictly later than its parent's, for
/// the same duration: whenever a child needs its own watchdog to produce an answer, the PARENT's
/// always expires first. The parent then logs
/// <c>DISPOSAL DEADLOCK DETECTED … RunLevel=DisposeHostedHubs</c> and force-tears-down a subtree that
/// was working correctly.</para>
///
/// <para>That is exactly the defect #1317 fixed one level down — <c>DisposeHubsReactive</c>'s flat
/// 5 s cap, removed with the note that <i>"a child's answer is guaranteed terminal, just not inside
/// 5 s"</i> — left in place one level up, between an owner's watchdog and its children's. It is why
/// #1701's captures show TWO <c>[FORCE-TEARDOWN]</c> pairs of which only the inner one carries
/// information, and it is a real production cost: the 8 s force-teardown is inside the recycle window
/// that clients must ride out (#1996 measured recovery at 10.06 s against a 7.75 s retry budget).</para>
///
/// <para><b>The fix is a shape, not a bound.</b> The watchdog measures a STALL instead of a
/// DURATION: it re-arms on every disposal-progress signal from anywhere in the subtree (each hub's
/// <c>RunLevel</c> transitions, recursively — never a heartbeat, so quiet means genuinely stopped).
/// A healthy nested teardown keeps it re-armed however deep it is; a hub that stops moving still
/// trips 8 s later, with the message finally TRUE.</para>
/// </summary>
public class DisposalStallWatchdogTest : HubTestBase
{
    private readonly DeadlockLogCapture capture = new();

    public DisposalStallWatchdogTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(capture));
    }

    /// <summary>Posted to a hub that handles it WITHOUT responding, so the observing callback
    /// stays pending and that hub's Quiescing phase burns its whole budget.</summary>
    private record NeverAnswered;

    /// <summary>Slow turns, queued deep enough to starve the phased shutdown — a genuine wedge.</summary>
    private record WedgeEvent;

    /// <summary>Per level: long enough that the whole chain outlasts the 8 s watchdog, short
    /// enough that no single gap between progress signals reaches it.</summary>
    private static readonly TimeSpan PerLevelQuiesce = TimeSpan.FromSeconds(3);
    private const int ChainDepth = 4;   // 4 × 3 s ≈ 12 s > 8 s, in 3 s steps

    /// <summary>
    /// A nested teardown that legitimately takes LONGER than the watchdog window, while every
    /// individual step stays well inside it. Four hosted hubs, each holding one unanswered callback
    /// so its Quiescing phase runs its full 3 s budget: the chain needs ~12 s end to end, in 3 s
    /// steps, and nothing is wedged at any point.
    ///
    /// <para><b>Non-vacuity.</b> On <c>origin/main</c> the root's fixed 8 s timer expires at t=8 —
    /// four seconds before the subtree it is waiting on could possibly have finished — and logs
    /// <c>DISPOSAL DEADLOCK DETECTED</c>, force-tearing down a healthy teardown mid-flight.</para>
    /// </summary>
    [Fact(Timeout = 180_000)]
    public async Task NestedTeardownSlowerThanTheWindow_DoesNotTripTheWatchdog()
    {
        var chain = BuildQuiesceChain();
        var root = chain[0];

        var started = DateTime.UtcNow;
        root.Dispose();
        await root.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(120.Seconds());
        var elapsed = DateTime.UtcNow - started;

        // The premise of the test, asserted rather than assumed: the teardown really did outlast
        // the watchdog window. If it did not, "no deadlock was logged" would prove nothing.
        elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(9),
            "the chain must genuinely take longer than the 8 s watchdog window, or this test is vacuous "
            + $"(actual {elapsed.TotalSeconds:F1}s over {ChainDepth} levels of {PerLevelQuiesce.TotalSeconds}s)");

        foreach (var entry in capture.Entries)
            Output.WriteLine(entry);

        capture.Entries.Should().BeEmpty(
            "nothing was wedged — every level advanced through its phases within 3 s of the last. "
            + "A watchdog that fires here is measuring DURATION, and it out-runs the very mechanism "
            + "that answers it: a child's watchdog is armed one quiesce budget later than its "
            + $"parent's, for the same 8 s. Elapsed {elapsed.TotalSeconds:F1}s.");
    }

    /// <summary>
    /// The safety half: a hub that genuinely stops moving must STILL trip. Re-arming on progress
    /// must not be a way of switching the backstop off — a stall watchdog that never fires is just
    /// a deleted watchdog.
    ///
    /// <para>Same wedge recipe as <c>DisposalDeadlockDiagnosticsTest</c>: a FIFO backlog of slow
    /// turns that the phased <c>ShutdownRequest</c> cannot jump. The hub emits no phase transition
    /// while starved, so there is no progress to re-arm on — and the report still names the message
    /// holding the action block.</para>
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task GenuinelyWedgedHub_StillTripsTheWatchdog()
    {
        var wedgeTurns = 0;
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "stall-watchdog"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<WedgeEvent>((_, d) =>
            {
                Interlocked.Increment(ref wedgeTurns);
                Thread.Sleep(15);
                return d.Processed();
            }), HostedHubCreation.Always)!;

        for (var i = 0; i < 800; i++)
            victim.Post(new WedgeEvent(), o => o.WithTarget(victim.Address));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        victim.Dispose();
        await victim.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(60.Seconds());

        var deadlock = capture.Entries.FirstOrDefault();
        deadlock.Should().NotBeNull(
            "a hub whose pump is starved emits no phase transition, so there is no progress to re-arm "
            + "on and the watchdog must still fire "
            + $"(wedge turns processed: {Volatile.Read(ref wedgeTurns)})");
        Output.WriteLine(deadlock!);

        deadlock!.Should().Contain("made no teardown progress",
            "the message must now say what is actually true — no progress — rather than conflating "
            + "'slow but progressing' with 'wedged'");
        deadlock.Should().Contain(nameof(WedgeEvent),
            "the report must still NAME the message occupying the action block (#1701's diagnostic half)");
    }

    /// <summary>
    /// Builds <see cref="ChainDepth"/> nested hosted hubs, each with a long Quiescing budget it
    /// will actually burn: one unanswered self-request per hub keeps its <c>responseSubjects</c>
    /// non-empty, so the Quiescing drain runs to its deadline instead of completing early.
    /// </summary>
    private MessageHub[] BuildQuiesceChain()
    {
        var chain = new MessageHub[ChainDepth];
        IMessageHub parent = Mesh;
        for (var level = 0; level < ChainDepth; level++)
        {
            var hub = (MessageHub)parent.GetHostedHub(
                new Address("chain", $"level{level}"),
                c => c.WithPostingIdentity(PostingIdentity.System)
                      .WithQuiesceTimeout(PerLevelQuiesce)
                      // Handled, never answered: the observing callback below stays pending.
                      .WithHandler<NeverAnswered>((_, d) => d.Processed()),
                HostedHubCreation.Always)!;

            // One pending callback is enough — Quiescing polls for responseSubjects to EMPTY.
            hub.Observe(new NeverAnswered(), o => o.WithTarget(hub.Address)).Subscribe(_ => { }, _ => { });

            chain[level] = hub;
            parent = hub;
        }
        return chain;
    }

    /// <summary>Keeps only the disposal-deadlock lines (the provider sees every category).</summary>
    private sealed class DeadlockLogCapture : ILoggerProvider
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
                if (message.Contains("DISPOSAL DEADLOCK DETECTED", StringComparison.Ordinal))
                    sink.Enqueue(message);
            }
        }
    }
}
