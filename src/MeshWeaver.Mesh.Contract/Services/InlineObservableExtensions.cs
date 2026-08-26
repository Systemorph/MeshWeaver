using System.Reactive.Concurrency;
using System.Reactive.Linq;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Turns an in-memory sequence into an observable that iterates <b>on the subscribing thread, during
/// <c>Subscribe</c></b> — the shape every <see cref="IStorageAdapter"/> read and every pedestrian
/// query walk in this codebase already claims to have.
/// </summary>
/// <remarks>
/// <para>🚨 <b>Never use the parameterless <c>IEnumerable.ToObservable()</c> on a storage read or a
/// query walk.</b> Rx defaults it to <c>SchedulerDefaults.Iteration</c>, which is
/// <see cref="CurrentThreadScheduler"/> — and that scheduler does NOT mean "run it here now". It
/// means "run it on <i>this thread's</i> Rx trampoline". <c>CurrentThreadScheduler</c> keeps a
/// <c>[ThreadStatic] bool</c> saying whether a trampoline is already running on the thread; when one
/// is, <c>Schedule</c> merely <b>enqueues</b> and returns, and the item is drained by whoever owns
/// that outer trampoline — not by us.</para>
///
/// <para><b>The failure that motivated this (issue #2377), and it is not a test artefact.</b> Rx
/// runs every operator subscription through that trampoline (<c>Producer.SubscribeRaw</c>), so a
/// frame can be deep inside one without any local sign of it. The captured 558-frame stack from a
/// reproduction reads, bottom-up:</para>
///
/// <code>
/// MessageService.DrainOne()                       // the hub's own pump, on a ThreadPool thread
///  → Producer.SubscribeRaw
///    → CurrentThreadScheduler.Schedule            // trampoline OPENED here; the flag is now set
///      → … ~500 Rx frames …
///        → ToTaskObserver.OnCompleted → TaskCompletionSource.TrySetResult   // a .ToTask() resolves
///          → AwaitTaskContinuation.RunOrScheduleAction(allowInlining: true) // awaiter resumes INLINE
///            → … the awaiting code, and everything it goes on to call …
/// </code>
///
/// <para>Completing a <c>Task</c> from inside an Rx pipeline — exactly what
/// <c>FirstAsync().ToTask()</c>, an <c>AsyncSubject</c>, or a <c>TaskCompletionSource</c> resolved on
/// an <c>OnNext</c> does — resumes the awaiter <b>on that thread, still inside the trampoline</b>,
/// and everything it then calls inherits the flag. If such a frame subscribes a query and then
/// BLOCKS waiting for its first result, the walk it just enqueued can only run after the block
/// returns, and the block only returns when the walk runs: the query's <c>Initial</c> is
/// <b>never emitted, with no error and no completion</b> — a live children listing that silently
/// stays empty forever.</para>
///
/// <para>Under xUnit the reach is wider still: the resumed continuation was a mesh teardown await,
/// so the test runner carried on <i>on that stack</i> and started subsequent tests inside the
/// trampoline. That is why <c>LiveQueryHandoffDropTest</c> failed ~23% of cold whole-assembly runs on
/// a 4-CPU Linux runner (its 30 s warm-up wait was the block) while passing every warm, single-test
/// run.</para>
///
/// <para><see cref="ImmediateScheduler"/> has no such ambient state: it invokes the action directly,
/// and its recursive form is trampolined through a per-call <c>AsyncLock</c>, so a long sequence
/// iterates without growing the stack. <c>LiveQueryForeignTrampolineTest</c> pins this.</para>
/// </remarks>
public static class InlineObservableExtensions
{
    /// <summary>
    /// Iterates <paramref name="source"/> inline on the subscribing thread — see the type remarks
    /// for why the parameterless <c>ToObservable()</c> is unsafe on a read path.
    /// </summary>
    /// <typeparam name="T">Element type.</typeparam>
    /// <param name="source">The sequence to emit.</param>
    /// <returns>An observable that emits every element during <c>Subscribe</c>.</returns>
    public static IObservable<T> ToInlineObservable<T>(this IEnumerable<T> source)
        => source.ToObservable(ImmediateScheduler.Instance);
}
