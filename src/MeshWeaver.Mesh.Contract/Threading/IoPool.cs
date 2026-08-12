using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// The bounded <see cref="IIoPool"/> implementation for a single resource class.
/// One instance owns one <see cref="SemaphoreSlim"/> (the async gate) and one
/// <see cref="LimitedConcurrencyLevelTaskScheduler"/> (for sync-blocking work),
/// both sized to the same cap. Instances are owned by <see cref="IoPoolRegistry"/>
/// (a mesh-scoped singleton) and disposed with the mesh — no static state.
/// </summary>
public sealed class IoPool : IIoPool, IDisposable
{
    private readonly SemaphoreSlim _gate;
    private readonly TaskFactory _blockingFactory;
    private readonly int _maxConcurrency;
    // Pool-wide cancellation, linked into every leaf's token. Drain()/Dispose() cancel it so all
    // in-flight leaves unwind promptly — the join then knows they will release their gate permits.
    private readonly CancellationTokenSource _poolCts = new();
    private int _inFlight;

    // 🚨 Idle signal for BLOCKING leaves, which hold no gate permit — see Drain(). Set while no
    // blocking leaf is running, so the join has something real to wait on instead of polling.
    // A ManualResetEventSlim here is consistent with this file being the ONE sanctioned home for
    // such a primitive: IoPool IS the boundary between the turn-based schedulers and blocking I/O.
    private readonly ManualResetEventSlim _blockingIdle = new(initialState: true);

    // 🚨 A DEDICATED counter, not _inFlight. _inFlight is shared with Invoke / InvokeStream /
    // SubscribeThroughPool, so 0↔1 transitions on it do NOT correspond to blocking work starting or
    // stopping — and the signal was driven off exactly those transitions. Both directions broke: a
    // blocking leaf starting while an async leaf ran incremented to 2, so the Reset never fired and
    // Drain saw "idle" (the original bug, unfixed), and a blocking leaf finishing while an async leaf
    // ran decremented to 1, so the Set never fired and Drain waited out its whole budget then
    // reported a survivor that had already unwound. (Copilot review, #1334.)
    private int _blockingInFlight;
    private volatile bool _disposed;

    // Teardown safety net for the drain join: cancellation makes in-flight leaves release in ms,
    // so this is effectively never hit — it only bounds a hang if a leaf ignores its token.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates a pool whose async gate and sync-blocking scheduler are both capped at
    /// <paramref name="maxConcurrency"/>.
    /// </summary>
    /// <param name="maxConcurrency">Maximum number of operations allowed to run concurrently; must be at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxConcurrency"/> is less than 1.</exception>
    public IoPool(int maxConcurrency)
    {
        if (maxConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        _maxConcurrency = maxConcurrency;
        _gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _blockingFactory = new TaskFactory(
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            TaskContinuationOptions.None,
            new LimitedConcurrencyLevelTaskScheduler(maxConcurrency));
    }

    /// <summary>Number of operations currently executing through this pool.</summary>
    public int CurrentInFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Runs an async I/O leaf off the calling scheduler under the pool's concurrency gate.
    /// </summary>
    /// <typeparam name="T">Result type produced by the I/O operation.</typeparam>
    /// <param name="io">The cancellable async work to run once the gate grants a slot.</param>
    /// <returns>A cold observable that, on subscribe, runs the work and emits its single result.</returns>
    public IObservable<T> Invoke<T>(Func<CancellationToken, Task<T>> io)
        // SubscribeOn moves the whole subscribe — including the gate wait and the
        // synchronous prologue of `io` — onto a ThreadPool thread, so the work
        // never runs on the calling hub/grain scheduler. (FromAsync's own
        // scheduler arg only affects notification delivery, not where the
        // function is invoked — hence the SubscribeOn, matching MeshQuery.)
        => Observable.FromAsync(async subscriberCt =>
        {
            // Link the subscriber's token with the pool-wide token so Drain()/Dispose()
            // cancels this leaf too — the teardown join relies on every running leaf
            // unwinding and releasing its gate permit once the pool is cancelled.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(subscriberCt, _poolCts.Token);
            var ct = linked.Token;
            // WaitAsync(ct) makes acquisition itself cancellable — a dispose
            // before the slot is granted throws here, before the increment, so
            // no slot is ever leaked. The ThreadPool thread is released during
            // the inner await, so the gate caps in-flight ops, not threads.
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _inFlight);
            try
            {
                return await io(ct).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                _gate.Release();
            }
        }).SubscribeOn(TaskPoolScheduler.Default);

    /// <summary>
    /// Streams an async-enumerable I/O leaf off the calling scheduler under the pool's concurrency gate.
    /// </summary>
    /// <typeparam name="T">Element type produced by the stream.</typeparam>
    /// <param name="source">The cancellable async sequence to enumerate once the gate grants a slot.</param>
    /// <returns>A cold observable that, on subscribe, enumerates the source and emits each element.</returns>
    public IObservable<T> InvokeStream<T>(Func<CancellationToken, IAsyncEnumerable<T>> source)
        => Observable.Create<T>(async (observer, subscriberCt) =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(subscriberCt, _poolCts.Token);
            var ct = linked.Token;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            Interlocked.Increment(ref _inFlight);
            try
            {
                await foreach (var item in source(ct).WithCancellation(ct).ConfigureAwait(false))
                    observer.OnNext(item);
                observer.OnCompleted();
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
                _gate.Release();
            }
        }).SubscribeOn(TaskPoolScheduler.Default);

    /// <summary>
    /// Runs a synchronous, blocking or CPU-bound leaf on the pool's limited-concurrency scheduler
    /// so it never blocks the calling hub/grain thread.
    /// </summary>
    /// <typeparam name="T">Result type produced by the work.</typeparam>
    /// <param name="work">The cancellable blocking work to run once the scheduler grants a slot.</param>
    /// <returns>A cold observable that, on subscribe, runs the work and emits its single result; unsubscribing cancels it.</returns>
    public IObservable<T> InvokeBlocking<T>(Func<CancellationToken, T> work)
        => Observable.Create<T>(observer =>
        {
            // Linked to the pool token so Drain()/Dispose() cancels blocking work too.
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_poolCts.Token);
            _blockingFactory.StartNew(() =>
                {
                    // _inFlight increments only once the scheduler grants a slot —
                    // so CurrentInFlight reflects actually-running blocking work,
                    // capped at the scheduler's MaximumConcurrencyLevel.
                    Interlocked.Increment(ref _inFlight);
                    if (Interlocked.Increment(ref _blockingInFlight) == 1)
                        _blockingIdle.Reset();
                    try
                    {
                        return work(cts.Token);
                    }
                    finally
                    {
                        // 🚨 ORDER MATTERS: the signal is set LAST. Drain() wakes on _blockingIdle, so
                        // anything it releases must already observe fully-updated counters — setting
                        // the signal before decrementing _inFlight let Drain return while
                        // CurrentInFlight was still 1, which is both a false "no pool thread is
                        // running" and a flake in this file's own assertion. (Copilot review, #1338.)
                        Interlocked.Decrement(ref _inFlight);
                        if (Interlocked.Decrement(ref _blockingInFlight) == 0)
                            _blockingIdle.Set();
                    }
                }, cts.Token)
                .ContinueWith(t =>
                {
                    if (t.IsCanceled)
                    {
                        // Unsubscribed before completion — silent teardown.
                    }
                    else if (t.IsFaulted)
                    {
                        observer.OnError(t.Exception!.GetBaseException());
                    }
                    else
                    {
                        observer.OnNext(t.Result);
                        observer.OnCompleted();
                    }
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return Disposable.Create(() =>
            {
                try { cts.Cancel(); }
                catch { /* already disposed */ }
                finally { cts.Dispose(); }
            });
        });

    /// <inheritdoc />
    public IObservable<T> SubscribeThroughPool<T>(IObservable<T> source) =>
        Observable.Create<T>(observer =>
        {
            // The long-lived subscription the setup leaf produces; disposed on unsubscribe OR pool drain.
            var inner = new SingleAssignmentDisposable();

            // Run the SUBSCRIBE — providers opening + the initial-snapshot emission that routes →
            // CreateHub (Autofac BeginLifetimeScope) — as a TRACKED, GATED, pool-cancellable leaf,
            // exactly like Invoke. So while that bounded, dangerous window runs it holds a gate permit
            // and counts in _inFlight; Drain() cancels _poolCts then RE-ACQUIRES every permit, so it
            // BLOCKS until this subscribe has released — i.e. the owning Autofac scope is never disposed
            // while a BeginLifetimeScope is running (the endemic teardown SIGSEGV).
            var setup = Observable.FromAsync(async subscriberCt =>
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(subscriberCt, _poolCts.Token);
                    var ct = linked.Token;
                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    Interlocked.Increment(ref _inFlight);
                    try
                    {
                        // Draining before we even subscribed → do not open providers / create hubs.
                        ct.ThrowIfCancellationRequested();
                        inner.Disposable = source.Subscribe(observer);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _inFlight);
                        _gate.Release();
                    }
                    return System.Reactive.Unit.Default;
                })
                .SubscribeOn(TaskPoolScheduler.Default)
                .Subscribe(
                    _ => { },
                    ex =>
                    {
                        // A drain/unsubscribe cancellation is expected teardown — swallow it; surface real faults.
                        if (ex is not OperationCanceledException)
                            observer.OnError(ex);
                    });

            // If the pool drains AFTER the subscribe completed, tear the live subscription down too.
            IDisposable drainReg;
            try { drainReg = _poolCts.Token.Register(inner.Dispose); }
            catch (ObjectDisposedException) { drainReg = Disposable.Empty; }

            return new CompositeDisposable(setup, inner, drainReg);
        });

    /// <summary>
    /// Cancels every in-flight leaf and JOINS — blocks until they have all unwound — WITHOUT
    /// disposing the pool. Called before a collectible node <c>AssemblyLoadContext</c> is unloaded
    /// so no pool thread is still executing (or about to dereference) that ALC's compiled types
    /// when it is torn down (the teardown use-after-unload SIGSEGV). TERMINAL — it cancels the
    /// pool token, so any leaf issued after Drain is cancelled immediately; idempotent (a second
    /// call is a safe no-op join). This is a teardown operation; the pool is not reused afterwards.
    /// </summary>
    /// <remarks>
    /// The join uses the gate we already own — no poll, no sleep, no extra signal. A running leaf
    /// holds one permit until its <c>finally</c> releases it; once <see cref="_poolCts"/> is
    /// cancelled every waiting leaf's <c>WaitAsync</c> throws, so no NEW leaf can take a permit.
    /// Re-acquiring all <see cref="_maxConcurrency"/> permits therefore blocks precisely until the
    /// last running leaf has released, then we release them back so the pool stays usable/idempotent.
    /// </remarks>
    /// <returns>
    /// The number of leaves that did NOT unwind within the drain budget — gate permits that could not
    /// be re-acquired because an async leaf ignored its cancellation token, PLUS any blocking leaf
    /// (<see cref="InvokeBlocking{T}"/>) still running, which holds no permit and so is counted
    /// separately. <c>0</c> means the join is REAL:
    /// no pool thread is still running when this returns. Anything else means teardown is about to
    /// proceed over live work (the use-after-unload SIGSEGV precondition) — the caller must surface
    /// it, never swallow it: a drain that silently gives up is how "disposal completed" becomes a
    /// lie and the crash moves 8&#160;ms into the next test's INIT where nothing can attribute it.
    /// </returns>
    public int Drain()
    {
        if (_disposed) return 0; // gate + cts already gone; nothing in flight to join
        _poolCts.Cancel();
        var acquired = 0;
        for (var i = 0; i < _maxConcurrency; i++)
        {
            if (_gate.Wait(DrainTimeout)) acquired++;
            else break; // safety net: a leaf ignored its token — don't hang teardown forever
        }
        if (acquired > 0)
            _gate.Release(acquired);
        var gateResidual = _maxConcurrency - acquired;

        // 🚨 BLOCKING LEAVES HOLD NO GATE PERMIT — so the gate join above joins NOTHING for them, and
        // this method returned 0 ("the join is REAL: no pool thread is still running") while a
        // Roslyn compile or a file read was still executing. That is precisely the use-after-unload
        // precondition Drain exists to rule out, and it was reachable on every node read
        // (CachingStorageAdapter routes through InvokeBlocking on the FileSystem pool). The existing
        // guard test only exercised Invoke, which DOES take a permit, so this went untested.
        // Repro: IoPoolTest.Drain_joins_InvokeBlocking_leaves_too.
        //
        // Join them on their own idle signal, bounded by the SAME budget — no new timeout, no poll.
        // Re-read the counter when the wait expires: a leaf that finished between the signal reset
        // and our wait would otherwise be reported as surviving.
        if (!_blockingIdle.Wait(DrainTimeout))
        {
            // _blockingInFlight, never _inFlight: an async leaf that ignored its token is ALREADY
            // reported through gateResidual, so adding it again would double-count it.
            var stillRunning = Volatile.Read(ref _blockingInFlight);
            return gateResidual + stillRunning;
        }

        return gateResidual;
    }

    /// <summary>
    /// Drains in-flight work (see <see cref="Drain"/>) then disposes the gate and cancellation
    /// source. Synchronous by design: when it returns, no pool thread is running, so the caller may
    /// safely unload the node ALCs whose types that work referenced. Called when the mesh is torn down.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        // 🚨 Dispose() joins NOTHING today: it sets _disposed and only THEN calls Drain(), whose
        // idempotence guard returns early on exactly that flag — so the promise in this method's
        // summary ("when it returns, no pool thread is running, so the caller may safely unload the
        // node ALCs whose types that work referenced") is unbacked on the disposal path, which is the
        // path that unloads every ALC.
        //
        // Deliberately NOT fixed here. Swapping the two lines makes Dispose actually cancel and join,
        // which CHANGES TEARDOWN SEMANTICS repo-wide, and the first CI run that carried it saw
        // OrderedRouteDispatcherTest hang for its full 30s budget (it passes locally in 1s, so the
        // attribution is unproven either way). That belongs in its own PR where a hang can only have
        // one cause. Tracked with the evidence in #1338's review thread.
        _disposed = true;
        Drain();
        _poolCts.Dispose();
        _gate.Dispose();
        _blockingIdle.Dispose();
    }

    /// <summary>
    /// A stateless, unbounded fallback pool used when no mesh-scoped pool is
    /// wired (e.g. an adapter constructed with <c>new</c> outside DI, in tests).
    /// It still offloads onto <c>Scheduler.Default</c> (the ThreadPool) — so it
    /// is never worse than the bare <c>Observable.FromAsync</c> it replaces — but
    /// applies no concurrency cap. It holds no mutable state, so it is a true
    /// immutable constant (allowed as <c>static</c>), not a cache.
    /// </summary>
    public static IIoPool Unbounded { get; } = new UnboundedIoPool();

    private sealed class UnboundedIoPool : IIoPool
    {
        public int CurrentInFlight => 0;

        public IObservable<T> Invoke<T>(Func<CancellationToken, Task<T>> io)
            => Observable.FromAsync(io).SubscribeOn(TaskPoolScheduler.Default);

        public IObservable<T> InvokeStream<T>(Func<CancellationToken, IAsyncEnumerable<T>> source)
            => Observable.Create<T>(async (observer, ct) =>
            {
                await foreach (var item in source(ct).WithCancellation(ct).ConfigureAwait(false))
                    observer.OnNext(item);
                observer.OnCompleted();
            }).SubscribeOn(TaskPoolScheduler.Default);

        public IObservable<T> InvokeBlocking<T>(Func<CancellationToken, T> work)
            => Observable.FromAsync(ct => Task.Run(() => work(ct), ct)).SubscribeOn(TaskPoolScheduler.Default);

        // No pool → no drain to coordinate with; the historical bare-threadpool behaviour.
        public IObservable<T> SubscribeThroughPool<T>(IObservable<T> source)
            => source.SubscribeOn(TaskPoolScheduler.Default);
    }
}
