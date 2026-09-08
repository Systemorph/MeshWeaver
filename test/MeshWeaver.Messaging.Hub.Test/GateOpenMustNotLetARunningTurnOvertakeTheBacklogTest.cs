using System;
using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Deterministic repro for the RESIDUAL half of MeshWeaver.Plugins#1394 — the half that survived
/// MeshWeaver#3408 and kept <c>ActivationBacklogFifoTest</c> failing on CI as <c>B, A, C</c> and
/// <c>C, A, B</c>: <b>a message that arrived later is processed first because its turn was already
/// running when the last initialization gate opened.</b>
///
/// <para><b>What #3408 fixed, and what it could not reach.</b> <c>OpenGate</c> used to drain the
/// deferred backlog onto the BACK of <c>mainQueue</c>; #3408 made it the FRONT, which restores the
/// order of everything still IN a queue. But deferral is decided at TURN time, so at the instant of
/// the drain there can be a delivery that is in NEITHER queue — dequeued, and somewhere between its
/// dequeue and its gate check. No rebuild of the two queues can reach it. It is younger than every
/// entry the drain just restored, and it runs first.</para>
///
/// <para><b>The two windows, and why both had to close.</b> The gate check has a lock-free fast
/// path (<c>gates.IsEmpty</c>) and takes <c>gateStateLock</c> only when that reads "not empty".
/// So a running turn escapes ordering in two distinct ways, with different fixes:</para>
/// <list type="number">
/// <item>the fast path reads "empty" while the backlog is still parked — closed by
/// <c>OpenGate</c> restoring BEFORE it removes the gate, so that state is never observable;</item>
/// <item>the fast path reads "not empty", the turn blocks on the lock, and by the time it gets in
/// the gate is open and the backlog restored — closed by the arrival-order barrier in
/// <c>NotifyAsync</c>, which re-queues the turn in place when older work heads the queue.</item>
/// </list>
///
/// <para><b>This test needs BOTH.</b> The opener is parked at the START of the restore — after the
/// gate removal in the old code, before it in the new — so with only the barrier the escaping turn
/// takes window 1 (nothing older is queued yet, so the barrier sees nothing and does not fire), and
/// with only the reordered <c>OpenGate</c> it takes window 2 (it blocks, then runs anyway). Either
/// half alone still prints <c>B, A</c>.</para>
///
/// <para><b>Why the interleaving is forced rather than hoped for.</b> Three structural facts do all
/// the work and no delay, poll-for-time or gate primitive is used: the turn loop is strictly FIFO;
/// a handler that Posts to its own hub enqueues behind its own turn; and <c>OpenGate</c> holds
/// <c>gateStateLock</c> across the whole restore. The opener runs on its own thread and is parked
/// INSIDE that critical section by the service's own <c>ILogger</c> — the same "decorate the real
/// dependency to control WHEN, never WHAT" technique <c>ActivationBacklogFifoTest</c> uses on
/// <c>IPathResolver</c>. Every wait is either an <c>AsyncSubject</c> the producer completes or a
/// re-query of the hub's public queue diagnostics.</para>
/// </summary>
public class GateOpenMustNotLetARunningTurnOvertakeTheBacklogTest : HubTestBase
{
    private const string GateName = "test-gate";

    /// <summary>
    /// The first line <c>MessageService.OpenGate</c> writes once it has decided to restore the
    /// deferred backlog, and BEFORE it touches <c>turnGate</c> — so parking here holds
    /// <c>gateStateLock</c> (which is the point) without holding the queue lock (which would wedge
    /// the drain instead of racing it).
    /// </summary>
    private const string RestoreMarker = "Draining deferred queue to the front of the main queue";

    /// <summary>Category of the logger that writes <see cref="RestoreMarker"/>.</summary>
    private const string MessageServiceCategory = "MeshWeaver.Messaging.MessageService";

    /// <summary>An ordinary message: does NOT pass the gate, so it defers.</summary>
    private record Tagged(string Tag);

    /// <summary>Passes the gate, so it can hold the turn loop while the gate is still closed.</summary>
    private record Blocker;

    /// <summary>Fires once the blocker's turn has begun AND B is provably queued behind it.</summary>
    private readonly AsyncSubject<Unit> windowOpen = new();

    /// <summary>Fires once the gate opener is parked inside the restore, holding the gate lock.</summary>
    private readonly AsyncSubject<Unit> openerInsideRestore = new();

    /// <summary>Fires once both tagged messages have been handled.</summary>
    private readonly AsyncSubject<Unit> allHandled = new();

    // Releases INTO workers the test deliberately parks: a volatile int polled under a bounded
    // SpinWait.SpinUntil, never a gate primitive, and always written in a finally so a failing
    // assertion cannot strand the thread that is holding a framework lock.
    private int releaseBlocker;
    private int releaseOpener;

    /// <summary>Armed just before the opener thread starts, so only THAT restore parks.</summary>
    private int parkArmed;

    /// <summary>One-shot: the park happens for the first armed restore only.</summary>
    private int parkTaken;

    /// <summary>Rendered address of the hub under test; other hubs' restores must not park.</summary>
    private volatile string hubAddress = "(no hub under test)";

    private ImmutableList<string> processed = ImmutableList<string>.Empty;

    /// <summary>
    /// Initializes the fixture and installs the logger seam that parks the gate opener.
    /// </summary>
    /// <param name="output">The xUnit output helper for the running test.</param>
    public GateOpenMustNotLetARunningTurnOvertakeTheBacklogTest(ITestOutputHelper output)
        : base(output)
    {
        // The restore line is Debug; the shared test appsettings filter would otherwise never
        // dispatch it to any provider. Scoped to the one category this test observes.
        Services.AddLogging(b => b.AddFilter(MessageServiceCategory, LogLevel.Debug));
        Services.AddSingleton<ILoggerProvider>(new RestoreParkLoggerProvider(this));
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithTypes(typeof(Tagged), typeof(Blocker))
            // Only the blocker may run while the gate is closed; everything else defers.
            .WithInitializationGate(GateName, d => d.Message is Blocker)
            .WithHandler<Tagged>((_, delivery) =>
            {
                // The turn loop is single-threaded, so this read-after-write is not a race.
                ImmutableInterlocked.Update(
                    ref processed, static (l, t) => l.Add(t), delivery.Message.Tag);
                if (processed.Count == 2)
                {
                    allHandled.OnNext(Unit.Default);
                    allHandled.OnCompleted();
                }
                return delivery.Processed();
            })
            .WithHandler<Blocker>((hub, delivery) =>
            {
                // Posting from inside our own turn enqueues behind it: B is now in mainQueue,
                // un-turned, while the gate is still closed and A is already deferred.
                hub.Post(new Tagged("B"), o => o.WithTarget(hub.Address));
                // 🚨 PROVE it landed rather than assuming the post was synchronous — the whole
                // script downstream reads "buffer back to 0" as "B was dequeued", which is only
                // a signal if B was provably in the queue first.
                SpinWait.SpinUntil(
                    () => hub.GetPendingRequestDiagnostics().Contains(
                        "Queue(buffer=1,", StringComparison.Ordinal),
                    TestTimeouts.Convergence);
                windowOpen.OnNext(Unit.Default);
                windowOpen.OnCompleted();
                // Park the turn loop here, on the DRAIN thread, until the test has the opener
                // inside the restore. A volatile int, not an await: awaiting would resume the
                // test's continuation on this drain thread and the script would then run inside
                // the very turn it is trying to sequence against.
                SpinWait.SpinUntil(() => Volatile.Read(ref releaseBlocker) == 1, TestTimeouts.Convergence);
                return delivery.Processed();
            });

    // 120_000 ms, not TestTimeouts.Convergence: an attribute argument must be a constant, and the
    // inner waits below already carry the adaptive bound. This outer one only has to stop a WEDGE.
    [Fact(Timeout = 120_000)]
    public async Task AMessageWhoseTurnSpansTheGateOpen_RunsAfterTheBacklogItArrivedBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        var host = GetHost();
        hubAddress = host.Address.ToString();

        // A is posted first, so the FIFO turn loop reaches it first and defers it (it does not
        // pass the gate). The blocker is posted second, so when its turn starts — which is what
        // windowOpen reports — A is provably already in the deferred queue.
        host.Post(new Tagged("A"), o => o.WithTarget(host.Address));
        host.Post(new Blocker(), o => o.WithTarget(host.Address));

        await Settle(windowOpen).Await(ct);
        Output.WriteLine("A deferred; B queued behind the running blocker; gate still closed.");

        var opener = new Thread(() => host.OpenGate(GateName))
        {
            IsBackground = true,
            Name = "gate-opener",
        };
        try
        {
            Volatile.Write(ref parkArmed, 1);
            opener.Start();

            // The opener is now inside OpenGate's critical section, at the restore, holding
            // gateStateLock. This is the window a loaded CI shard hits by accident.
            await Settle(openerInsideRestore).Await(ct);
            Output.WriteLine("Gate opener parked inside the restore, holding the gate lock.");

            // Let the blocker's turn finish. The drain then dequeues B and B's turn runs THROUGH
            // the open window: it either reads "no gates" on the fast path or blocks on the lock.
            Volatile.Write(ref releaseBlocker, 1);

            // buffer back to 0 == B has left mainQueue, i.e. its turn is in flight (or, on the
            // unfixed code, has already run its handler). A re-query, because neither outcome
            // shares a positive signal to wait on.
            await Observable.Interval(TimeSpan.FromMilliseconds(10)).StartWith(0L)
                .Select(_ => host.GetPendingRequestDiagnostics())
                .Where(d => d.Contains("Queue(buffer=0,", StringComparison.Ordinal))
                .FirstAsync()
                .Timeout(TestTimeouts.Convergence)
                .Await(ct);
            Output.WriteLine("B's turn is past its dequeue while the gate open is still in flight.");
        }
        finally
        {
            // Never strand a thread holding gateStateLock, whatever happened above.
            Volatile.Write(ref releaseBlocker, 1);
            Volatile.Write(ref releaseOpener, 1);
        }

        await Settle(allHandled).Await(ct);
        Output.WriteLine($"Processing order: {string.Join(", ", processed)}");

        processed.Should().Equal(new[] { "A", "B" },
            "A was posted first and parked behind the initialization gate, so it must be processed "
            + "before B — a turn that was already running when the gate opened is YOUNGER than "
            + "everything the gate drain just restored, and running it first is the residual "
            + "reorder that survived MeshWeaver#3408 (Plugins#1394, observed on CI as B,A,C and "
            + "C,A,B). Restoring the backlog before the gate is removed closes the fast-path "
            + "window; the arrival-order barrier in NotifyAsync closes the blocked-on-the-lock one");
    }

    /// <summary>
    /// Awaits a producer signal on a scheduler of our own.
    ///
    /// <para>🚨 An <c>await</c> of an observable resumes the continuation ON THE SIGNALLING THREAD.
    /// Every signal here is raised by a thread that is holding a framework lock or a drain turn and
    /// is about to park, so resuming the test script there would run the rest of the script inside
    /// the very critical section it is sequencing against — and, in one case, deadlock the test
    /// against its own opener.</para>
    /// </summary>
    private static IObservable<Unit> Settle(IObservable<Unit> signal)
        => signal.ObserveOn(TaskPoolScheduler.Default).Timeout(TestTimeouts.Convergence);

    /// <summary>
    /// Parks the calling thread inside <c>OpenGate</c>'s restore, once, for the hub under test.
    /// </summary>
    private void ParkTheGateOpener()
    {
        openerInsideRestore.OnNext(Unit.Default);
        openerInsideRestore.OnCompleted();
        SpinWait.SpinUntil(() => Volatile.Read(ref releaseOpener) == 1, TestTimeouts.Convergence);
    }

    private bool ShouldPark(string message)
        => Volatile.Read(ref parkArmed) == 1
           && message.Contains(RestoreMarker, StringComparison.Ordinal)
           && message.Contains(hubAddress, StringComparison.Ordinal)
           && Interlocked.Exchange(ref parkTaken, 1) == 0;

    private sealed class RestoreParkLoggerProvider(
        GateOpenMustNotLetARunningTurnOvertakeTheBacklogTest test) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
            => categoryName == MessageServiceCategory
                ? new RestoreParkLogger(test)
                : InertLogger.Instance;

        public void Dispose() { }
    }

    private sealed class RestoreParkLogger(
        GateOpenMustNotLetARunningTurnOvertakeTheBacklogTest test) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (test.ShouldPark(formatter(state, exception)))
                test.ParkTheGateOpener();
        }
    }

    private sealed class InertLogger : ILogger
    {
        public static readonly InertLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
        }
    }
}
