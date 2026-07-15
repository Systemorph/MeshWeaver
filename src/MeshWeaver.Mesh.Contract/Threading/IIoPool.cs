using System.Reactive;
using System.Reactive.Threading.Tasks;

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
    /// reactive-SDK counterpart to <see cref="InvokeStream{T}"/>; the <c>.ToTask()</c>
    /// bridge lives here, in the pool — never at the call site.</para>
    /// </summary>
    IObservable<T> InvokeObservable<T>(Func<CancellationToken, IObservable<T>> source)
        => Invoke(ct => source(ct).ToTask(ct));

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
}
