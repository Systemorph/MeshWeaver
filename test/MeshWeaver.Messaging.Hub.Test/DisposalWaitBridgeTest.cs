using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The root cause of issue #2301, pinned as tests. Two properties, in the order they matter:
///
/// <list type="number">
/// <item><b>THE DEADLOCK (primary).</b> Rx's <c>ToTask()</c> completes its
///   <c>TaskCompletionSource</c> from inside the pipeline WITHOUT
///   <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so <c>await</c>ing it
///   resumes the caller <b>INLINE on the thread that signalled</b>. On the mesh that thread is a
///   hub's single-threaded action block or a grain's turn scheduler — the very scheduler the
///   caller's NEXT wait needs in order to finish. It is sticky, too: with no
///   <see cref="SynchronizationContext"/>, <c>await</c> captures
///   <see cref="TaskScheduler"/>.<see cref="TaskScheduler.Current"/>, so one inline resumption
///   routes every later await in the method onto that scheduler as well. #2301 failed at exactly
///   30 s — its <c>Timeout</c> budget — every time, while a healthy activation leaves the catalog
///   in 0.10 s. A number that is always the budget rather than a distribution around it is a
///   deadlock, not contention.</item>
/// <item><b>THE LOST FAULT (secondary).</b> When that timeout finally fires it settles the Task,
///   and a fault still travelling the chain has no observer left — an unobserved exception,
///   surfaced on the finalizer, escalated by xUnit v3 into a Catastrophic failure that poisons the
///   NEXT test class. That is the <c>HOST_CRASHED</c> marker; it is what the deadlock does on its
///   way out.</item>
/// </list>
///
/// <para>#2301 recurred four times because <c>IMessageHub.DisposalCompleted</c>'s own
/// documentation recommended the broken bridge — <i>"at a genuine async edge (test teardown, grain
/// deactivation) bridge once with <c>DisposalCompleted.FirstOrDefaultAsync()</c> /
/// <c>.ToTask()</c>"</i> — so every attempted fix aimed at the timeout instead. That sentence is
/// gone.</para>
///
/// <para>These tests use a bare <see cref="Subject{T}"/> rather than a real hub ON PURPOSE. It
/// makes "which thread signalled" observable with no timing in the test at all, and
/// <c>MessageHub.disposalCompleted</c> is CAS-guarded so one hub can never produce the
/// emit-then-fault shape property 2 needs (that shape belongs to COMPOSED signals: a merge over a
/// subtree of hubs, a drain chain, or #2301's poll whose per-tick calls were still in flight when
/// the outer wait settled).</para>
/// </summary>
public class DisposalWaitBridgeTest
{
    /// <summary>A cross-thread flag: a captured local cannot be read with <c>Volatile.Read</c>.</summary>
    private sealed class Flag
    {
        public volatile bool Value;
    }

    // ── 1. THE DEADLOCK ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE PRIMARY PROOF. <c>ObserveCompletion</c> never resumes its caller on the thread that
    /// signalled, so a waiter can never end up holding the scheduler whose progress it is waiting
    /// for. Deterministic: with <c>RunContinuationsAsynchronously</c> the continuation is queued,
    /// so it cannot possibly run on the signalling thread while that thread is still inside
    /// <c>OnNext</c>.
    /// </summary>
    [Fact]
    public async Task ObserveCompletion_NeverResumesItsCallerOnTheSignallingThread()
    {
        var signal = new Subject<Unit>();
        // 🚨 RECORD, never assert, inside a late-fault reporter. ObserveCompletion deliberately
        // guards the reporter (a logger whose provider died with the scope throws), so an
        // Assert.Fail here would be caught and routed to Trace — the test would pass while a late
        // fault went unnoticed. Capture it and assert on the TEST thread instead. (Copilot review,
        // #2538 — and it is the same "a verification step that cannot fail is not a verification
        // step" trap AGENTS.md names.)
        Exception? lateFault = null;
        var wait = signal.ObserveCompletion(ex => lateFault = ex);

        var signallingThread = Environment.CurrentManagedThreadId;
        var insideOnNext = new Flag();
        var resumedInline = new Flag();

        var probe = wait.ContinueWith(
            _ => resumedInline.Value =
                insideOnNext.Value && Environment.CurrentManagedThreadId == signallingThread,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        insideOnNext.Value = true;
        signal.OnNext(Unit.Default);
        insideOnNext.Value = false;

        await probe;

        resumedInline.Value.Should().BeFalse(
            "RunContinuationsAsynchronously is what keeps the waiter off the hub/grain scheduler " +
            "that signalled — resuming there is how #2301 held the very scheduler it was then " +
            "waiting on");
        lateFault.Should().BeNull("no fault is produced in this test — if one appeared, the " +
            "measurement above was of something else");
    }

    /// <summary>
    /// THE CONTROL, and the shape the interface documentation used to prescribe: <c>.ToTask()</c>
    /// resumes its caller INLINE, on the signalling thread, even with an
    /// <c>ExecuteSynchronously</c> continuation asked to run on <see cref="TaskScheduler.Default"/>.
    /// On the mesh that thread is the hub's action block or the grain's turn — and everything the
    /// caller does next runs there.
    /// </summary>
    [Fact]
    public async Task TheOldToTaskBridge_ResumesItsCallerInlineOnTheSignallingThread()
    {
        // 🚨 DELIBERATE, and the ONLY .ToTask() left in the repository: this is the NEGATIVE
        // CONTROL for the ban. It asserts that Rx's own bridge really does resume its caller
        // inline on the signalling thread — the defect the ruling exists to prevent. Converting
        // it to the safe wrapper inverts what it proves, so ObservableToTaskBridgeGuard allow-lists
        // exactly this file and this method.
        var signal = new Subject<Unit>();
        var wait = signal.FirstOrDefaultAsync().ToTask();

        var signallingThread = Environment.CurrentManagedThreadId;
        var insideOnNext = new Flag();
        var resumedInline = new Flag();

        var probe = wait.ContinueWith(
            _ => resumedInline.Value =
                insideOnNext.Value && Environment.CurrentManagedThreadId == signallingThread,
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        insideOnNext.Value = true;
        signal.OnNext(Unit.Default);
        insideOnNext.Value = false;

        await probe;

        resumedInline.Value.Should().BeTrue(
            "this is the defect, asserted rather than described: Rx's ToTask() creates its " +
            "TaskCompletionSource WITHOUT RunContinuationsAsynchronously, so the awaiter runs on " +
            "whatever thread produced the completion");
    }

    // ── 2. THE LOST FAULT ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The signal emits its interesting value, the waiter settles on it, and THEN the signal
    /// faults. With <c>ObserveCompletion</c> the error arm is still attached, so the late fault is
    /// reported — it never becomes an unobserved exception.
    /// </summary>
    [Fact]
    public async Task LateFault_AfterTheCompletionValue_IsReported()
    {
        var signal = new Subject<Unit>();
        Exception? reported = null;

        var wait = signal.ObserveCompletion(ex => reported = ex);

        signal.OnNext(Unit.Default);     // the interesting value — disposal finished
        await wait;                      // …and the waiter has settled on it

        var late = new InvalidOperationException("disposal faulted after it completed");
        signal.OnError(late);            // THE LATE FAULT

        reported.Should().BeSameAs(late,
            "the subscription's error arm outlives the task on purpose — .Await() unsubscribes " +
            "on settle and the fault then reaches nobody at all");
    }

    /// <summary>
    /// The same late fault through the old bridge reaches NOBODY — not even the <c>.Catch</c> that
    /// was supposed to handle it. Nothing throws, nothing logs, nothing fails.
    /// </summary>
    [Fact]
    public async Task LateFault_ThroughTheOldToTaskBridge_ReachesNobody()
    {
        var signal = new Subject<Unit>();
        Exception? seenByAnyone = null;

        // The exact bridge the interface documentation prescribed until this change.
        var wait = signal
            .Catch<Unit, Exception>(ex => { seenByAnyone = ex; return Observable.Return(Unit.Default); })
            .FirstOrDefaultAsync()
            .ToTask();

        signal.OnNext(Unit.Default);
        await wait;                      // FirstOrDefaultAsync has already unsubscribed

        signal.OnError(new InvalidOperationException("disposal faulted after it completed"));

        seenByAnyone.Should().BeNull(
            "once the bridge settled, the fault had no observer at all — this is the unobserved " +
            "exception that xUnit v3 escalates into the HOST_CRASHED failure");
    }

    /// <summary>
    /// The case #2301 actually took: the wait is abandoned (a <c>Timeout</c> there, a cancellation
    /// here) and the fault lands afterwards. The abandoned waiter still reports it.
    /// </summary>
    [Fact]
    public async Task FaultAfterTheWaitWasAbandoned_IsStillReported()
    {
        var signal = new Subject<Unit>();
        Exception? reported = null;
        using var cts = new CancellationTokenSource();

        var wait = signal.ObserveCompletion(ex => reported = ex, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        var late = new InvalidOperationException("faulted after the waiter gave up");
        signal.OnError(late);

        reported.Should().BeSameAs(late,
            "cancelling the WAIT must not detach the observation — a bound that orphans the work " +
            "it bounded is the #2301 mechanism");
    }

    /// <summary>A fault that arrives BEFORE any value is the task's own answer, not a late fault.</summary>
    [Fact]
    public async Task FaultBeforeAnyValue_FaultsTheReturnedTask()
    {
        var signal = new Subject<Unit>();
        Exception? reported = null;

        var wait = signal.ObserveCompletion(ex => reported = ex);
        signal.OnError(new InvalidOperationException("disposal faulted"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => wait);
        thrown.Message.Should().Be("disposal faulted");
        reported.Should().BeNull("the task carried it, so the late-fault reporter must NOT double-report");
    }

    /// <summary>
    /// A signal that already finished answers a late subscriber immediately — the replay contract
    /// <c>DisposalCompleted</c> (ReplaySubject) and <see cref="GrainDeactivationCompleted"/>
    /// (AsyncSubject) both have, and the reason subscribing can never be "too late".
    /// </summary>
    [Fact]
    public async Task SubscribingAfterTheSignalAlreadyFired_CompletesImmediately()
    {
        var signal = new AsyncSubject<Unit>();
        signal.OnNext(Unit.Default);
        signal.OnCompleted();

        // Record, never assert, inside the reporter — ObserveCompletion guards it, so an
        // Assert.Fail there would be swallowed and this test would pass regardless.
        Exception? lateFault = null;
        var wait = signal.ObserveCompletion(ex => lateFault = ex);

        await wait;
        wait.IsCompletedSuccessfully.Should().BeTrue();
        lateFault.Should().BeNull("an already-completed AsyncSubject cannot fault afterwards");
    }

    /// <summary>
    /// A reporter is MANDATORY. Nothing may pass an empty lambda "because it cannot fault" —
    /// that is how the fault stops being reported again.
    /// </summary>
    [Fact]
    public void AReporterIsRequired()
    {
        var signal = new Subject<Unit>();
        Assert.Throws<ArgumentNullException>(() => { _ = signal.ObserveCompletion(null!); });
    }

    /// <summary>
    /// 🚨 <b>#2772 — the opt-in that gives a cancelled caller its resource back.</b>
    ///
    /// <para><c>ObserveCompletion</c> deliberately does NOT dispose its subscription: that is what
    /// keeps the error arm attached so a LATE fault is still reported. But it silently changed
    /// cancellation semantics for every site converted from <c>.ToTask(ct)</c>, which DID dispose —
    /// and Rx cancels <c>IoPool.InvokeObservable</c>'s <c>subscriberCt</c> on subscription
    /// disposal. Under the default, a caller that has given up keeps its pool permit for the whole
    /// duration of work nobody is waiting for, and it is invisible: nothing faults, nothing logs,
    /// the slot is simply not available.</para>
    ///
    /// <para>The subscription's disposal is asserted through the SOURCE, not through a flag this
    /// test sets: <see cref="Observable.Create{TResult}(System.Func{IObserver{TResult}, IDisposable})"/>'s
    /// returned disposable runs if and only if Rx tears the subscription down, which is the exact
    /// signal that reaches a pooled operation.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CancelSourceTrue_DisposesTheSubscription_SoCancellationReachesTheSource()
    {
        // The producer→test signal is an AsyncSubject the PRODUCER completes — the house shape for
        // this (AGENTS.md), and it also gives the failure a sentence instead of a bare timeout.
        var disposed = new AsyncSubject<Unit>();
        var source = Observable.Create<Unit>(_ => Disposable.Create(() =>
        {
            disposed.OnNext(Unit.Default);
            disposed.OnCompleted();
        }));

        // Never an empty reporter — the contract forbids it, and an ignored late fault here would
        // hide the very thing this file exists to keep observable.
        Exception? late = null;
        using var cts = new CancellationTokenSource();
        var wait = source.ObserveCompletion(ex => late = ex, cancelSource: true, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        await disposed.Should().Within(10.Seconds()).Emit(
            "cancelling an opted-in wait must dispose the subscription — that disposal IS what "
            + "cancels Rx's subscriberCt and releases an IoPool permit (#2772)");
        late.Should().BeNull("the source only ever completes by disposal, so nothing may be reported");
    }

    /// <summary>
    /// The other direction, and the one that must not regress: the DEFAULT stays non-disposing, so
    /// a fault arriving after a cancelled wait still reaches the reporter. Making
    /// <c>cancelSource: true</c> the default would trade this away everywhere at once — the whole
    /// reason this bridge exists instead of <c>.ToTask()</c>.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CancelSourceDefault_KeepsTheSubscription_SoALateFaultIsStillReported()
    {
        var subject = new Subject<Unit>();
        Exception? seen = null;
        var reported = new AsyncSubject<Unit>();

        using var cts = new CancellationTokenSource();
        var wait = subject.ObserveCompletion(
            ex => { seen = ex; reported.OnNext(Unit.Default); reported.OnCompleted(); },
            cts.Token);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);

        // The wait is over; the source is not. Pre-fix AND post-fix this fault must be reported —
        // if it is ever lost here, the default has silently flipped.
        var boom = new InvalidOperationException("late");
        subject.OnError(boom);

        await reported.Should().Within(10.Seconds()).Emit(
            "the DEFAULT keeps the subscription attached, so a fault arriving after a cancelled wait "
            + "is still reported — if this ever goes silent, the default has flipped");
        seen.Should().BeSameAs(boom);
    }

    /// <summary>
    /// The race the <c>SingleAssignmentDisposable</c> exists for: the token fires while
    /// <c>Subscribe</c> is still in flight, so the handle is assigned to an ALREADY-cancelled
    /// registration. Assigning to a disposed SAD disposes the value immediately — without that, the
    /// source would keep running with nobody waiting, which is the exact leak this option removes.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task CancelSourceTrue_DisposesEvenWhenTheTokenFiresDuringSubscribe()
    {
        var disposed = new AsyncSubject<Unit>();
        using var cts = new CancellationTokenSource();

        var source = Observable.Create<Unit>(_ =>
        {
            cts.Cancel();                       // inside Subscribe, before the handle is assigned
            return Disposable.Create(() =>
            {
                disposed.OnNext(Unit.Default);
                disposed.OnCompleted();
            });
        });

        Exception? late = null;
        var wait = source.ObserveCompletion(ex => late = ex, cancelSource: true, cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        await disposed.Should().Within(10.Seconds()).Emit(
            "a token that fires DURING Subscribe must still reach the source: the handle is assigned "
            + "to an already-disposed SingleAssignmentDisposable, which disposes it on assignment");
        late.Should().BeNull("nothing faults here — a report would mean the SAD race took another route");
    }
}
