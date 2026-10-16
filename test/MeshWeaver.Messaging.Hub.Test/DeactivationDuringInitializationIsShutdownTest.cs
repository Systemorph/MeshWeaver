using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>A host deactivating a hub mid-initialization is a SHUTDOWN, never an initialization
/// failure (#6648).</b>
///
/// <para>An Orleans grain deactivating tears its hub down with
/// <see cref="HubDeactivation.DisposeAbortingCurrentTurn"/>: dispose, then abort the current turn.
/// It used to run the two the other way round — <c>CancelCurrentExecution()</c> first. A hub whose
/// BuildupAction was still pending (a declaration read, a data source's first load) has its
/// <c>InitializeHubRequest</c> on the block, holding the execution token; cancelling that token
/// fires <c>HandleInitialize</c>'s cancellation race SYNCHRONOUSLY, inside
/// <c>CancellationTokenSource.Cancel()</c>, so its <c>.Catch</c> asked <c>IsShuttingDown</c> one
/// statement before <c>Dispose()</c> would have made the answer <c>true</c>. The routine end of an
/// activation was logged at Error — <i>"initialization failed — BuildupAction n of m (…) faulted
/// (OperationCanceledException: Hub X was told to shut down before its initialization completed).
/// Hub is now in FAILED state."</i> — and latched <c>InitializationError</c> on a hub that was
/// leaving: dozens of lines in one second on a portal whose grains deactivated in a wave.</para>
///
/// <para>The BuildupAction here never completes, so the ONLY thing that can end the init turn is
/// the deactivation's cancellation. The positive control is the recognised-shutdown Debug line: it
/// proves the cancellation reached <c>HandleInitialize</c>'s classifier, so the absence of the
/// Error line cannot be explained by the classifier never running.</para>
/// </summary>
public class DeactivationDuringInitializationIsShutdownTest : HubTestBase
{
    private readonly InitLog log = new();

    public DeactivationDuringInitializationIsShutdownTest(ITestOutputHelper output) : base(output)
    {
        Services.AddLogging(l =>
        {
            l.Services.AddSingleton<ILoggerProvider>(log);
            // The positive control is a Debug line, so the capture must see Debug — otherwise "the
            // Error is gone" and "the classifier never ran" would read identically.
            l.AddFilter<InitLog>(null, LogLevel.Debug);
        });
    }

    [Fact]
    public async Task HostDeactivation_OfAHubStillInitializing_IsARecognisedShutdown_NotAFailure()
    {
        var actionEntered = new AsyncSubject<Unit>();
        var address = new Address("deactivate-mid-init", "1");
        var client = GetClient();
        var hub = client.ServiceProvider.CreateMessageHub(
            address,
            c => c
                .WithPostingIdentity(PostingIdentity.System)
                .WithInitialization(_ => Observable.Defer(() =>
                {
                    actionEntered.OnNext(Unit.Default);
                    actionEntered.OnCompleted();
                    // A BuildupAction that never answers — only a cancellation can end this turn.
                    return Observable.Never<Unit>();
                })));

        await actionEntered.Should().Within(TestTimeouts.Convergence)
            .Emit("the BuildupAction must be pending, or the deactivation does not land mid-initialization");

        hub.DisposeAbortingCurrentTurn();

        await hub.DisposalCompleted.Should().Within(TestTimeouts.Convergence)
            .Emit("a hub deactivated mid-initialization must still finish disposing");

        var self = address.ToString();
        log.Lines.Should().Contain(
            l => l.Level == LogLevel.Debug && l.Message.Contains(self, StringComparison.Ordinal)
                 && l.Message.Contains("initialization ended by shutdown", StringComparison.Ordinal),
            "the deactivation's cancellation must reach HandleInitialize's classifier and be recognised "
            + "as a shutdown — without this line the assertions below could pass on a path that never ran");
        log.Lines.Where(l => l.Level >= LogLevel.Error).Select(l => l.Message).Should().NotContain(
            m => m.Contains(self, StringComparison.Ordinal) && m.Contains("initialization failed", StringComparison.Ordinal),
            "a deactivation landing mid-initialization is the routine end of an activation, not an "
            + "initialization failure — it must not be logged at Error");
        ((MessageHub)hub).InitializationError.Should().BeNull(
            "a hub that was told to shut down must not latch the FAILED state on its way out");
    }

    private sealed class InitLog : ILoggerProvider
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capture(Lines);
        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<(LogLevel, string)> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;
            public void Log<TState>(LogLevel level, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => lines.Enqueue((level, formatter(state, exception)));
        }
    }
}
