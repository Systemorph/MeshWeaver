using System.Diagnostics;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// A controlled I/O pool for one resource class. It is the single sealed boundary
/// between MeshWeaver's single-threaded, turn-based hub/grain schedulers and the
/// genuinely-async (or sync-blocking) I/O at the leaves — it pushes the work onto
/// the shared ThreadPool, bounds how much runs concurrently, and bridges the
/// result back into the reactive (<see cref="IObservable{T}"/>) contract.
///
/// <para>This is the generalization of the Postgres pattern
/// (<c>Observable.FromAsync(work, Scheduler.Default)</c> bounded by Npgsql's
/// connection pool) to resources that carry no pool of their own. It is hidden
/// inside the leaf adapters — public adapter signatures stay <c>IObservable&lt;T&gt;</c>.</para>
///
/// <para>All three methods return <b>cold</b> observables: the work runs on
/// <c>Subscribe</c>, not on call, and a pool slot is acquired only on Subscribe
/// and released when the operation completes, errors, or is unsubscribed.</para>
/// </summary>
public interface IIoPool
{
    /// <summary>
    /// Runs a genuinely-async I/O leaf (blob, HTTP, async file, DB round-trip).
    /// Bounded by an async semaphore gate over <c>Scheduler.Default</c>: the gate
    /// caps the number of in-flight operations, and the ThreadPool thread is
    /// released during the <c>await</c>, so a cap of 32 network ops consumes ~0
    /// threads while waiting.
    /// </summary>
    IObservable<T> Invoke<T>(Func<CancellationToken, Task<T>> io);

    /// <summary>
    /// Runs a genuinely-async I/O leaf that produces no value — e.g. an Orleans
    /// stream <c>UnsubscribeAsync</c>, a final flush on dispose. Same bounded
    /// async-gate semantics as <see cref="Invoke{T}"/>; emits a single
    /// <see cref="Unit"/> when the work completes so callers can observe (or await,
    /// in tests) teardown without inventing a dummy return value. The
    /// <c>Unit.Default</c> bridge lives here, in the pool — never at the call site.
    /// </summary>
    IObservable<Unit> Invoke(Func<CancellationToken, Task> io)
        => Invoke(async ct =>
        {
            await io(ct).ConfigureAwait(false);
            return Unit.Default;
        });

    /// <summary>
    /// Runs a sync-blocking / CPU-bound leaf (e.g. <c>File.ReadAllBytes</c>,
    /// Roslyn compile, <c>Process.WaitForExit</c>) on a dedicated
    /// limited-concurrency scheduler, so the (real, thread-holding) work cannot
    /// trigger ThreadPool thread-injection that would starve Orleans' grain
    /// schedulers.
    /// </summary>
    IObservable<T> InvokeBlocking<T>(Func<CancellationToken, T> work);

    /// <summary>
    /// Bridges an <see cref="IAsyncEnumerable{T}"/> leaf (e.g. a partition-objects
    /// enumeration) into a bounded observable, holding one pool slot for the
    /// duration of the enumeration and emitting each item as <c>OnNext</c>.
    /// </summary>
    IObservable<T> InvokeStream<T>(Func<CancellationToken, IAsyncEnumerable<T>> source);

    /// <summary>
    /// Bridges an <see cref="IObservable{T}"/> I/O leaf — e.g. an Octokit.Reactive
    /// <c>ObservableGitHubClient</c> call — into the pool: the leaf is subscribed on a
    /// ThreadPool thread behind the concurrency gate and its <b>last</b> value is emitted
    /// once it completes. It composes on <see cref="Invoke{T}"/>, so the async gate,
    /// <c>ConfigureAwait(false)</c> and off-hub scheduling all come from there — a reactive
    /// SDK leaf (itself <c>FromAsync</c>-shaped and otherwise unbounded on the subscribing
    /// hub scheduler) can never deadlock a hub/grain turn or exceed the pool's cap.
    ///
    /// <para>The leaf must emit at least one value (a single-item call, or a multi-item
    /// paginated <c>GetAll…</c> reduced with <c>.ToList()</c>/<c>.Any()</c> at the call
    /// site so the single emitted value is the whole result). This is the sanctioned
    /// reactive-SDK counterpart to <see cref="InvokeStream{T}"/>; the wait lives here,
    /// in the pool — never at the call site.</para>
    ///
    /// <para>🚨 <b>The wait is <see cref="ReactiveCompletion.ObserveCompletion{T}(System.IObservable{T}, System.Action{System.Exception}, bool, System.Threading.CancellationToken)"/>, never Rx's
    /// own observable-to-<see cref="Task"/> bridge</b> (maintainer, 2026-08-30: <i>"no ToTask
    /// ever"</i> — this pool used to be the one sanctioned exception, and is no longer). Rx's
    /// bridge completes its <see cref="TaskCompletionSource{TResult}"/> from inside the pipeline
    /// without <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/>, so the pool's
    /// own continuation — the code that RELEASES THE SLOT — resumed inline on whatever thread the
    /// SDK leaf signalled from, still inside Rx's trampoline. In a pool that is the worst place
    /// for it: the slot release is what unblocks the next queued operation.
    /// <c>ObserveCompletion</c> queues that continuation instead, and keeps its error arm attached
    /// so a leaf faulting after the wait settled is reported rather than orphaned.</para>
    ///
    /// <para><c>LastAsync()</c> is the faithful equivalent of the previous bridge's semantics:
    /// the leaf's FINAL value, emitted once it completes, and an <c>InvalidOperationException</c>
    /// if it emitted nothing at all.</para>
    /// </summary>
    IObservable<T> InvokeObservable<T>(Func<CancellationToken, IObservable<T>> source)
        // Invoke<T> explicitly: inference off the lambda would pick up ObserveCompletion's
        // nullable T? and hand back an IObservable<T?>.
        => Invoke<T>(ct => source(ct)
            .LastAsync()
            .ObserveCompletion(
                ex => Trace.TraceError(
                    "IIoPool.InvokeObservable: the I/O leaf faulted AFTER the wait settled — "
                    + "reported, not orphaned: {0}: {1}", ex.GetType().Name, ex.Message),
                ct)!);

    /// <summary>
    /// Runs the SUBSCRIBE of a long-lived reactive leaf — a <c>MeshQuery</c> change-feed subscription —
    /// through the pool. The subscribe action opens the providers and emits the initial snapshot, which
    /// can route → create a per-node hub (Autofac <c>BeginLifetimeScope</c>); that bounded, dangerous
    /// window holds one pool slot and counts as in-flight, so teardown's <c>Drain()</c> gate-join WAITS
    /// for it before the owning service scope is disposed — no <c>BeginLifetimeScope</c> races the scope
    /// teardown, which is the endemic teardown-SIGSEGV. The resulting subscription lives on and is
    /// disposed when the pool drains. Unlike <see cref="InvokeObservable{T}"/> (one-shot — awaits
    /// completion, emits the last value) this fits a NEVER-COMPLETING change feed. It is the tracked,
    /// drainable replacement for a bare <c>.SubscribeOn(TaskPoolScheduler.Default)</c>, which the drain
    /// cannot reach.
    /// </summary>
    IObservable<T> SubscribeThroughPool<T>(IObservable<T> source);

    /// <summary>Operations currently in flight through this pool. Diagnostics / tests only.</summary>
    int CurrentInFlight { get; }

    /// <summary>
    /// How long granted work WAITED for a slot on this pool, as a distribution.
    ///
    /// <para>The companion to <see cref="CurrentInFlight"/>, and the one that answers a different
    /// question: in-flight says how much is running NOW, this says what it COST to get there. A
    /// cap is too small when the tail buckets fill, not when the mean rises — see
    /// <see cref="IoPoolWaitStats"/> for why that distinction is the whole point (MeshWeaver#1198).</para>
    ///
    /// <para>Defaulted to <see cref="IoPoolWaitStats.Empty"/> so an implementation that does not
    /// instrument is not obliged to — an empty reading is honest ("this pool reports nothing"),
    /// and the caller can tell it from a busy pool by <see cref="IoPoolWaitStats.Samples"/>.</para>
    ///
    /// <para>🚨 <b>Read a pool through <see cref="IoPoolRegistry.Snapshot"/>, never by resolving it
    /// by name.</b> <see cref="IoPoolRegistry.Get"/> is a RESOLVER: handed a name no pool carries it
    /// MINTS one, which then honestly reports nothing — so a readout built on it answers a typo or
    /// an unwired backend with a clean, empty distribution that cannot be told from a real idle
    /// pool. <see cref="IoPoolQueueReport"/> is the formatter that keeps those two apart, and a
    /// third state ("no registry — nothing was asked") with them.</para>
    /// </summary>
    IoPoolWaitStats QueueWait => IoPoolWaitStats.Empty;

    /// <summary>
    /// Work that has REACHED an admission point and is queued for a slot right now — the live
    /// gauge to <see cref="QueueWait"/>'s history, and the other half of the picture
    /// <see cref="CurrentInFlight"/> starts: running, waiting, and what the waiting has cost.
    ///
    /// <para>Counted from the moment a leaf arrives at the gate (or, for blocking work, is handed
    /// to the scheduler) until it is granted or cancelled — so a depth that stays high while
    /// <see cref="CurrentInFlight"/> sits at the cap is a pool whose cap is the constraint.</para>
    ///
    /// <para>Defaulted to 0 so an implementation that does not instrument is not obliged to.</para>
    /// </summary>
    int CurrentlyWaiting => 0;
}
