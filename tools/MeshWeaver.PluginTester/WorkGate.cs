using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.PluginTester;

/// <summary>
/// A concurrency cap SHARED by every caller: work handed to <see cref="Run{T}"/> runs on the
/// scheduler, at most <c>maxParallel</c> items at a time across everything enqueued, through the
/// same <c>Merge(maxParallel)</c> primitive <see cref="Cascade"/> uses — no semaphore, no thread
/// parked on a wait.
///
/// <para><b>Why it exists.</b> <c>mw-plugin-test build</c> runs its PACKAGES through the cascade,
/// and until this gate it compiled each package's NodeTypes one after another. The in-mesh lane's
/// critical path is one package: MeshWeaver.Plugins' <c>Hosting</c>, 48 NodeTypes, measured at
/// <c>compile 1516.6s</c> of a 1,749.7 s build (<c>parallel speed-up 1.1×</c>, Plugins run
/// 40614698191). The types of one package do not reference each other's emitted assemblies
/// (Systemorph/MeshWeaver.Plugins#1970), so they can compile side by side. One gate for the whole
/// build keeps the total number of concurrent Roslyn compilations at the cap however many packages
/// are in flight — a per-package gate would multiply it by the package count.</para>
/// </summary>
internal sealed class WorkGate : IDisposable
{
    private readonly ISubject<IObservable<Unit>> queue;
    private readonly IDisposable subscription;
    private readonly IScheduler scheduler;

    /// <summary>Opens a gate of <paramref name="maxParallel"/> slots.</summary>
    /// <param name="maxParallel">The cap — 1 runs everything one at a time.</param>
    /// <param name="scheduler">Where the work runs; the task pool by default.</param>
    public WorkGate(int maxParallel, IScheduler? scheduler = null)
    {
        if (maxParallel < 1)
            throw new ArgumentOutOfRangeException(nameof(maxParallel), maxParallel, "at least one slot");
        MaxParallel = maxParallel;
        this.scheduler = scheduler ?? TaskPoolScheduler.Default;
        // Synchronized: packages enqueue from whichever pool thread their cascade slot runs on.
        var subject = new Subject<IObservable<Unit>>();
        queue = Subject.Synchronize(subject);
        subscription = subject.Merge(maxParallel).Subscribe(_ => { }, _ => { });
    }

    /// <summary>The cap.</summary>
    public int MaxParallel { get; }

    /// <summary>
    /// One item: enqueued when the returned stream is subscribed, run when a slot is free, and its
    /// value (or exception) emitted on the returned stream — on the scheduler, never inside the
    /// item's own pipeline, so a continuation the caller chains on it never occupies a slot.
    /// </summary>
    public IObservable<T> Enqueue<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Observable.Defer(() =>
        {
            var result = new AsyncSubject<T>();
            queue.OnNext(Observable.Defer(() => Observable.Start(work, scheduler))
                .Do(value =>
                    {
                        result.OnNext(value);
                        result.OnCompleted();
                    },
                    result.OnError)
                .Select(_ => Unit.Default)
                // A failed item frees its slot and leaves the gate open for the rest; the
                // exception reaches its caller through `result`.
                .Catch(Observable.Empty<Unit>()));
            // 🚨 Delivered OFF the slot. `result` completes inside the item's own pipeline, before
            // the gate's Merge sees that item complete, so without this boundary whatever the
            // caller chains on the value — `mw-plugin-test build` folds a package and RUNS ITS
            // TESTS there — would execute while still holding a compile slot.
            return result.ObserveOn(scheduler);
        });
    }

    /// <summary>
    /// Every item through the gate, emitted once as one array IN INPUT ORDER whatever order they
    /// finished in — so a caller folds the results exactly as a serial loop would have produced
    /// them. An empty input emits an empty array.
    /// </summary>
    public IObservable<ImmutableArray<T>> Run<T>(IReadOnlyList<Func<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (work.Count == 0)
            return Observable.Return(ImmutableArray<T>.Empty);
        return work
            .Select((item, index) => Enqueue(item).Select(value => (index, value)))
            .Merge()
            .ToList()
            .Select(done => done.OrderBy(d => d.index).Select(d => d.value).ToImmutableArray());
    }

    /// <summary>Closes the gate; items not yet started never run.</summary>
    public void Dispose()
    {
        queue.OnCompleted();
        subscription.Dispose();
    }
}
