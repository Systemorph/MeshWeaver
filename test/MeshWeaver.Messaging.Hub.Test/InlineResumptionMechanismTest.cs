using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Measures — rather than assumes — WHICH waits resume their awaiter on the thread that signalled.
/// This is the evidence behind the 2026-08-30 ruling (<i>"no ToTask ever"</i>) and behind the choice
/// of <see cref="ReactiveCompletion.ObserveCompletion{T}(System.IObservable{T}, System.Action{System.Exception}, System.Threading.CancellationToken)"/> as the replacement everywhere.
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
    /// Signals from a known thread and reports where the awaiter resumed.
    ///
    /// <para>🚨 The signalling thread is a DEDICATED thread, never a pool worker. The measurement is
    /// "did the awaiter resume on the signalling thread", and that is only a proxy for "inline" if
    /// the pool can never legitimately put the continuation on that same thread. After an
    /// <c>await Task.Delay</c> this method itself runs on a pool worker; a
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> completion queues the
    /// continuation to the pool, and the moment <c>OnCompleted</c> returns and this method yields
    /// at <c>await waiter</c>, that same worker is free to pick the continuation up — same managed
    /// thread id, NOT inline. Measured on CI (2026-08-30, #2781, shard 5): thread 84 both sides,
    /// on a diff of three documentation lines. A thread the pool does not own closes that hole:
    /// inline resumption runs on it by definition, pooled resumption never can.</para>
    /// </summary>
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

        var signalling = 0;
        var producer = new Thread(() =>
        {
            Volatile.Write(ref signalling, Environment.CurrentManagedThreadId);
            subject.OnNext(1);
            subject.OnCompleted();
        }) { IsBackground = true, Name = "InlineResumptionMechanismTest.producer" };
        producer.Start();
        // No Join: in the inline cases the waiter completes INSIDE OnCompleted, and a task
        // continuation runs synchronously on the completing thread — so this very method resumes
        // on the producer, and a Join here would be a thread joining itself. `signalling` is
        // published before the signal that completes `waiter` (the completion's interlocked state
        // change already orders it); the volatile pair makes that hand-off explicit.
        await waiter;
        return (Volatile.Read(ref signalling), resumed);
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
    /// 🚨 THE SHAPE A REDUCER-KEYED SWEEP CANNOT SEE, measured on the same rig.
    ///
    /// <para>Everything above ends in a REDUCER — <c>await source.FirstAsync()</c> — and every
    /// instrument this repo pointed at the defect keyed on one, so all of them reported zero for a
    /// chain whose last operator is <c>.Take(1).Timeout(…)</c>. That is not a different mechanism:
    /// the reducer is irrelevant, the awaiter is Rx's either way, and the continuation lands on the
    /// producer either way. <c>MeshWeaver.Testing.InMesh.MeshTestContext.First</c> shipped exactly
    /// this line — under a doc comment offering <i>"Rx's own awaiter — no task bridge"</i> as the
    /// SAFETY property — and that assembly runs its cases INSIDE the portal, so the producer here
    /// stands in for a hub's action block or a grain's turn scheduler.</para>
    /// </summary>
    [Fact]
    public async Task AwaitingAChainWhoseTailIsAnOperator_AlsoResumesOnTheSignallingThread()
    {
        var (signalling, resumed) = await Measure(
            async source => await source.Take(1).Timeout(TestTimeouts.Convergence));

        // 🚨 Both halves. The equality alone would hold vacuously if the rig never ran — a pair of
        // zeros is equal — so the thread ids are asserted to be real ones first.
        Assert.NotEqual(0, signalling);
        Assert.NotEqual(0, resumed);
        Assert.Equal(signalling, resumed);
    }

    /// <summary>
    /// The fix AT THAT SITE: the same chain, handed to the sanctioned bridge. <c>Await</c> is a
    /// faithful <c>ToTask</c> — LAST value, faulting on an empty sequence — and the <c>Take(1)</c>
    /// makes LAST and FIRST the same element, so only the continuation's scheduling changes.
    /// </summary>
    [Fact]
    public async Task Await_DoesNotResumeOnTheSignallingThread()
    {
        var (signalling, resumed) = await Measure(
            async source => await source.Take(1).Timeout(TestTimeouts.Convergence)
                .Await(TestContext.Current.CancellationToken));

        Assert.NotEqual(0, signalling);
        Assert.NotEqual(0, resumed);
        Assert.NotEqual(signalling, resumed);
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
