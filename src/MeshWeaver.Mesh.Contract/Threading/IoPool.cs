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
    // Idempotence for Dispose. A plain `if (_disposed) return` cannot serve: _disposed is now set
    // AFTER the join (so Drain's own guard does not short-circuit it), which leaves a window where
    // two callers would both drain and both dispose the gate.
    private int _disposing;
    private int _disposalReported;
    // Fires the residual leaf count once the join AND the resource release have happened, then
    // completes. AsyncSubject so a subscriber attaching after disposal still gets the report —
    // the same contract MeshTeardownSignal uses.
    private readonly System.Reactive.Subjects.AsyncSubject<int> _disposedSubject = new();

    /// <summary>
    /// Emits the number of leaves that did NOT unwind (see <see cref="Drain"/>) once this pool has
    /// been drained AND its gate/cancellation released, then completes. <c>0</c> means no pool
    /// thread is running any more, so the caller may unload collectible node ALCs.
    ///
    /// <para>Await this — never <see cref="Dispose"/>'s return — before releasing anything the
    /// pooled work could still be executing. <see cref="IoPoolRegistry.Disposed"/> aggregates it
    /// across every pool, which is what silo shutdown waits on.</para>
    /// </summary>
    public IObservable<int> Disposed => _disposedSubject.AsObservable();

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


    // 🚨 AFTER DISPOSAL, A LEAF IS CANCELLED — NOT AN ObjectDisposedException.
    //
    // Disposal releases _poolCts and _gate, so the natural code path below would throw
    // ObjectDisposedException from `_poolCts.Token` / `_gate.WaitAsync`. That matters because the
    // pool is now disposed DURING SILO SHUTDOWN (IoPoolSiloTeardown), while the mesh is still
    // being torn down afterwards: hub disposal keeps pushing final writes at the pool, and an
    // ObjectDisposedException surfacing out of a reactive chain there is the "catastrophic
    // teardown" class this codebase already fights.
    //
    // Cancellation is also the HONEST answer. A drained pool already cancels every leaf issued
    // after Drain(); disposal is that same terminal state plus the resource release, so callers
    // should see the same thing either way. "The pool is gone, this work will not run" is a
    // cancellation, never a caller bug.
    private static IObservable<T> Cancelled<T>() =>
        Observable.Throw<T>(new OperationCanceledException(
            "The I/O pool has been disposed (mesh or silo teardown) — this leaf will not run."));

    /// <summary>Number of operations currently executing through this pool.</summary>
    public int CurrentInFlight => Volatile.Read(ref _inFlight);

    /// <summary>
    /// Runs an async I/O leaf off the calling scheduler under the pool's concurrency gate.
    /// </summary>
    /// <typeparam name="T">Result type produced by the I/O operation.</typeparam>
    /// <param name="io">The cancellable async work to run once the gate grants a slot.</param>
    /// <returns>A cold observable that, on subscribe, runs the work and emits its single result.</returns>
    public IObservable<T> Invoke<T>(Func<CancellationToken, Task<T>> io)
        => _disposed ? Cancelled<T>() : InvokeCore(io);

    private IObservable<T> InvokeCore<T>(Func<CancellationToken, Task<T>> io)
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
                // 🚨 RELEASE THE PERMIT FIRST. TryFinishDisposal disposes _gate the moment the
                // last leaf's decrement takes _inFlight to zero — so with the decrement first,
                // THIS leaf disposed the gate and then called Release() on it, throwing
                // ObjectDisposedException out of its own finally. Not a cross-thread race: the
                // last leaf to unwind during a dispose does it to itself, every time (issue
                // #2135, seen in prod as a failed `Comments` area render on memex-cloud).
                //
                // Releasing first is safe for the drain guarantee: Drain joins by re-acquiring
                // every permit, and once Release has run the only work left in this finally is
                // two interlocked ops — no user code, no ALC code — so "no pool thread is still
                // running the leaf" remains true at the moment Drain observes the permit.
                ReleaseGateThenFinish();
            }
        }).SubscribeOn(TaskPoolScheduler.Default);

    /// <summary>
    /// The exit path every gated leaf shares: hand the permit back while the gate is still alive,
    /// then account for the leaf and let disposal complete if it was the last one.
    ///
    /// <para>Extracted so the ordering exists in ONE place. It was duplicated at three call sites
    /// and wrong at all three, which is what made <see cref="Dispose"/>'s careful "release the
    /// resources on the last leaf's way out" design throw on that very last leaf.</para>
    /// </summary>
    private void ReleaseGateThenFinish()
    {
        _gate.Release();
        Interlocked.Decrement(ref _inFlight);
        TryFinishDisposal();
    }

    /// <summary>
    /// Streams an async-enumerable I/O leaf off the calling scheduler under the pool's concurrency gate.
    /// </summary>
    /// <typeparam name="T">Element type produced by the stream.</typeparam>
    /// <param name="source">The cancellable async sequence to enumerate once the gate grants a slot.</param>
    /// <returns>A cold observable that, on subscribe, enumerates the source and emits each element.</returns>
    public IObservable<T> InvokeStream<T>(Func<CancellationToken, IAsyncEnumerable<T>> source)
        => _disposed ? Cancelled<T>() : InvokeStreamCore(source);

    private IObservable<T> InvokeStreamCore<T>(Func<CancellationToken, IAsyncEnumerable<T>> source)
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
                ReleaseGateThenFinish();
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
        => _disposed ? Cancelled<T>() : InvokeBlockingCore(work);

    private IObservable<T> InvokeBlockingCore<T>(Func<CancellationToken, T> work)
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
                        // AFTER both counters — a blocking leaf holds _blockingInFlight as well as
                        // _inFlight, so calling this between the two decrements would always see a
                        // non-zero count, return early, and leave nothing to retry: Disposed would
                        // never fire for a pool whose last leaf was a blocking one.
                        TryFinishDisposal();
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
        _disposed ? Cancelled<T>() : SubscribeThroughPoolCore(source);

    private IObservable<T> SubscribeThroughPoolCore<T>(IObservable<T> source) =>
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
                        ReleaseGateThenFinish();
                    }
                    return System.Reactive.Unit.Default;
                })
                .SubscribeOn(TaskPoolScheduler.Default)
                .Subscribe(
                    _ => { },
                    ex =>
                    {
                        // A drain/unsubscribe cancellation is expected teardown, so it must not be
                        // surfaced as a FAULT — but it must still TERMINATE the observer.
                        //
                        // 🚨 Swallowing it outright (the previous behaviour) left the subscriber with
                        // neither OnCompleted nor OnError: the drain registration below disposes
                        // `inner`, and disposing a subscription emits nothing. The observable then
                        // never terminated, so every `.Finally(...)` hung off it never ran — which is
                        // exactly the bookkeeping that releases a route's in-flight slot and advances
                        // OrderedRouteDispatcher's per-destination FIFO (RoutingGrain.Dispatch,
                        // OrderedRouteDispatcher.DrainNext). A leg cancelled by the drain therefore
                        // leaked its slot and stranded its destination's queue permanently. Silent
                        // non-termination is the one thing this codebase never tolerates: an error
                        // must reach a graceful sink, never a silent hang.
                        if (ex is OperationCanceledException)
                            observer.OnCompleted();
                        else
                            observer.OnError(ex);
                    });

            // If the pool drains AFTER the subscribe completed, tear the live subscription down too
            // — and TERMINATE the observer, which disposing it does not do.
            //
            // 🚨 THE OTHER HALF OF THE SAME LEAK — issue #1789. The error arm above covers a leg the
            // drain cancels BEFORE or DURING `source.Subscribe(observer)`: its gate wait throws
            // OperationCanceledException and the arm turns that into OnCompleted. A leg cancelled
            // AFTER the subscribe completed never goes near that arm — it arrives here, and this
            // registration used to call `inner.Dispose()` and nothing else. Disposing a subscription
            // EMITS NOTHING: no OnCompleted, no OnError. So the observable returned by
            // SubscribeThroughPool terminated in neither direction and every `.Finally(...)` hung off
            // it never ran — exactly the bookkeeping that decrements RoutingGrain's `inFlightRoutes`
            // and advances OrderedRouteDispatcher's per-destination FIFO. The slot leaked and the
            // destination's queue stranded PERMANENTLY, which is also why `cleared after` became
            // structurally impossible to log (it needs in-flight to fall back below half the
            // threshold). Prod, 2026-08-17: two saturation Criticals ten minutes apart at identical
            // depth, both emitted deep inside the termination grace period — i.e. after
            // IoPool.Drain() had cancelled `_poolCts` — with no clear line in the whole window.
            //
            // OnCompleted, not OnError, for the same reason the arm above chose it: a drain is
            // expected teardown, not a fault. The latch makes the terminal exactly-once even if the
            // drain races an unsubscribe (Rx's AutoDetachObserver would swallow a second one anyway;
            // relying on that would leave the invariant implicit).
            //
            // This does NOT change IoPool.Drain()'s join reasoning: a leg past its subscribe holds
            // no gate permit and is not counted in `_inFlight`, so terminating it moves neither the
            // permit count Drain re-acquires nor the residual it reports.
            var drainTerminated = 0;
            IDisposable drainReg;
            try
            {
                drainReg = _poolCts.Token.Register(() =>
                {
                    if (Interlocked.Exchange(ref drainTerminated, 1) != 0) return;
                    inner.Dispose();
                    observer.OnCompleted();
                });
            }
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
    /// safely unload the node ALCs whose types that work referenced. Called when the mesh is torn
    /// down, and — deterministically, before anything is released — from silo shutdown.
    ///
    /// <para>The join is bounded by <see cref="Drain"/>'s budget, so a leaf that ignores its token
    /// cannot hang shutdown; it is reported through <see cref="Disposed"/> instead.</para>
    /// </summary>
    public void Dispose()
    {
        // 🚨 DISPOSE MUST NOT BLOCK. An earlier attempt made this call Drain() — a synchronous
        // join with a 30 s budget — which is itself the hazard: `using var pool = new IoPool(8)`
        // in an async method runs this on a ThreadPool thread, so the join parks a pool thread
        // while the very leaves it is waiting for need pool threads to observe the cancellation
        // and release their permits. On a 4-vCPU CI runner that starves into a deadlock, and
        // OrderedRouteDispatcherTest hung for its full 30 s budget — the exact failure recorded
        // when this fix was first deferred. An 18-core dev box hides it completely.
        //
        // So: cancel here (leaves unwind promptly), and put the WAITING on `Disposed`, which the
        // caller awaits ASYNCHRONOUSLY. Resource release happens on the last leaf's way out —
        // see TryFinishDisposal — because a leaf still running would otherwise touch a disposed
        // _gate / _poolCts.
        if (Interlocked.CompareExchange(ref _disposing, 1, 0) != 0) return;

        // Set BEFORE the cancel so a leaf issued in the gap short-circuits to Cancelled<T>()
        // instead of racing the token.
        _disposed = true;
        try { _poolCts.Cancel(); } catch (ObjectDisposedException) { /* already released */ }

        // Covers the common case: nothing in flight, so disposal completes right here.
        TryFinishDisposal();
    }

    /// <summary>
    /// Completes disposal once the last leaf has unwound: releases the gate/CTS and fires
    /// <see cref="Disposed"/>. Called from <see cref="Dispose"/> (for an already-idle pool) and
    /// from every leaf's exit path. Idempotent — only the transition to zero reports.
    ///
    /// <para>If a leaf never unwinds, <see cref="Disposed"/> simply never fires and the caller's
    /// bounded wait surfaces that as the timeout it is. That is the honest outcome: the resources
    /// stay held rather than being pulled out from under a live thread.</para>
    /// </summary>
    private void TryFinishDisposal()
    {
        if (Volatile.Read(ref _disposing) == 0) return;
        if (Volatile.Read(ref _inFlight) != 0 || Volatile.Read(ref _blockingInFlight) != 0) return;
        if (Interlocked.CompareExchange(ref _disposalReported, 1, 0) != 0) return;

        _poolCts.Dispose();
        _gate.Dispose();
        _blockingIdle.Dispose();
        _disposedSubject.OnNext(0);
        _disposedSubject.OnCompleted();
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
