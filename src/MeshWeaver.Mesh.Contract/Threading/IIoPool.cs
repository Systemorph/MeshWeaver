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

    /// <summary>Operations currently in flight through this pool. Diagnostics / tests only.</summary>
    int CurrentInFlight { get; }
}
