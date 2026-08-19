using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// Mesh-scoped resolver of named <see cref="IIoPool"/> instances. Registered as a
/// singleton in <c>MeshBuilder</c>, so its lifetime IS the mesh's: when the mesh
/// is disposed every pool (and its <see cref="SemaphoreSlim"/>) dies with it. No
/// static state — the backing dictionary is an instance field.
///
/// <para>Pools are created lazily on first use, so Wave-2/3 pool names
/// (<see cref="IoPoolNames.Http"/>, etc.) cost nothing until a leaf actually
/// touches that resource class.</para>
/// </summary>
public sealed class IoPoolRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, IoPool> _pools = new(StringComparer.Ordinal);
    private readonly IoPoolOptions _options;
    private int _disposing;
    private readonly System.Reactive.Subjects.AsyncSubject<int> _disposed = new();

    /// <summary>
    /// Emits the TOTAL number of leaves that did not unwind across every pool, once all of them
    /// have been drained AND released, then completes. <c>0</c> is the contract: no pool thread is
    /// running any more, so collectible node ALCs may be unloaded and the owning scope released.
    ///
    /// <para>🚨 This is what a silo must await before releasing — not <see cref="Dispose"/>'s
    /// return, and not <c>IMessageHub.DisposalCompleted</c> (which covers only action blocks and
    /// message round-trips, never the offloaded ThreadPool I/O). AsyncSubject, so a late
    /// subscriber still receives the report.</para>
    /// </summary>
    public IObservable<int> Disposed => _disposed.AsObservable();

    /// <summary>
    /// Creates the registry with the given per-resource-class concurrency options.
    /// </summary>
    /// <param name="options">Concurrency caps per pool name; defaults are used when null.</param>
    public IoPoolRegistry(IoPoolOptions? options = null)
    {
        _options = options ?? new IoPoolOptions();
    }

    /// <summary>
    /// Gets (creating on first use) the bounded pool for the given resource-class
    /// name. The cap comes from <see cref="IoPoolOptions.MaxConcurrencyFor"/>.
    /// </summary>
    public IIoPool Get(string name)
    {
        // 🚨 A pool handed out AFTER disposal began would never be cancelled or joined by anyone —
        // work issued on it runs unsupervised straight through the ALC unload, which is the exact
        // hole this whole teardown path exists to close. Dispose() snapshots and clears _pools, so
        // without this a racing Get() silently re-populates the dictionary with a live pool.
        // (Copilot review, #1887.)
        if (Volatile.Read(ref _disposing) != 0)
            return _refused.Value;

        var pool = _pools.GetOrAdd(name, n => new IoPool(_options.MaxConcurrencyFor(n)));

        // Re-check: disposal may have begun between the check above and the add, in which case our
        // pool went in after the snapshot was taken. Pull it back out and refuse — losing a pool
        // mid-shutdown is safe (its work is cancelled), whereas leaving one live is not.
        if (Volatile.Read(ref _disposing) != 0 && _pools.TryRemove(name, out var raced))
        {
            raced.Dispose();
            return _refused.Value;
        }

        return pool;
    }

    // An already-disposed pool: every entry point on it returns a cancelled observable, so a late
    // caller is refused loudly-but-gracefully instead of getting something that will outlive the
    // teardown. Instance, not static — it dies with the registry (NoStaticState).
    private readonly Lazy<IoPool> _refused = new(() =>
    {
        var pool = new IoPool(1);
        pool.Dispose();
        return pool;
    });

    /// <summary>
    /// Total operations currently executing across every pool. Zero means no
    /// offloaded I/O continuation is in flight — the safe point at which the
    /// owning mesh's service scope may be torn down without a continuation
    /// resolving a disposed scope.
    /// </summary>
    public int TotalInFlight => _pools.Values.Sum(p => p.CurrentInFlight);

    /// <summary>
    /// Completes once <see cref="TotalInFlight"/> reaches zero (polled), or after
    /// <paramref name="timeout"/> elapses — whichever comes first. This is the
    /// "wait for the I/O queue" half of mesh teardown: hub <c>DisposalCompleted</c>
    /// only drains the action blocks + message round-trips, but I/O offloaded onto
    /// the ThreadPool via <see cref="IIoPool"/> runs independently. If the service
    /// scope is disposed while such an operation is still running, its continuation
    /// (which may resolve a service) throws <see cref="ObjectDisposedException"/>
    /// from the dead Autofac scope — surfacing as an unobserved "catastrophic"
    /// failure. Await this between <c>DisposalCompleted</c> and scope disposal.
    /// On timeout it completes anyway (a stuck slot is a separate bug the caller
    /// can surface from a non-zero <see cref="TotalInFlight"/>).
    /// </summary>
    public IObservable<Unit> WhenDrained(TimeSpan timeout) =>
        Observable.Interval(TimeSpan.FromMilliseconds(20))
            .StartWith(-1L)
            .Select(_ => TotalInFlight)
            .Where(inFlight => inFlight == 0)
            .Take(1)
            .Select(_ => Unit.Default)
            .Timeout(timeout)
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default));

    /// <summary>
    /// Synchronously drains every created pool: cancels all in-flight leaves and JOINS (blocks until
    /// they have unwound) — see <see cref="IoPool.Drain"/>. Unlike <see cref="WhenDrained"/> (which
    /// only WAITS, so a live change-feed subscription never reaches zero and it times out), this
    /// CANCELS the work so it actually stops. Call it between the hub's <c>DisposalCompleted</c>
    /// and service-scope disposal so no pooled I/O thread is still executing a collectible node ALC's
    /// compiled types when that scope disposes and unloads them (the teardown use-after-unload SIGSEGV).
    /// </summary>
    /// <returns>
    /// The total number of leaves across all pools that did NOT unwind within the drain budget
    /// (see <see cref="IoPool.Drain"/>). <c>0</c> means the join is real and the caller may proceed
    /// to scope disposal / ALC unload. Non-zero means live work survives teardown — surface it.
    /// </returns>
    public int DrainAll()
    {
        var leaked = 0;
        foreach (var pool in _pools.Values)
            leaked += pool.Drain();
        return leaked;
    }

    /// <summary>Disposes every created pool and clears the registry; called when the mesh is torn down.</summary>
    public void Dispose()
    {
        // Idempotent, and only the first caller reports.
        if (Interlocked.CompareExchange(ref _disposing, 1, 0) != 0) return;

        // 🚨 NON-BLOCKING. Each pool.Dispose() cancels its leaves and completes its own Disposed
        // when the last one unwinds — nothing here waits. A blocking join belongs nowhere near a
        // Dispose(): `using var pool = …` in an async method runs it on a ThreadPool thread, and
        // parking one there while the leaves it waits for need pool threads starves into a
        // deadlock on a small runner. The WAIT lives on Disposed, awaited asynchronously.
        var pools = _pools.Values.ToArray();
        _pools.Clear();
        if (pools.Length == 0)
        {
            _disposed.OnNext(0);
            _disposed.OnCompleted();
            return;
        }

        // Zip: one emission once EVERY pool has reported, carrying the total residual. A pool whose
        // leaf never unwinds never reports, so this never fires — and the caller's bounded wait
        // surfaces that as the timeout it is, rather than a false all-clear.
        Observable.Zip(pools.Select(pool => pool.Disposed))
            .Select(residuals => residuals.Sum())
            .Take(1)
            .Subscribe(
                total => { _disposed.OnNext(total); _disposed.OnCompleted(); },
                _ => { _disposed.OnNext(0); _disposed.OnCompleted(); });

        foreach (var pool in pools)
            pool.Dispose();
    }
}
