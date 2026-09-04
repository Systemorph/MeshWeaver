using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The stall verdict must carry the evidence that turns it into a diagnosis: the recursive
/// disposal snapshot — queue depths, the message occupying the action block and its age, the
/// pending callbacks (#1701). Without it the log names a phase and nothing else, and a backlog,
/// a frozen turn, an in-flight construction and a child answering on its own budget all read
/// alike. The verdict is also what production's red-log triage files as an issue, so it is what a
/// reproduction has to start from.
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
    public async Task DisposalStallVerdict_NamesTheMessageHoldingTheActionBlock_WithQueueDepths()
    {
        var entered = new AsyncSubject<Unit>();
        var release = 0;
        // A turn that holds the block and is blind to cancellation — the shape whose verdict has
        // to be read off the log alone, because nothing else about it ever terminates.
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "diagnostics"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<WedgeEvent>((_, d) =>
            {
                entered.OnNext(Unit.Default);
                entered.OnCompleted();
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TimeSpan.FromSeconds(60));
                return d.Processed();
            }), HostedHubCreation.Always)!;

        try
        {
            victim.Post(new WedgeEvent(), o => o.WithTarget(victim.Address));
            await entered.Should().Within(10.Seconds()).Emit("the turn must hold the block before we dispose");

            victim.Dispose();

            var verdict = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
                .Where(_ => !_capture.Entries.IsEmpty)
                .Select(_ => _capture.Entries.First())
                .FirstAsync()
                .Timeout(TimeSpan.FromSeconds(25))
                .Await(TestContext.Current.CancellationToken);
            Output.WriteLine(verdict);

            verdict.Should().Contain("Queue(buffer=",
                "the verdict must carry the wedged hub's queue depths — a backlog and a frozen "
                + "turn produce the same RunLevel and are told apart by nothing else");
            verdict.Should().Contain(nameof(WedgeEvent),
                "the verdict must NAME the message occupying the action block; without it the "
                + "log identifies a phase and leaves the culprit to guesswork (#1701)");
            verdict.Should().Contain("Executing(",
                "the verdict must carry how long the turn has held the block");
            verdict.Should().Contain(victim.Address.ToString(),
                "the verdict must name the hub, so a reproduction knows where to look");
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
        await victim.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(TestTimeouts.Convergence);
    }

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
                if (message.Contains("DISPOSAL DEADLOCK DETECTED", StringComparison.Ordinal)
                    || message.Contains("[DISPOSE-WEDGE]", StringComparison.Ordinal))
                    sink.Enqueue(message);
            }
        }
    }
}
