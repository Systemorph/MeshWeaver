using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace MeshWeaver.Reactive;

/// <summary>
/// The re-query shape for a LIVE query: one run at a time, and every trigger that arrives while a
/// run is in flight folds into ONE follow-up — never a queue of one run per trigger.
///
/// <para>🚨 <b>What it replaces, and why.</b> The live pipelines of the query providers were written
/// <c>triggers.Select(_ =&gt; RunQuery()).Concat()</c>: one full read QUEUED per change notification,
/// with no bound. A burst of N writes under one live query's scope cost N back-to-back reads, and the
/// queue grows fastest exactly when reads are slowest — each read then holds its pool slot longer, and
/// more notifications pile up behind it, every one of them another full read. That backlog is what an
/// Initial issued on the same process (a grain activation's path resolution, a compile watcher's
/// sources listing) queues behind, until the query fan-in's stall terminal fires on it
/// (MeshWeaver#5344, #5315, #1186).</para>
///
/// <para><b>Why this is not a debounce.</b> There is no timer and no batching window: an idle pipeline
/// runs the moment a trigger arrives, so no subscriber can attach mid-window and read a pre-write
/// snapshot (the race the old 100 ms <c>Buffer</c> had). The follow-up starts when the current run
/// completes, which is after the last folded trigger arrived — so it reads every write those triggers
/// announced. Its answer is the queue's LAST answer; only the redundant reads in between are gone.</para>
///
/// <para>Emissions are serialised by construction (one run at a time), which is what a live diff over
/// shared state relies on. An error from a run or from the triggers terminates the output; the output
/// completes once the triggers have completed and no run is in flight or pending.</para>
/// </summary>
public static class CoalesceWhileRunningExtensions
{
    /// <summary>
    /// Runs <paramref name="run"/> once per trigger, but never two at once and never a queue: a
    /// trigger that arrives while a run is in flight marks ONE follow-up run, started the moment the
    /// current one completes. See the type remarks.
    /// </summary>
    /// <typeparam name="TTrigger">The trigger element type; its values are not read.</typeparam>
    /// <typeparam name="TResult">What a run emits.</typeparam>
    /// <param name="triggers">The source of re-run requests (change notifications).</param>
    /// <param name="run">Creates ONE cold run; called once per started run.</param>
    /// <returns>Every run's emissions, in run order.</returns>
    public static IObservable<TResult> CoalesceWhileRunning<TTrigger, TResult>(
        this IObservable<TTrigger> triggers, Func<IObservable<TResult>> run)
        => Observable.Create<TResult>(observer =>
        {
            var gate = new object();
            var running = false;
            var pending = false;
            var triggersDone = false;
            var stopped = false;
            var current = new SerialDisposable();

            void Start()
            {
                // Iterative, not recursive: a run that completes synchronously loops here instead of
                // nesting a Start per pending trigger.
                while (true)
                {
                    var completedSynchronously = false;
                    var inStart = true;
                    IDisposable subscription;
                    try
                    {
                        subscription = run().Subscribe(
                            item =>
                            {
                                lock (gate)
                                    if (stopped) return;
                                observer.OnNext(item);
                            },
                            ex =>
                            {
                                lock (gate)
                                {
                                    if (stopped) return;
                                    stopped = true;
                                }
                                observer.OnError(ex);
                            },
                            () =>
                            {
                                bool again, complete = false;
                                lock (gate)
                                {
                                    if (stopped) return;
                                    again = pending;
                                    pending = false;
                                    running = again;
                                    if (!again && triggersDone)
                                    {
                                        stopped = true;
                                        complete = true;
                                    }
                                    if (again && inStart)
                                    {
                                        completedSynchronously = true;
                                        return;
                                    }
                                }
                                if (complete) observer.OnCompleted();
                                else if (again) Start();
                            });
                    }
                    catch (Exception ex)
                    {
                        lock (gate)
                        {
                            if (stopped) return;
                            stopped = true;
                        }
                        observer.OnError(ex);
                        return;
                    }
                    current.Disposable = subscription;
                    lock (gate)
                    {
                        inStart = false;
                        if (!completedSynchronously) return;
                    }
                }
            }

            var triggerSubscription = triggers.Subscribe(
                _ =>
                {
                    lock (gate)
                    {
                        if (stopped) return;
                        if (running)
                        {
                            pending = true;
                            return;
                        }
                        running = true;
                    }
                    Start();
                },
                ex =>
                {
                    lock (gate)
                    {
                        if (stopped) return;
                        stopped = true;
                    }
                    observer.OnError(ex);
                },
                () =>
                {
                    lock (gate)
                    {
                        triggersDone = true;
                        if (running || stopped) return;
                        stopped = true;
                    }
                    observer.OnCompleted();
                });

            return new CompositeDisposable(
                triggerSubscription,
                current,
                Disposable.Create(() =>
                {
                    lock (gate) stopped = true;
                }));
        });
}
