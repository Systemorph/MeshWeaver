using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<IoPoolRegistry>? _logger;
    private int _disposing;
    private readonly System.Reactive.Subjects.AsyncSubject<int> _disposed = new();

    // 🚨 THE POOLS Dispose() IS WAITING ON, kept so the TIMEOUT path can still name them — issue
    // #2480. Dispose() clears _pools (a handed-out pool after disposal would never be joined by
    // anyone), so by the time a caller's bounded wait expires there was nothing left to enumerate
    // and the report named neither pool nor leaf. That is the whole reason #2480's 18 occurrences
    // are all the bare "pooled I/O did not finish within 00:00:30" line with nothing beside it.
    private volatile IReadOnlyList<KeyValuePair<string, IoPool>> _draining = [];

    // Which of _draining have completed their own Disposed. A pool that never unwinds never
    // reports, so this is what separates "joined" from "still holding a thread" WITHOUT waiting.
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

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
    /// <param name="logger">
    /// Optional — resolved from DI when the mesh registers logging. Names the offending pool at
    /// <see cref="DrainAll()"/> time (issue #2480: the teardown report carried no pool name, exception,
    /// or stack, so a fingerprint could never be turned into a direct pointer to the leaf).
    /// </param>
    public IoPoolRegistry(IoPoolOptions? options = null, ILogger<IoPoolRegistry>? logger = null)
    {
        _options = options ?? new IoPoolOptions();
        _logger = logger;
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

        // 🚨 A LOSING CANDIDATE MUST BE DISPOSED. ConcurrentDictionary.GetOrAdd does NOT promise the
        // value factory runs once: on concurrent first use of a name two pools are built and only
        // one is kept. The discarded one used to be garbage — a SemaphoreSlim and a scheduler nobody
        // referenced — so nothing noticed. It no longer is: a pool owns a started `IoPool-cancel`
        // thread from its constructor (#4448), and a thread is a GC ROOT that parks for the process's
        // life unless someone disposes the pool that owns it. So the factory records what it built
        // and anything that did not win the race is disposed, which wakes its canceller and lets it
        // exit. Same reason the refusal path below disposes `raced`.
        IoPool? candidate = null;
        var pool = _pools.GetOrAdd(name, n => candidate = new IoPool(_options.MaxConcurrencyFor(n), _options.DrainTimeout, _options.DrainGrace));
        if (candidate is not null && !ReferenceEquals(pool, candidate))
            candidate.Dispose();

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
    /// A reading of every pool that EXISTS right now — name, cap, in-flight, queue depth and the
    /// wait distribution — creating none.
    ///
    /// <para>🚨 <b>This is the only honest way to read a pool, and <see cref="Get"/> is not one.</b>
    /// <see cref="Get"/> is a resolver: handed a name no pool carries it MINTS one and hands it
    /// back, brand new and therefore reporting nothing. So a readout built on <see cref="Get"/>
    /// answers a typo, a renamed provider or a backend that is not wired at all with
    /// <c>Samples = 0</c> — indistinguishable from a real, idle pool, and it leaves a phantom in the
    /// registry besides. MeshWeaver#1198's whole history is instruments that answer confidently
    /// wrong; the reading that decides a cap must not be one of them, so it enumerates rather than
    /// asks.</para>
    ///
    /// <para>A point-in-time copy, ordered by name: the underlying dictionary is live and every
    /// counter is lock-free, so the readings are individually consistent and the set is not a
    /// transaction. That is the right trade for a diagnostic — a snapshot that took a lock would
    /// make reading the pools a way to stall them.</para>
    ///
    /// <para>Empty once disposal has begun (<see cref="Dispose"/> clears the pools), which reads
    /// correctly: a torn-down mesh has no pool queueing anybody.</para>
    /// </summary>
    public IReadOnlyList<IoPoolReading> Snapshot() =>
        _pools
            .Select(kv => new IoPoolReading(
                kv.Key,
                kv.Value.MaxConcurrency,
                kv.Value.CurrentInFlight,
                kv.Value.CurrentlyWaiting,
                kv.Value.QueueWait))
            .OrderBy(r => r.Name, StringComparer.Ordinal)
            .ToArray();

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
    public int DrainAll() => DrainAll(out _);

    /// <summary>
    /// <see cref="DrainAll()"/>, but also handing back WHICH pool leaked and how much.
    ///
    /// <para>🚨 <b>Why the ILogger warning was not enough.</b> The residual warning added for
    /// #2480 goes to <see cref="ILogger"/>, and <c>DrainAll</c> runs during mesh teardown — after
    /// the mesh's log sink has stopped capturing. Measured on the #2616 shard-2 trx: of <b>294</b>
    /// <c>Mesh.Dispose() invoking</c> windows, <b>zero</b> contain a single ILogger line at any
    /// level, while the same trx carries 84 <c>[Warning] [SynchronizationStream]</c> and 76
    /// <c>[Warning] [RoutingServiceBase]</c> records from BEFORE dispose. So the one diagnostic
    /// written to name the offending pool is structurally invisible in exactly the window it
    /// exists for: two occurrences of the drain flake (#2578, #2616) and the leaf is still an
    /// anonymous <c>"1"</c>.</para>
    ///
    /// <para>The residual therefore has to travel back as a RETURN VALUE, so the caller can put it
    /// somewhere that survives dispose — <c>TestPhaseTrace</c> in the test base, the
    /// <c>TeardownReport</c> in production. A diagnostic that can only be read where nobody is
    /// listening is the same defect as no diagnostic at all.</para>
    /// </summary>
    /// <param name="byPool">
    /// One entry per pool that did NOT fully unwind, in drain order. Empty on a clean drain.
    /// </param>
    public int DrainAll(out IReadOnlyList<PoolResidual> byPool) => DrainAll(out byPool, out _);

    /// <summary>
    /// <see cref="DrainAll(out IReadOnlyList{PoolResidual})"/>, also handing back the leaves each pool
    /// had to CANCEL because they outlived the drain grace with the pool making no further progress
    /// (<see cref="IoPool.LeavesCancelledAfterGrace"/>). Those leaves unwound — they are not a
    /// residual — but each is a unit of work that did not finish its job; the teardown report names
    /// them so a wedged write, read or compile is a visible finding rather than a silent kill.
    /// </summary>
    /// <param name="byPool">One entry per pool that did NOT fully unwind, in drain order.</param>
    /// <param name="cancelledByPool">One entry per pool that had to cancel a wedged leaf, in drain order.</param>
    public int DrainAll(out IReadOnlyList<PoolResidual> byPool, out IReadOnlyList<PoolResidual> cancelledByPool)
    {
        var leaked = 0;
        var residuals = new List<PoolResidual>();
        var cancelled = new List<PoolResidual>();
        foreach (var kvp in _pools)
        {
            // Read the sites BEFORE the drain returns nothing to name: Drain() joins, so by the
            // time it hands back a residual the surviving leaves are exactly the ones still
            // registered — but a leaf that unwinds during the join must not be reported, so this
            // is read after Drain and reflects what is genuinely still running.
            var residual = kvp.Value.Drain();
            var wedged = kvp.Value.LeavesCancelledAfterGrace;
            if (wedged != 0)
            {
                cancelled.Add(new PoolResidual(kvp.Key, wedged) { Sites = kvp.Value.CancelledLeafSites });
                _logger?.LogWarning(
                    "IoPoolDrain: pool '{PoolName}' had to CANCEL {Wedged} leaf(es) that made no progress "
                    + "within the {Grace} drain grace — that work did not finish its job. Find why it stalled; "
                    + "do not widen the grace. Cancelled: {Sites}", kvp.Key, wedged, _options.DrainGrace,
                    kvp.Value.CancelledLeafSites.Count == 0
                        ? "(no site captured)"
                        : string.Join(" | ", kvp.Value.CancelledLeafSites));
            }
            if (residual != 0)
                residuals.Add(new PoolResidual(kvp.Key, residual) { Sites = kvp.Value.PendingLeafSites });
            if (residual != 0)
                // 🚨 "IoPoolDrain", not "IoPoolSiloTeardown" (Copilot review, PR #2598): DrainAll()
                // is the generic mesh-teardown phase 2 (MeshTeardownExtensions AND
                // MonolithMeshTestBase.DisposeAsync both call it), not just the Orleans silo path —
                // the earlier prefix mislabeled every non-silo residual as if it came from
                // IoPoolSiloTeardown specifically. That class's own Dispose()/Disposed path below
                // keeps the "IoPoolSiloTeardown" prefix; it is where that name is accurate.
                _logger?.LogWarning(
                    "IoPoolDrain: pool '{PoolName}' did not finish {Residual} leaf(es) within "
                    + "the drain budget — a leaf ignored its cancellation token; fix the leaf, do not "
                    + "widen the budget. Still running: {Sites}", kvp.Key, residual,
                    kvp.Value.PendingLeafSites.Count == 0
                        ? "(no site captured)"
                        : string.Join(" | ", kvp.Value.PendingLeafSites));
            leaked += residual;
        }
        byPool = residuals;
        cancelledByPool = cancelled;
        return leaked;
    }

    /// <summary>
    /// One pool's unfinished-leaf count from <see cref="DrainAll(out IReadOnlyList{PoolResidual})"/>.
    /// <see cref="ToString"/> is the wire format the teardown traces embed, so a residual reads as
    /// <c>Query=1</c> rather than as a bare <c>1</c>.
    /// </summary>
    public readonly record struct PoolResidual(string Pool, int Residual)
    {
        /// <summary>
        /// The call sites of the leaves that did not unwind — a lambda's compiler-generated name
        /// carries its ENCLOSING method, so this points at the operation to fix. Empty when the
        /// pool could not offer them.
        ///
        /// <para>🚨 Without this the residual is an anonymous <c>AgentStore=1</c>: enough to know a
        /// pool did not drain, never enough to act. #2480 added the POOL NAME and stopped one level
        /// short — measured 2026-08-30, three dirty teardowns in 20 loaded runs of
        /// MeshWeaver.AI.Test named AgentStore, Query and FileSystem on different runs, and none of
        /// the three could be chased any further.</para>
        /// </summary>
        public IReadOnlyList<string> Sites { get; init; } = [];

        /// <inheritdoc />
        public override string ToString() =>
            Sites.Count == 0
                ? $"{Pool}={Residual}"
                : $"{Pool}={Residual} [{string.Join(" | ", Sites)}]";
    }

    /// <summary>
    /// The pools that <see cref="Dispose"/> asked to unwind and which have NOT reported yet — name,
    /// how many leaves are still in flight, and WHERE those leaves are — readable at ANY moment
    /// after disposal began, without waiting for anything.
    ///
    /// <para>🚨 <b>This is the attribution the TIMEOUT path needs, and it exists because the other
    /// one cannot reach it</b> (issue #2480). <see cref="Dispose"/> subscribes a per-pool warning to
    /// each pool's own <c>Disposed</c>, and that is sound for a pool that unwinds LATE — but a pool
    /// whose leaf never unwinds never completes <c>Disposed</c> at all, so on the one path this
    /// issue ever fires on the per-pool line is never written. The residual attribution was
    /// published on the SUCCESS signal, which is the #4466 inversion pointed at a report: the
    /// failure case is exactly the case that names nothing. Measured: all 18 occurrences of #2480
    /// are the bare <c>pooled I/O did not finish within 00:00:30</c> line, over three weeks, with no
    /// pool, no site and no stack — so "fix the leaf" could never be acted on because nothing ever
    /// said WHICH leaf.</para>
    ///
    /// <para>Empty before <see cref="Dispose"/> is called, and empty once every pool has reported —
    /// so a non-empty result means live pool work outlived the caller's join, which is the finding.
    /// Reads lock-free counters and a <see cref="ConcurrentDictionary{TKey,TValue}"/> only: a
    /// diagnostic must never be the reason a teardown blocks.</para>
    /// </summary>
    /// <summary>
    /// The budget a host's terminal drain holds shutdown for while waiting on <see cref="Disposed"/>
    /// — see <see cref="IoPoolOptions.SiloJoinBudget"/>. Read from the registry so the waiter needs
    /// no DI resolution of its own: <c>IoPoolSiloTeardown</c> captures this registry while the
    /// container is provably alive and its stop must resolve NOTHING (#1898/#1899).
    /// </summary>
    public TimeSpan SiloJoinBudget => _options.SiloJoinBudget;

    public IReadOnlyList<PoolResidual> UnreportedResiduals() =>
        _draining
            .Where(kvp => !_reported.ContainsKey(kvp.Key))
            .Select(kvp => new PoolResidual(kvp.Key, kvp.Value.CurrentInFlight)
            {
                Sites = kvp.Value.PendingLeafSites
            })
            .ToArray();

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
        var pools = _pools.ToArray();
        _pools.Clear();
        // Published BEFORE anything is cancelled, so a caller whose join expires can always
        // enumerate what it was waiting on — _pools is now empty and would name nothing (#2480).
        _draining = pools;
        if (pools.Length == 0)
        {
            _disposed.OnNext(0);
            _disposed.OnCompleted();
            return;
        }

        // Per-pool attribution (issue #2480: the silo-teardown report named neither pool nor leaf).
        // Logged the moment EACH pool's own Disposed fires — independent of the Zip below, and
        // independent of whether the caller's own bounded wait (IoPoolSiloTeardown's 30 s Timeout
        // on the aggregate) has already given up. A pool that unwinds AFTER that timeout still gets
        // attributed here, which is strictly more than the aggregate -1 residual ever names.
        foreach (var kvp in pools)
        {
            var name = kvp.Key;
            kvp.Value.Disposed.Subscribe(residual =>
            {
                // 🚨 Recorded FIRST, and unconditionally — UnreportedResiduals() subtracts this set,
                // so a pool that reported must leave it whatever its residual was. Doing it inside
                // the `residual != 0` branch below would leave every CLEAN pool looking un-joined
                // to the timeout report, which is the opposite lie to the one #2480 is about.
                _reported[name] = 0;
                if (residual != 0)
                    _logger?.LogWarning(
                        "IoPoolSiloTeardown: pool '{PoolName}' left {Residual} leaf(es) still "
                        + "running at dispose — a leaf ignored its cancellation token; fix the "
                        + "leaf, do not widen the budget.", name, residual);
            });
        }

        // Zip: one emission once EVERY pool has reported, carrying the total residual. A pool whose
        // leaf never unwinds never reports, so this never fires — and the caller's bounded wait
        // surfaces that as the timeout it is, rather than a false all-clear.
        Observable.Zip(pools.Select(kvp => kvp.Value.Disposed))
            .Select(residuals => residuals.Sum())
            .Take(1)
            .Subscribe(
                total => { _disposed.OnNext(total); _disposed.OnCompleted(); },
                _ => { _disposed.OnNext(0); _disposed.OnCompleted(); });

        foreach (var kvp in pools)
            kvp.Value.Dispose();
    }
}
