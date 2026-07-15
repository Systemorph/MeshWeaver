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
        => _pools.GetOrAdd(name, n => new IoPool(_options.MaxConcurrencyFor(n)));

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
    public void DrainAll()
    {
        foreach (var pool in _pools.Values)
            pool.Drain();
    }

    /// <summary>Disposes every created pool and clears the registry; called when the mesh is torn down.</summary>
    public void Dispose()
    {
        foreach (var pool in _pools.Values)
            pool.Dispose();
        _pools.Clear();
    }
}
