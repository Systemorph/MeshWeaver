using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Measures — rather than assumes — WHICH waits resume their awaiter on the thread that signalled.
/// This is the evidence behind the 2026-08-30 ruling (<i>"no ToTask ever"</i>) and behind the choice
/// of <see cref="ReactiveCompletion.ObserveCompletion{T}"/> as the replacement everywhere.
///
/// <para>🚨 <b>The trap this exists to stop is a plausible "simplification".</b> The obvious way to
/// remove a <c>.ToTask()</c> is to await the observable DIRECTLY — <c>await source.FirstAsync()</c>.
/// It reads cleaner, it drops a namespace, and it looks like exactly what "stay reactive" means.
/// It also does not fix anything: Rx's own awaiter is built on <see cref="AsyncSubject{T}"/>, which
/// completes its continuation from inside <c>OnCompleted</c> — on the signalling thread — so it has
/// the SAME inline-resume property as the bridge it replaced. Swapping one for the other is a
/// no-op dressed as a fix, and it would be invisible in review.</para>
///
/// <para>Only <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> actually breaks the
/// chain, which is why <c>ObserveCompletion</c> is the sanctioned wait and why every conversion in
/// this sweep went through it. Its companion property — that the error arm survives the settle —
/// is pinned separately by <c>DisposalWaitBridgeTest</c>.</para>
///
/// <para>Why it matters beyond tidiness: the resumed continuation inherits the producer's thread
/// AND, because <c>await</c> captures <see cref="TaskScheduler.Current"/> when there is no
/// <see cref="SynchronizationContext"/>, every later <c>await</c> in the same method schedules onto
/// it too. That is #2301 (a grain teardown holding the scheduler its own deactivation needed) and
/// #2377 (a query walk enqueued on a trampoline that could only drain after the block that was
/// waiting for it returned).</para>
/// </summary>
public class InlineResumptionMechanismTest
{
    /// <summary>
    /// Signals from a known thread and reports whether the awaiter resumed INLINE — that is,
    /// whether the wait had already completed by the time the signalling call returned.
    ///
    /// <para>🚨 <b>This used to compare managed thread IDs, and that probe is unsound.</b>
    /// <c>Environment.CurrentManagedThreadId</c> differing proves nothing: a correctly QUEUED
    /// continuation runs on a pool thread, and the pool is free to hand it the very thread that
    /// just finished signalling. So <c>NotEqual(signalling, resumed)</c> fails intermittently on a
    /// loaded shard while the behaviour under test is perfectly correct — observed 2026-08-30 on a
    /// documentation-only PR. The same unsound shape is recorded two files away, in
    /// <c>ActivityLiveProgressTest</c>: <i>"createdThread != commandThread — the runtime makes no
    /// such [guarantee]"</i>.</para>
    ///
    /// <para>Inline resumption has a DIRECT observable: the continuation runs on the signaller's
    /// stack, so the wait is already complete when <c>OnCompleted</c> returns. Queued resumption
    /// cannot be. That is the property, measured rather than proxied — and the two positive
    /// controls below (Rx's bridge, and awaiting the observable directly) prove the measurement
    /// discriminates, because they must report TRUE.</para>
    /// </summary>
    private static async Task<(bool ResumedInline, int Signalling, int Resumed)> Measure(
        Func<IObservable<int>, Task> wait)
    {
        var subject = new Subject<int>();
        var resumed = 0;
        var insideSignal = 0;
        var observedInsideSignal = false;

        var waiter = Task.Run(async () =>
        {
            await wait(subject);
            resumed = Environment.CurrentManagedThreadId;
            observedInsideSignal = Volatile.Read(ref insideSignal) == 1;
        });

        // Let the waiter subscribe before we signal. A bare Subject drops a notification that
        // arrives with no observer attached, so the wait would never complete.
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var signalling = Environment.CurrentManagedThreadId;
        Volatile.Write(ref insideSignal, 1);
        subject.OnNext(1);
        subject.OnCompleted();
        Volatile.Write(ref insideSignal, 0);

        await waiter;

        // 🚨 SAME THREAD **AND** STILL INSIDE THE SIGNALLING CALL. Neither half is sufficient and
        // both earlier attempts at this proved it:
        //   * thread ids differing proves nothing — the pool may hand a QUEUED continuation the
        //     very thread that just finished signalling (the intermittent this replaces);
        //   * "the waiter had completed when OnCompleted returned" proves nothing either — the
        //     pool can run a queued continuation inside that window, and measurably does: it
        //     reported inline on every one of five runs against ObserveCompletion.
        // Together they are rigorous, because a thread cannot be in two places at once: if the
        // continuation ran on the signalling thread WHILE the signaller was still inside OnNext/
        // OnCompleted, it can only have been on the signaller's own stack. A queued continuation
        // that later lands on that recycled thread necessarily finds the flag already cleared,
        // because the thread was occupied until the signaller left the call.
        return (resumed == signalling && observedInsideSignal, signalling, resumed);
    }

    /// <summary>
    /// The banned bridge, and the behaviour that got it banned: the awaiting code continues on the
    /// producer's thread.
    /// </summary>
    [Fact]
    public async Task TheRxToTaskBridge_ResumesItsAwaiterOnTheSignallingThread()
    {
        var (resumedInline, signalling, resumed) = await Measure(
            async source => await source.FirstAsync().ToTask());

        Assert.True(resumedInline,
            "Rx's bridge completes its TaskCompletionSource from inside the pipeline without "
            + "RunContinuationsAsynchronously, so the awaiter runs on the signaller's stack and the "
            + $"wait is already finished when OnCompleted returns (signalled on {signalling}, "
            + $"resumed on {resumed})");
    }

    /// <summary>
    /// 🚨 THE POINT OF THIS FILE. Awaiting the observable directly is NOT a fix — it resumes inline
    /// exactly as the bridge does, because Rx's awaiter is an <see cref="AsyncSubject{T}"/> that
    /// completes its continuation from inside <c>OnCompleted</c>.
    /// </summary>
    [Fact]
    public async Task AwaitingTheObservableDirectly_AlsoResumesOnTheSignallingThread()
    {
        var (resumedInline, signalling, resumed) = await Measure(
            async source => await source.FirstAsync());

        Assert.True(resumedInline,
            "Rx's awaiter is an AsyncSubject that completes its continuation from inside "
            + "OnCompleted — awaiting the observable directly has the SAME inline-resume property "
            + $"as the bridge it appears to replace (signalled on {signalling}, resumed on {resumed})");
    }

    /// <summary>
    /// The fix, and the reason every conversion in the sweep went through it.
    /// </summary>
    [Fact]
    public async Task ObserveCompletion_DoesNotResumeOnTheSignallingThread()
    {
        var lateFaults = 0;
        var (resumedInline, signalling, resumed) = await Measure(
            async source => await source.FirstAsync().ObserveCompletion(_ => Interlocked.Increment(ref lateFaults)));

        Assert.False(resumedInline,
            "RunContinuationsAsynchronously must QUEUE the continuation, so the wait cannot already "
            + "be complete when OnCompleted returns. 🚨 This deliberately does NOT assert that the "
            + "thread ids differ: a queued continuation may legitimately run on the pool thread that "
            + $"just signalled (signalled on {signalling}, resumed on {resumed})");
        Assert.Equal(0, lateFaults);
    }
}
