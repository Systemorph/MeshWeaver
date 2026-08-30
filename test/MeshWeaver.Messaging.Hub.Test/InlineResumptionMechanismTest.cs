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
    /// <summary>Signals from a known thread and reports where the awaiter resumed.</summary>
    private static async Task<(int Signalling, int Resumed)> Measure(Func<IObservable<int>, Task> wait)
    {
        var subject = new Subject<int>();
        var resumed = 0;

        var waiter = Task.Run(async () =>
        {
            await wait(subject);
            resumed = Environment.CurrentManagedThreadId;
        });

        // Let the waiter subscribe before we signal. A bare Subject drops a notification that
        // arrives with no observer attached, so the wait would never complete.
        await Task.Delay(300, TestContext.Current.CancellationToken);

        var signalling = Environment.CurrentManagedThreadId;
        subject.OnNext(1);
        subject.OnCompleted();
        await waiter;
        return (signalling, resumed);
    }

    /// <summary>
    /// The banned bridge, and the behaviour that got it banned: the awaiting code continues on the
    /// producer's thread.
    /// </summary>
    [Fact]
    public async Task TheRxToTaskBridge_ResumesItsAwaiterOnTheSignallingThread()
    {
        var (signalling, resumed) = await Measure(async source => await source.FirstAsync().ToTask());

        Assert.Equal(signalling, resumed);
    }

    /// <summary>
    /// 🚨 THE POINT OF THIS FILE. Awaiting the observable directly is NOT a fix — it resumes inline
    /// exactly as the bridge does, because Rx's awaiter is an <see cref="AsyncSubject{T}"/> that
    /// completes its continuation from inside <c>OnCompleted</c>.
    /// </summary>
    [Fact]
    public async Task AwaitingTheObservableDirectly_AlsoResumesOnTheSignallingThread()
    {
        var (signalling, resumed) = await Measure(async source => await source.FirstAsync());

        Assert.Equal(signalling, resumed);
    }

    /// <summary>
    /// The fix, and the reason every conversion in the sweep went through it.
    /// </summary>
    [Fact]
    public async Task ObserveCompletion_DoesNotResumeOnTheSignallingThread()
    {
        var lateFaults = 0;
        var (signalling, resumed) = await Measure(
            async source => await source.FirstAsync().ObserveCompletion(_ => Interlocked.Increment(ref lateFaults)));

        Assert.NotEqual(signalling, resumed);
        Assert.Equal(0, lateFaults);
    }
}
