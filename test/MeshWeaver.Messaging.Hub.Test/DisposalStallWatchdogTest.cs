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
/// The disposal STALL DETECTOR — what it reports, what it does, and what it never does.
///
/// <para><b>#1701 — an owner must not out-run the mechanism that answers it.</b> The detector is
/// armed per hub at that hub's own <c>Dispose()</c>. A child's <c>Dispose()</c> is NOT called at the
/// parent's t=0 — it is called in the parent's <c>DisposeHostedHubs</c> phase, i.e. strictly AFTER
/// the parent's quiesce budget. So a fixed-DURATION watchdog on the parent always expires first and
/// reports a subtree that is merely still working. The detector therefore measures a STALL: it
/// re-arms on every disposal-progress signal from anywhere in the subtree (each hub's
/// <c>RunLevel</c> transitions, recursively — never a heartbeat, so quiet means genuinely stopped).</para>
///
/// <para><b>It forces nothing.</b> Its predecessor ran an out-of-band teardown from the watchdog
/// thread while the wedged turn was still executing and then signalled Dead — a hub that reported
/// itself disposed with its children mid-flight, and in production a pod whose "completed" teardown
/// was still holding work. Now a stall has five verdicts (<c>MessageHub.OnDisposalStall</c>), none of
/// which tears anything down: a pump that keeps completing turns is BUSY (Information, keep waiting);
/// a turn holding the block with no progress is CANCELLED once, cooperatively, and reported at Error
/// by name; a turn that ignores that is reported again, every budget, as the defect it is; a
/// ShutDown phase that is itself blocked names the registrant; and a stall with no turn executing is
/// in a child or a join the diagnostics name — while disposal stays honestly pending until the work
/// returns. The bound that ENDS a wedged teardown is the caller's.</para>
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

    /// <summary>A turn that parks the action block until the test releases it.</summary>
    private record WedgeEvent;

    /// <summary>Per level: long enough that the whole chain outlasts the 8 s stall window, short
    /// enough that no single gap between progress signals reaches it.</summary>
    private static readonly TimeSpan PerLevelQuiesce = TimeSpan.FromSeconds(3);
    private const int ChainDepth = 4;   // 4 × 3 s ≈ 12 s > 8 s, in 3 s steps

    /// <summary>
    /// A nested teardown that legitimately takes LONGER than the stall window, while every
    /// individual step stays well inside it. Four hosted hubs, each holding one unanswered callback
    /// so its Quiescing phase runs its full 3 s budget: the chain needs ~12 s end to end, in 3 s
    /// steps, and nothing is wedged at any point.
    ///
    /// <para><b>Non-vacuity.</b> A fixed 8 s timer on the root expires at t=8 — four seconds before
    /// the subtree it is waiting on could possibly have finished — and reports
    /// <c>DISPOSAL DEADLOCK DETECTED</c> about a healthy teardown.</para>
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

        elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(9),
            "the chain must genuinely take longer than the 8 s stall window, or this test is vacuous "
            + $"(actual {elapsed.TotalSeconds:F1}s over {ChainDepth} levels of {PerLevelQuiesce.TotalSeconds}s)");

        foreach (var entry in capture.Entries)
            Output.WriteLine(entry);
        capture.Entries.Should().BeEmpty(
            "nothing was wedged — every level advanced through its phases within 3 s of the last. "
            + "A detector that fires here is measuring DURATION, and it out-runs the very mechanism "
            + "that answers it: a child's detector is armed one quiesce budget later than its "
            + $"parent's, for the same 8 s. Elapsed {elapsed.TotalSeconds:F1}s.");
    }

    /// <summary>
    /// A turn that holds the block but OBSERVES its cancellation token. After one stall budget the
    /// detector reports it at Error — naming the message and the hub — and hands it the cancel; the
    /// handler returns, and disposal completes through the ordinary phases. Nothing is forced.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ACooperativeWedgedTurn_IsReportedAndCancelled_ThenDisposalCompletes()
    {
        var entered = new AsyncSubject<Unit>();
        var release = 0;
        var cancellationObserved = 0;
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "stall-cooperative"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<WedgeEvent>((_, d, ct) =>
            {
                entered.OnNext(Unit.Default);
                entered.OnCompleted();
                SpinWait.SpinUntil(
                    () => Volatile.Read(ref release) == 1 || ct.IsCancellationRequested,
                    TimeSpan.FromSeconds(60));
                if (ct.IsCancellationRequested)
                    Interlocked.Exchange(ref cancellationObserved, 1);
                return Task.FromResult(d.Processed());
            }), HostedHubCreation.Always)!;

        try
        {
            victim.Post(new WedgeEvent(), o => o.WithTarget(victim.Address));
            await entered.Should().Within(10.Seconds()).Emit("the turn must hold the block before we dispose");

            victim.Dispose();

            var verdict = await FirstVerdict(TimeSpan.FromSeconds(25));
            Output.WriteLine(verdict);
            verdict.Should().Contain("[DISPOSE-WEDGE]",
                "the first verdict on a turn holding the block is the cooperative cancel, reported at Error");
            verdict.Should().Contain(nameof(WedgeEvent),
                "the report must NAME the message occupying the action block (#1701's diagnostic half)");
            verdict.Should().Contain("Queue(buffer=",
                "the report carries the recursive disposal snapshot — queue depths, the executing turn, "
                + "pending callbacks — so a reader can reproduce it from the log alone");

            await victim.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(TestTimeouts.Convergence);
            Volatile.Read(ref cancellationObserved).Should().Be(1,
                "the turn returned because it observed the cancellation the detector handed it");
            victim.RunLevel.Should().Be(MessageHubRunLevel.Dead);
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// A turn that ignores its cancellation. The detector cancels once (Error), then reports the
    /// turn again a budget later as one that ignores cancellation (Error, <c>DISPOSAL DEADLOCK
    /// DETECTED</c>) — and disposal stays PENDING the whole time: nothing is torn down around a
    /// running turn, and <c>DisposalCompleted</c> is not signalled until the turn actually returns.
    /// Releasing the turn then completes disposal through the ordinary phases, children and
    /// disposables included.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ATurnThatIgnoresCancellation_IsReportedEveryBudget_AndDisposalWaitsForIt()
    {
        var entered = new AsyncSubject<Unit>();
        var childDisposed = new AsyncSubject<Unit>();
        var ownDisposed = new AsyncSubject<Unit>();
        var release = 0;
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "stall-uncooperative"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<WedgeEvent>((_, d) =>
            {
                entered.OnNext(Unit.Default);
                entered.OnCompleted();
                // Deliberately blind to cancellation: the shape of a handler that blocks on
                // something it never checks.
                SpinWait.SpinUntil(() => Volatile.Read(ref release) == 1, TimeSpan.FromSeconds(90));
                return d.Processed();
            }), HostedHubCreation.Always)!;
        victim.RegisterForDisposal(System.Reactive.Disposables.Disposable.Create(() =>
        {
            ownDisposed.OnNext(Unit.Default);
            ownDisposed.OnCompleted();
        }));
        var child = victim.GetHostedHub(new Address("child", "1"),
            cc => cc.WithPostingIdentity(PostingIdentity.System), HostedHubCreation.Always)!;
        child.RegisterForDisposal(System.Reactive.Disposables.Disposable.Create(() =>
        {
            childDisposed.OnNext(Unit.Default);
            childDisposed.OnCompleted();
        }));

        try
        {
            victim.Post(new WedgeEvent(), o => o.WithTarget(victim.Address));
            await entered.Should().Within(10.Seconds()).Emit("the turn must hold the block before we dispose");

            var completed = victim.DisposalCompleted.FirstOrDefaultAsync().Await();
            victim.Dispose();

            // Two budgets of no progress: the cancel verdict, then the ignores-cancellation verdict.
            var second = await NthVerdict(2, TimeSpan.FromSeconds(40));
            foreach (var entry in capture.Entries)
                Output.WriteLine(entry);
            capture.Entries.First().Should().Contain("[DISPOSE-WEDGE]");
            second.Should().Contain("DISPOSAL DEADLOCK DETECTED",
                "a turn that ignores the cancel is reported again, every budget, as the defect it is");
            second.Should().Contain("ignores cancellation");
            second.Should().Contain(nameof(WedgeEvent));

            // Sanctioned negative wait: the finding IS that disposal has NOT completed — nothing was
            // torn down around the running turn, so there is no positive signal to filter for.
            var winner = await Task.WhenAny(completed, Task.Delay(TimeSpan.FromSeconds(2)));
            winner.Should().NotBe(completed,
                "disposal must stay PENDING while the turn runs: a hub that has not finished must not "
                + "say it has — the predecessor force-tore the subtree down here and signalled Dead");
            ownDisposed.IsCompleted.Should().BeFalse("nothing is torn down around a running turn");
            childDisposed.IsCompleted.Should().BeFalse("nothing is torn down around a running turn");

            // The work finishes; the ordinary phases run to the end.
            Volatile.Write(ref release, 1);
            await completed.WaitAsync(TestTimeouts.Convergence);
            await ownDisposed.Should().Within(5.Seconds()).Emit("ShutDown disposes the hub's own registrations");
            await childDisposed.Should().Within(5.Seconds()).Emit("DisposeHostedHubs disposes the children");
            victim.RunLevel.Should().Be(MessageHubRunLevel.Dead);
        }
        finally
        {
            Volatile.Write(ref release, 1);
        }
    }

    /// <summary>
    /// A backlog of accepted work ahead of the ShutdownRequest is not a wedge: turns keep
    /// completing, the detector reads the pump as busy, reports nothing at Error, and the
    /// shutdown proceeds once the backlog is through. Pinned here beside the wedge cases because
    /// the two produce the SAME RunLevel and the same queue depths — only the completed-turn count
    /// tells them apart, and a detector that cannot tell them apart force-tore the busy case down.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ABacklogOfAcceptedWork_IsBusyNotWedged_AndDrains()
    {
        var turns = 0;
        var victim = (MessageHub)Mesh.GetHostedHub(new Address("victim", "stall-busy"), c => c
            .WithPostingIdentity(PostingIdentity.System)
            .WithHandler<WedgeEvent>((_, d) =>
            {
                Interlocked.Increment(ref turns);
                Thread.Sleep(15);
                return d.Processed();
            }), HostedHubCreation.Always)!;
        for (var i = 0; i < 800; i++)
            victim.Post(new WedgeEvent(), o => o.WithTarget(victim.Address));
        await Task.Delay(100, TestContext.Current.CancellationToken);

        var started = DateTime.UtcNow;
        victim.Dispose();
        await victim.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(60.Seconds());
        var elapsed = DateTime.UtcNow - started;

        elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(7),
            $"the backlog must outlast the stall window so the detector provably looked at this hub "
            + $"(turns processed: {Volatile.Read(ref turns)})");
        Volatile.Read(ref turns).Should().Be(800, "every accepted turn ran before the shutdown");
        foreach (var entry in capture.Entries)
            Output.WriteLine(entry);
        capture.Entries.Should().BeEmpty(
            "a pump that keeps completing turns is BUSY, not wedged — nothing to report at Error");
    }

    private async Task<string> FirstVerdict(TimeSpan within) => await NthVerdict(1, within);

    private async Task<string> NthVerdict(int n, TimeSpan within)
    {
        var verdict = await Observable.Interval(TimeSpan.FromMilliseconds(100)).StartWith(0L)
            .Where(_ => capture.Entries.Count >= n)
            .Select(_ => capture.Entries.ElementAt(n - 1))
            .FirstAsync()
            .Timeout(within)
            .Await(TestContext.Current.CancellationToken);
        return verdict;
    }

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
                      .WithHandler<NeverAnswered>((_, d) => d.Processed()),
                HostedHubCreation.Always)!;
            hub.Observe(new NeverAnswered(), o => o.WithTarget(hub.Address)).Subscribe(_ => { }, _ => { });
            chain[level] = hub;
            parent = hub;
        }
        return chain;
    }

    /// <summary>Captures every Error-level stall verdict — both the cancel and the deadlock shapes.</summary>
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
