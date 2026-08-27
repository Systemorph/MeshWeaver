using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

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
        var wait = signal.ObserveCompletion(_ => { });

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
            "the subscription's error arm outlives the task on purpose — .ToTask() unsubscribes " +
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

        var wait = signal.ObserveCompletion(_ => Assert.Fail("no fault is possible here"));

        await wait;
        wait.IsCompletedSuccessfully.Should().BeTrue();
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
}
