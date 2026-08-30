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
/// #1701 — the disposal-deadlock error must carry the EVIDENCE, not just the phase.
///
/// <para><b>What was wrong.</b> When the watchdog fires, the log said only
/// <c>"Hub &lt;X&gt; did not complete shutdown within 00:00:08. RunLevel=DisposeHostedHubs"</c>.
/// <c>RunLevel=DisposeHostedHubs</c> means "a hosted child has not answered yet" and NOTHING
/// about which child or why — and at least four different mechanisms produce it: a child whose
/// pump is starved behind a FIFO backlog, a child frozen on one non-terminating turn, an
/// in-flight hosted-hub construction the join is (correctly) waiting out, and a child that simply
/// answers on its OWN watchdog — which is armed one quiesce budget LATER than the parent's and
/// therefore structurally cannot answer before it. #1701 sat on "the compile-pool children are
/// the prime suspects" for months because the log could not tell those apart.</para>
///
/// <para><b>What is pinned.</b> The error now carries the recursive disposal snapshot: the wedged
/// hub's queue depths and — decisive — the message currently occupying its action block, by name
/// and elapsed. Those two numbers are what discriminate "starved behind a backlog"
/// (<c>buffer</c> large, <c>Executing</c> a few ms) from "frozen on one turn" (<c>buffer</c>
/// small, <c>Executing</c> thousands of ms) from "waiting on a child" (this hub idle, a CHILD
/// line carrying the backlog).</para>
///
/// <para>The wedge itself is the harness <c>MessageHubTest.Dispose_WhenActionBlockWedged_…</c>
/// already uses — a genuine FIFO backlog of slow turns that the phased <c>ShutdownRequest</c>
/// cannot jump — so this test asserts on a REAL watchdog trip, never a simulated one.</para>
/// </summary>
public class DisposalDeadlockDiagnosticsTest : HubTestBase
{
    private readonly DeadlockLogCapture _capture = new();

    public DisposalDeadlockDiagnosticsTest(ITestOutputHelper output)
        : base(output)
    {
        Services.AddLogging(l => l.Services.AddSingleton<ILoggerProvider>(_capture));
    }

    private record WedgeEvent;

    [Fact(Timeout = 120_000)]
    public async Task DisposalDeadlock_NamesTheMessageHoldingTheActionBlock()
    {
        var wedgeTurns = 0;
        // Same starvation recipe as MessageHubTest's zombie-hub regression: a queue backlog of
        // slow turns, sized under the storm breaker's trip threshold (a tripped flood is dropped
        // at ingestion and never builds a backlog) and long enough to outlast the 8 s watchdog.
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "diagnostics"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<WedgeEvent>((_, d) =>
            {
                Interlocked.Increment(ref wedgeTurns);
                Thread.Sleep(15);
                return d.Processed();
            }), HostedHubCreation.Always)!;

        for (var i = 0; i < 800; i++)
            victim.Post(new WedgeEvent(), o => o.WithTarget(victim.Address));
        // Let the first turn start so the backlog is genuinely queued when Dispose posts.
        await Task.Delay(100, TestContext.Current.CancellationToken);

        victim.Dispose();
        await victim.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(30.Seconds());

        var deadlock = _capture.Entries.FirstOrDefault(e => e.Contains("DISPOSAL DEADLOCK DETECTED"));
        deadlock.Should().NotBeNull(
            "the backlog must starve the phased shutdown so the watchdog is what completes disposal "
            + $"(wedge turns processed: {Volatile.Read(ref wedgeTurns)})");
        Output.WriteLine(deadlock!);

        deadlock!.Should().Contain("Queue(buffer=",
            "the deadlock report must carry the wedged hub's queue depths — a backlog and a frozen "
            + "turn produce the same RunLevel and are told apart by nothing else");
        deadlock.Should().Contain(nameof(WedgeEvent),
            "the deadlock report must NAME the message occupying the action block; without it the "
            + "log identifies a phase and leaves the culprit to guesswork (#1701)");
    }

    /// <summary>
    /// Keeps only the disposal-deadlock lines — the provider is attached to every category, so
    /// storing everything would make the capture the test's dominant cost.
    /// </summary>
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
