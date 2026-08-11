using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Deterministic (virtual-time) pin for the fresh-pod wedge fix
/// (<see cref="NodeTypeEnrichmentHelpers.WaitForCompileSettled"/>).
///
/// <para>The wedge: on a fresh pod every dynamic NodeType recompiles from source
/// at once. A user request that activates a per-instance hub while that NodeType is
/// still mid-compile used to hit a flat <c>SlowPathTimeout</c> wall-clock — it
/// expired mid-compile, cached the compilation-error overlay onto the instance hub
/// for its whole lifetime, and only a manual recycle could heal it.</para>
///
/// <para>The fix: a compile-in-progress is a WAIT, not a fault. Once a
/// <c>Pending</c>/<c>Compiling</c> state is observed the wall-clock is DISARMED and
/// the wait is bounded by the compile FINISHING (RunCompile always writes a terminal
/// Ok/Error). The wall-clock still bounds the genuine "no compile is coming" case so
/// a truly stuck/misconfigured type surfaces the diagnostic (the graceful sink).</para>
///
/// <para>Virtual time via <see cref="TestScheduler"/> — no <c>Task.Delay</c>, no real
/// Roslyn compile; the scheduler lets a compile "take" far longer than the budget in
/// zero wall-clock so the assertion is a pure state race, not a timing race.</para>
/// </summary>
public class CompileWaitDoesNotTimeoutTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    private static MeshNode Node(CompilationStatus? status, bool withAssembly = false,
        string? configuration = null)
        => new("DynamicType", "Test")
        {
            Content = new NodeTypeDefinition
            {
                CompilationStatus = status,
                Configuration = configuration,
                LatestAssemblyCollection = withAssembly ? "coll" : null,
                LatestAssemblyPath = withAssembly ? "path" : null,
            }
        };

    /// <summary>The BuildSlowPath terminal predicate, in miniature: Ok-with-assembly or Error.</summary>
    private static bool IsSettled(MeshNode n)
        => n.Content is NodeTypeDefinition d
            && ((d.CompilationStatus == CompilationStatus.Ok
                    && !string.IsNullOrEmpty(d.LatestAssemblyCollection)
                    && !string.IsNullOrEmpty(d.LatestAssemblyPath))
                || d.CompilationStatus == CompilationStatus.Error);

    private static IObservable<MeshNode> Settled(IObservable<MeshNode> typeStream)
        => typeStream.Where(IsSettled).Take(1);

    /// <summary>
    /// THE Part-2 contract: while a compile is in flight the wall-clock is disarmed —
    /// the wait outlasts many multiples of the no-progress budget and only resolves
    /// when the compile finally settles Ok. Before the fix the flat 60 s timeout fired
    /// and cached the overlay; here we advance 5× the budget with the type still
    /// Compiling and NOTHING resolves until the Ok write lands.
    /// </summary>
    [Fact]
    public void InFlightCompile_WaitsPastWallClock_ThenEmitsOnTerminalOk()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, Settled(typeStream), Budget,
                () => new TimeoutException("no compile in flight"), scheduler)
            .Subscribe(n => emitted = n, ex => error = ex);

        // A compile is genuinely in flight — this DISARMS the wall-clock.
        typeStream.OnNext(Node(CompilationStatus.Compiling));

        // Advance FIVE budgets of virtual time. A flat wall-clock would have fired at
        // one budget and cached the overlay; the disarmed timer must stay silent.
        scheduler.AdvanceBy(Budget.Ticks * 5);
        Assert.Null(error);
        Assert.Null(emitted);

        // The compile finally settles Ok — NOW the wait resolves.
        var ok = Node(CompilationStatus.Ok, withAssembly: true);
        typeStream.OnNext(ok);

        Assert.Null(error);
        Assert.Same(ok, emitted);
    }

    /// <summary>
    /// An in-flight compile that fails LATE still surfaces the Error deterministically
    /// (never a hang) so the caller can overlay the diagnostics — the
    /// GrainActivationFailureRegistry/error path stays intact.
    /// </summary>
    [Fact]
    public void InFlightCompile_ThatFailsLate_SurfacesError_NotHang()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, Settled(typeStream), Budget,
                () => new TimeoutException("no compile in flight"), scheduler)
            .Subscribe(n => emitted = n, ex => error = ex);

        typeStream.OnNext(Node(CompilationStatus.Pending));     // disarm
        scheduler.AdvanceBy(Budget.Ticks * 3);
        Assert.Null(error);
        Assert.Null(emitted);

        var failed = Node(CompilationStatus.Error);
        typeStream.OnNext(failed);

        Assert.Null(error);                     // the Error node is a VALUE, not a fault
        Assert.Same(failed, emitted);           // caller applies the compilation-error overlay
    }

    /// <summary>
    /// The graceful sink for the genuine "no compile is coming" case: a type stuck at
    /// a non-settled, non-in-flight state (source present but no compile ever kicked)
    /// still faults after the budget so the caller overlays a diagnostic — never an
    /// infinite silent hang.
    /// </summary>
    [Fact]
    public void NoCompileEverStarts_TimesOut_ForGracefulSink()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, Settled(typeStream), Budget,
                () => new TimeoutException("no compile in flight"), scheduler)
            .Subscribe(n => emitted = n, ex => error = ex);

        // Neither settled nor in-flight: sits waiting for a compile that never starts.
        typeStream.OnNext(Node(status: null, configuration: "config => config"));

        scheduler.AdvanceBy(Budget.Ticks + 1);

        Assert.Null(emitted);
        Assert.IsType<TimeoutException>(error);
    }

    /// <summary>
    /// 🚨 The wedge the disarm left behind: a compile that RUNS and lands a terminal state the
    /// caller's <c>settled</c> predicate REJECTS must re-arm the budget, never wait forever.
    ///
    /// <para>The framework-stale self-heal is exactly that caller: <c>IsRecompileSettled</c> with
    /// <c>requireUsableBuild</c> accepts ONLY a genuinely usable build, so a rebuild that ends at
    /// <c>Error</c> — a node repo's committed stale stamp whose source no longer compiles, the
    /// Store/Catalog shape — is not an answer. The disarm was a one-shot edge
    /// (<c>TakeUntil(compileInFlight.Take(1))</c>): once ONE Pending/Compiling emission was seen
    /// the wall clock was cancelled for good, so after that rejected terminal NOTHING was left to
    /// bound the wait. The per-instance hub's enrichment then never completed and never faulted —
    /// every message routed to that node parked until the SENDER's own timeout, and the node's hub
    /// was never created at all. Measured: a package install whose root is typed by such a NodeType
    /// stalled 60.3 s on its content publish (the mesh hub's RequestTimeout) and then dropped the
    /// package's binaries. A hang that only ends at someone else's timeout is the "wedges to zero"
    /// violation — the wait must sink gracefully so the caller can overlay the diagnostic.</para>
    ///
    /// <para>Contrast <see cref="InFlightCompile_ThatFailsLate_SurfacesError_NotHang"/>: there the
    /// predicate ACCEPTS Error, so the same sequence resolves as a value. The distinguishing input
    /// is the predicate, which is why the disarm's one-shot shape hid this for so long.</para>
    /// </summary>
    [Fact]
    public void InFlightCompile_LandingATerminalTheWaitRejects_ReArmsTheBudget()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        // The framework-stale heal's rule: ONLY a usable build settles. Error does not.
        var settledUsableOnly = typeStream
            .Where(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestAssemblyPath))
            .Take(1);

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, settledUsableOnly, Budget,
                () => new TimeoutException("no compile in flight"), scheduler)
            .Subscribe(n => emitted = n, ex => error = ex);

        typeStream.OnNext(Node(CompilationStatus.Compiling));   // disarms the wall clock
        scheduler.AdvanceBy(Budget.Ticks * 3);
        Assert.Null(error);
        Assert.Null(emitted);

        // The compile FINISHES — at a terminal state this caller does not accept. No further
        // compile is in flight, so nothing else is coming: the budget must run again.
        typeStream.OnNext(Node(CompilationStatus.Error));
        Assert.Null(error);                                     // budget re-armed, not yet elapsed

        scheduler.AdvanceBy(Budget.Ticks + 1);

        Assert.Null(emitted);
        Assert.IsType<TimeoutException>(error);
    }

    /// <summary>
    /// The re-arm must not shorten a LEGITIMATE second compile: a rejected terminal followed by a
    /// fresh Pending disarms the clock again, and the eventual usable build still resolves the
    /// wait however long it takes. Otherwise the fix above would trade a hang for the fresh-pod
    /// mid-compile fault it exists to prevent.
    /// </summary>
    [Fact]
    public void RejectedTerminal_ThenASecondCompile_DisarmsAgain_AndResolvesOnTheUsableBuild()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        var settledUsableOnly = typeStream
            .Where(n => n.Content is NodeTypeDefinition d
                && d.CompilationStatus == CompilationStatus.Ok
                && !string.IsNullOrEmpty(d.LatestAssemblyPath))
            .Take(1);

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, settledUsableOnly, Budget,
                () => new TimeoutException("no compile in flight"), scheduler)
            .Subscribe(n => emitted = n, ex => error = ex);

        typeStream.OnNext(Node(CompilationStatus.Compiling));
        scheduler.AdvanceBy(Budget.Ticks * 2);
        typeStream.OnNext(Node(CompilationStatus.Error));       // rejected → budget re-arms

        // A second compile starts well inside the re-armed budget — disarm again.
        scheduler.AdvanceBy(Budget.Ticks / 2);
        typeStream.OnNext(Node(CompilationStatus.Pending));
        scheduler.AdvanceBy(Budget.Ticks * 5);
        Assert.Null(error);
        Assert.Null(emitted);

        var ok = Node(CompilationStatus.Ok, withAssembly: true);
        typeStream.OnNext(ok);

        Assert.Null(error);
        Assert.Same(ok, emitted);
    }

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The compilation-in-progress overlay's trigger: a compile CONTINUOUSLY in flight for
    /// the grace emits the (unsettled) in-flight node as a VALUE — the caller's cue to stop
    /// holding the activation and serve the live progress overlay instead. The discriminator
    /// is the node's own Pending/Compiling status (every settle predicate rejects those).
    /// </summary>
    [Fact]
    public void InFlightPastGrace_EmitsTheInFlightNode_ForTheProgressOverlay()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, Settled(typeStream), Budget,
                () => new TimeoutException("no compile in flight"), scheduler,
                inFlightGrace: Grace)
            .Subscribe(n => emitted = n, ex => error = ex);

        var compiling = Node(CompilationStatus.Compiling);
        typeStream.OnNext(compiling);

        // Inside the grace: still waiting silently (a short compile stays invisible).
        scheduler.AdvanceBy(Grace.Ticks - 1);
        Assert.Null(emitted);
        Assert.Null(error);

        scheduler.AdvanceBy(2);
        Assert.Null(error);
        Assert.Same(compiling, emitted);        // the caller branches on Pending/Compiling
    }

    /// <summary>
    /// A compile that settles INSIDE the grace behaves exactly as before the grace existed:
    /// the settled node wins, no overlay emission, no flicker for short compiles.
    /// </summary>
    [Fact]
    public void InFlightSettlingWithinGrace_EmitsSettled_NeverTheOverlayCue()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, Settled(typeStream), Budget,
                () => new TimeoutException("no compile in flight"), scheduler,
                inFlightGrace: Grace)
            .Subscribe(n => emitted = n, ex => error = ex);

        typeStream.OnNext(Node(CompilationStatus.Compiling));
        scheduler.AdvanceBy(Grace.Ticks / 2);

        var ok = Node(CompilationStatus.Ok, withAssembly: true);
        typeStream.OnNext(ok);
        Assert.Same(ok, emitted);
        Assert.Null(error);

        // Long past the grace nothing else fires — Take(1) unsubscribed the grace timer.
        scheduler.AdvanceBy(Grace.Ticks * 3);
        Assert.Same(ok, emitted);
        Assert.Null(error);
    }

    /// <summary>
    /// Leaving Pending/Compiling BEFORE the grace elapses cancels the grace timer (the
    /// level/Switch shape — same as the budget disarm), and the no-progress budget re-arms:
    /// an unsettled non-in-flight terminal still ends at the graceful timeout sink, never
    /// at a stale overlay cue.
    /// </summary>
    [Fact]
    public void InFlightLeavingBeforeGrace_CancelsTheGrace_AndReArmsTheBudget()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        // Settle predicate that rejects everything — isolates the timers.
        var neverSettles = typeStream.Where(_ => false).Take(1);

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, neverSettles, Budget,
                () => new TimeoutException("no compile in flight"), scheduler,
                inFlightGrace: Grace)
            .Subscribe(n => emitted = n, ex => error = ex);

        typeStream.OnNext(Node(CompilationStatus.Compiling));
        scheduler.AdvanceBy(Grace.Ticks / 2);

        // The compile leaves in-flight without settling (a state this caller rejects).
        typeStream.OnNext(Node(status: null, configuration: "config => config"));

        // The grace must NOT fire — the compile is no longer in flight.
        scheduler.AdvanceBy(Grace.Ticks);
        Assert.Null(emitted);
        Assert.Null(error);

        // …and the re-armed budget ends the wait at the graceful sink.
        scheduler.AdvanceBy(Budget.Ticks + 1);
        Assert.Null(emitted);
        Assert.IsType<TimeoutException>(error);
    }

    /// <summary>
    /// A NodeType that is already settled Ok at first observation resolves immediately
    /// — the wall-clock never even starts to matter.
    /// </summary>
    [Fact]
    public void AlreadySettled_EmitsImmediately_NoTimeout()
    {
        var scheduler = new TestScheduler();
        var typeStream = new Subject<MeshNode>();

        MeshNode? emitted = null;
        Exception? error = null;
        NodeTypeEnrichmentHelpers
            .WaitForCompileSettled(typeStream, Settled(typeStream), Budget,
                () => new TimeoutException("no compile in flight"), scheduler)
            .Subscribe(n => emitted = n, ex => error = ex);

        var ok = Node(CompilationStatus.Ok, withAssembly: true);
        typeStream.OnNext(ok);

        Assert.Same(ok, emitted);
        Assert.Null(error);

        // Even long past the budget nothing else fires (the timer was unsubscribed).
        scheduler.AdvanceBy(Budget.Ticks * 5);
        Assert.Null(error);
    }
}
