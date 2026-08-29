using System;
using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// <see cref="HubDisposalJoin"/>, pinned. The type exists because a harness that disposes a hub and
/// PROCEEDS runs the rest of its teardown concurrently with the hub's — and then disposes the scope,
/// unloads a node ALC, or hands the mesh to the next test. That is a use-after-dispose, and it kills
/// the process (SIGSEGV / exit 139) rather than failing a test.
///
/// <para>Three outcomes have to stay DISTINGUISHABLE, because a harness that cannot tell them apart
/// is exactly what the old <c>catch { /* best-effort */ }</c> sites were: joined, faulted, and
/// budget-expired. Each is asserted here, plus the two ways this could quietly become useless — a
/// reporter that throws taking teardown down with it, and a late fault with nowhere to go.</para>
///
/// <para>These tests drive a bare <see cref="Subject{T}"/> rather than a real hub, for the same
/// reason <c>DisposalWaitBridgeTest</c> gives: it makes the timing and the terminating notification
/// controllable, and a real hub cannot be made to produce a disposal that never completes — which is
/// the case the BUDGET exists for.</para>
/// </summary>
public class HubDisposalJoinTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    // ── joined ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Async_ReturnsOnlyAfterTheSignalTerminates()
    {
        var signal = new Subject<Unit>();
        var reports = new ConcurrentQueue<string>();

        var join = signal.JoinDisposalAsync("hub/1", reports.Enqueue, TimeSpan.FromSeconds(10));
        Assert.False(join.IsCompleted, "the join settled before the signal did — it is not joining anything");

        signal.OnNext(Unit.Default);
        signal.OnCompleted();

        Assert.True(await join);
        Assert.Empty(reports);
    }

    [Fact]
    public async Task Sync_BlocksUntilTheSignalTerminates()
    {
        var signal = new Subject<Unit>();
        var reports = new ConcurrentQueue<string>();

        // The blocking form is driven from ANOTHER thread and the signal is terminated from this
        // one, so "it waited" is observable rather than assumed.
        var joiner = Task.Run(() =>
            signal.JoinDisposal("hub/1", reports.Enqueue, TimeSpan.FromSeconds(10)));

        // A Subject is cold until subscribed, so publish the terminal notification only once the
        // joiner is actually listening — otherwise it goes into the void and the test measures the
        // budget instead of the join.
        Assert.True(SpinWait.SpinUntil(() => signal.HasObservers, TimeSpan.FromSeconds(5)),
            "the synchronous join never subscribed to the signal");
        Assert.False(joiner.IsCompleted, "the join returned before the signal terminated");

        signal.OnCompleted();

        Assert.True(await joiner.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(reports);
    }

    /// <summary>
    /// A signal that has ALREADY terminated joins immediately. This is the everyday case for a hub:
    /// <c>MessageHub.disposalCompleted</c> is a <c>ReplaySubject(1)</c>, so a hub that finished
    /// disposing before the join was written replays its completion rather than hanging for the
    /// whole budget.
    /// </summary>
    [Fact]
    public async Task AnAlreadyCompletedSignalJoinsImmediately()
    {
        var replay = new ReplaySubject<Unit>(1);
        replay.OnNext(Unit.Default);
        replay.OnCompleted();

        Assert.True(await replay.JoinDisposalAsync("hub/1", _ => { }, Budget));
        Assert.True(replay.JoinDisposal("hub/1", _ => { }, Budget));
    }

    // ── budget expired ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 The case the whole design turns on. A disposal that never finishes must NOT hang the
    /// harness (a silent hang is not an improvement over a crash) and must NOT pass silently either.
    /// It returns false, and the report NAMES the failure and refuses to recommend a bigger budget.
    /// </summary>
    [Fact]
    public async Task Async_TimesOutLoudlyRatherThanHanging()
    {
        var neverCompletes = new Subject<Unit>();
        var reports = new ConcurrentQueue<string>();

        Assert.False(await neverCompletes.JoinDisposalAsync(
            "hub/wedged", reports.Enqueue, Budget, () => "pending: SubscribeRequest 42s"));

        var report = Assert.Single(reports);
        Assert.Contains("hub/wedged", report, StringComparison.Ordinal);
        Assert.Contains("did NOT complete", report, StringComparison.Ordinal);
        Assert.Contains("do not widen the budget", report, StringComparison.Ordinal);
        Assert.Contains("pending: SubscribeRequest 42s", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Sync_TimesOutLoudlyRatherThanHanging()
    {
        var neverCompletes = new Subject<Unit>();
        var reports = new ConcurrentQueue<string>();

        Assert.False(neverCompletes.JoinDisposal("hub/wedged", reports.Enqueue, Budget));

        var report = Assert.Single(reports);
        Assert.Contains("did NOT complete", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// Diagnostics are a courtesy, never a hazard: a snapshot taken from a half-disposed hub can
    /// throw, and reporting must not be the thing that fails a teardown.
    /// </summary>
    [Fact]
    public async Task AThrowingDiagnosticsSnapshotIsAbsorbedIntoTheReport()
    {
        var reports = new ConcurrentQueue<string>();

        Assert.False(await new Subject<Unit>().JoinDisposalAsync(
            "hub/1", reports.Enqueue, Budget,
            () => throw new ObjectDisposedException("scope")));

        Assert.Contains("diagnostics unavailable: ObjectDisposedException",
            Assert.Single(reports), StringComparison.Ordinal);
    }

    // ── faulted ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A disposal FAULT is an answer, not a silence. The old shape at these sites — a bare
    /// <c>catch { }</c>, or a <c>.Catch(...)</c> spliced into the signal — rendered it as success.
    /// </summary>
    [Fact]
    public async Task Async_ReportsAFaultedDisposalAndDoesNotThrow()
    {
        var signal = new Subject<Unit>();
        var reports = new ConcurrentQueue<string>();

        var join = signal.JoinDisposalAsync("hub/1", reports.Enqueue, TimeSpan.FromSeconds(10));
        signal.OnError(new InvalidOperationException("action block faulted"));

        Assert.False(await join);
        var report = Assert.Single(reports);
        Assert.Contains("FAULTED", report, StringComparison.Ordinal);
        Assert.Contains("action block faulted", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Sync_ReportsAFaultedDisposalAndDoesNotThrow()
    {
        var replay = new ReplaySubject<Unit>(1);
        replay.OnError(new InvalidOperationException("action block faulted"));

        var reports = new ConcurrentQueue<string>();
        Assert.False(replay.JoinDisposal("hub/1", reports.Enqueue, Budget));

        var report = Assert.Single(reports);
        Assert.Contains("FAULTED", report, StringComparison.Ordinal);
        Assert.Contains("action block faulted", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The half a <see cref="Task"/> cannot represent: a fault that lands AFTER the wait gave up.
    /// Unobserved, it surfaces on the finalizer as an <c>UnobservedTaskException</c>, which xUnit v3
    /// escalates to a Catastrophic failure that poisons the NEXT test class (#2301). The
    /// subscription therefore outlives the wait, and the fault is reported instead.
    /// </summary>
    [Fact]
    public async Task ALateFaultIsStillReported()
    {
        var neverCompletes = new Subject<Unit>();
        var reports = new ConcurrentQueue<string>();

        Assert.False(await neverCompletes.JoinDisposalAsync("hub/1", reports.Enqueue, Budget));
        Assert.Single(reports); // the timeout

        neverCompletes.OnError(new InvalidOperationException("arrived after the budget"));

        Assert.Equal(2, reports.Count);
        Assert.Contains(reports, r =>
            r.Contains("faulted AFTER the join stopped waiting", StringComparison.Ordinal)
            && r.Contains("arrived after the budget", StringComparison.Ordinal));
    }

    // ── the reporter itself ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A reporter that throws must not become the failure. These sinks are loggers and xUnit output
    /// helpers, and BOTH throw once their scope is gone — an <c>ITestOutputHelper</c> after the test
    /// has been reported, an <c>ILogger</c> whose provider went down with the service scope. Letting
    /// that propagate would raise an exception on whatever thread signalled disposal, which is the
    /// failure this type exists to prevent.
    /// </summary>
    [Fact]
    public async Task AThrowingReportSinkNeverFailsTheTeardown()
    {
        static void Explode(string _) => throw new ObjectDisposedException("ITestOutputHelper");

        Assert.False(await new Subject<Unit>().JoinDisposalAsync("hub/1", Explode, Budget));
        Assert.False(new Subject<Unit>().JoinDisposal("hub/1", Explode, Budget));
    }

    // ── the hub-facing overloads ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A null hub is a no-op in both forms — a teardown that has nothing to dispose must not have to
    /// guard the call site, because a guard is where the join gets dropped.
    /// </summary>
    [Fact]
    public async Task ANullHubIsANoOp()
    {
        await ((IMessageHub?)null).DisposeAndJoinAsync(_ => Assert.Fail("nothing to report"));
        Assert.True(((IMessageHub?)null).DisposeAndJoin(_ => Assert.Fail("nothing to report")));
    }

    [Fact]
    public void ANullReportSinkIsRefusedRatherThanDefaultedToSilence()
    {
        // The one argument that must never be optional: a join whose outcome goes nowhere is the
        // `catch { }` these call sites are being moved OFF.
        Assert.Throws<ArgumentNullException>(
            () => new Subject<Unit>().JoinDisposal("hub/1", null!, Budget));
        Assert.Throws<ArgumentNullException>(
            () => ((IMessageHub?)null).DisposeAndJoin(null!));
    }
}
