using System.Collections.Concurrent;
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

    // 🚨 THE ADMISSION COUNTER — issue #2146. Counts callers that are inside a region where they MAY
    // still touch _gate / _poolCts / _blockingIdle. Incremented BEFORE the first touch and
    // decremented AFTER the last one, so TryFinishDisposal can PROVE nothing holds those primitives
    // before it releases them. Without it, disposal is decided on counters that are raised too late:
    //
    //  • _inFlight is incremented only once WaitAsync has GRANTED a permit. A leaf that has taken
    //    the permit but has not yet reached the increment is invisible — Dispose sees zero, disposes
    //    _gate, and the resumed leaf calls Release() on it (#2146, the acquire-side window left open
    //    by #2141).
    //  • Nothing at all covered reading `_poolCts.Token` at SUBSCRIBE time. Every entry point is a
    //    COLD observable, so a leaf minted while the pool was alive and subscribed after it died
    //    threw ObjectDisposedException out of a reactive chain during teardown — the exact class
    //    Cancelled<T>() exists to keep out of teardown (#2134/#2135).
    //  • Nor the _blockingIdle.Reset() a blocking leaf runs when the limited-concurrency scheduler
    //    finally grants it a slot, which can be long after its subscribe.
    private int _gateUsers;

    private volatile bool _disposed;
    // 🚨 Drain is TERMINAL from its FIRST line, not from the cancel that comes after the grace.
    // A leaf issued once the drain has begun is refused outright (Cancelled<T>(), exactly like a
    // leaf issued after disposal); only work admitted BEFORE the drain gets the grace. Without
    // this a producer that issues one leaf per completion keeps the admission count moving and
    // the grace open indefinitely (Copilot review, #3291).
    private volatile bool _draining;
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
    //
    // 🚨 THE DEFAULT IS THE CONTRACT; THE FIELD EXISTS SO A TEST NEED NOT SPEND IT.
    // A residual can only be observed by letting this budget expire, and Drain() spends it in
    // THREE places (the cancel join, each gate slot, then the blocking-idle join). A test that
    // pins the residual diagnostic therefore costs 30-90 s of wall clock — and cannot pass at all
    // under test/xunit.runner.json's `methodTimeout: 30000`, which is how
    // IoPoolResidualNamesItsPoolTest came to be killed at exactly 30 s with no assertion message.
    // The SUBJECT of that test is "the residual is reported and NAMES its pool", not the numeric
    // value below, so the budget is injectable and the default is asserted separately.
    internal static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(30);

    private readonly TimeSpan _drainTimeout;

    /// <summary>
    /// The GRACE <see cref="Drain"/> gives in-flight work before it cancels anything: how long the
    /// pool waits for the NEXT leaf to finish on its own. A leaf that is making progress is never
    /// cancelled — every completion restarts the clock, so a burst of ten short writes drains in
    /// ten completions, not one budget. A leaf that has not completed within one grace with the
    /// pool otherwise idle is WEDGED: it is then cancelled, joined under <see cref="DefaultDrainTimeout"/>,
    /// and named in <see cref="CancelledLeafSites"/>. Matches the hub's disposal stall budget so
    /// "no progress" means the same thing on both sides of the I/O boundary.
    /// </summary>
    internal static readonly TimeSpan DefaultDrainGrace = TimeSpan.FromSeconds(8);
    private readonly TimeSpan _drainGrace;

    /// <summary>
    /// Creates a pool whose async gate and sync-blocking scheduler are both capped at
    /// <paramref name="maxConcurrency"/>.
    /// </summary>
    /// <param name="maxConcurrency">Maximum number of operations allowed to run concurrently; must be at least 1.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxConcurrency"/> is less than 1.</exception>
    public IoPool(int maxConcurrency) : this(maxConcurrency, DefaultDrainTimeout) { }

    /// <summary>
    /// As <see cref="IoPool(int)"/>, with an explicit teardown drain budget.
    /// </summary>
    /// <param name="maxConcurrency">Maximum number of operations allowed to run concurrently; must be at least 1.</param>
    /// <param name="drainTimeout">
    /// How long <see cref="Drain"/> waits at each of its joins before reporting a residual.
    /// 🚨 Production must use <see cref="DefaultDrainTimeout"/> — this overload exists so a test
    /// that has to let the budget EXPIRE (the only way to observe a residual) can do so in
    /// milliseconds instead of spending 30 s of shard time per join.
    /// </param>
    public IoPool(int maxConcurrency, TimeSpan drainTimeout) : this(maxConcurrency, drainTimeout, DefaultDrainGrace) { }

    /// <summary>
    /// As <see cref="IoPool(int, TimeSpan)"/>, with an explicit drain grace.
    /// </summary>
    /// <param name="maxConcurrency">Maximum number of operations allowed to run concurrently; must be at least 1.</param>
    /// <param name="drainTimeout">How long <see cref="Drain"/> waits at each of its joins AFTER cancelling.</param>
    /// <param name="drainGrace">
    /// How long <see cref="Drain"/> waits for the next in-flight leaf to finish on its own BEFORE
    /// cancelling — see <see cref="DefaultDrainGrace"/>. 🚨 Production uses the default; this
    /// overload exists so a test whose leaf can only end by cancellation need not spend the grace.
    /// </param>
    public IoPool(int maxConcurrency, TimeSpan drainTimeout, TimeSpan drainGrace)
    {
        if (maxConcurrency < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        if (drainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(drainTimeout));
        if (drainGrace < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(drainGrace));
        _drainTimeout = drainTimeout;
        _drainGrace = drainGrace;
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
    private const string DisposedMessage =
        "The I/O pool has been disposed (mesh or silo teardown) — this leaf will not run.";

    private static IObservable<T> Cancelled<T>() =>
        Observable.Throw<T>(new OperationCanceledException(DisposedMessage));

    /// <summary>
    /// Opens the region in which the caller may touch <see cref="_gate"/>, <see cref="_poolCts"/> or
    /// <see cref="_blockingIdle"/>. Returns <c>false</c> once disposal has begun — the caller must
    /// then touch NONE of them and answer with a cancellation (see <see cref="Cancelled{T}"/>).
    ///
    /// <para>🚨 Publish-then-recheck, and the order is the whole point. The increment is a full
    /// fence published BEFORE <c>_disposing</c> is read; <see cref="Dispose"/> publishes
    /// <c>_disposing</c> (also a full fence) before it reads the counter. So of the two check-then-act
    /// orders at least one side always sees the other: either disposal DEFERS because this caller is
    /// counted, or this caller REFUSES because disposal has begun. Never both blind — which is
    /// precisely the hole a plain <c>if (_disposed)</c> at the top of the leaf leaves open, since
    /// that flag is read once when the cold observable is BUILT and the primitives are touched later,
    /// on subscribe.</para>
    /// </summary>
    private bool TryEnterGateRegion()
    {
        Interlocked.Increment(ref _gateUsers);
        if (Volatile.Read(ref _disposing) == 0)
            return true;
        LeaveGateRegion();
        return false;
    }

    /// <summary>
    /// Closes the region opened by <see cref="TryEnterGateRegion"/> and completes disposal if this
    /// was the last holder. Always call it from a <c>finally</c>: a leaked region would leave
    /// <see cref="Disposed"/> never firing, which the caller's bounded wait reports as the hang it is.
    /// </summary>
    private void LeaveGateRegion()
    {
        Interlocked.Decrement(ref _gateUsers);
        TryFinishDisposal();
    }

    /// <summary>Number of operations currently executing through this pool.</summary>
    public int CurrentInFlight => Volatile.Read(ref _inFlight);

    // 🚨 WHICH leaf, not just how many. The residual a dirty teardown reports has been an
    // anonymous "AgentStore=1" — enough to know a pool did not drain, never enough to fix it.
    // #2480 added the POOL NAME for exactly this reason and stopped one level short: measured
    // 2026-08-30 on MeshWeaver.AI.Test under load, 3 of 20 runs ended in a dirty teardown naming
    // AgentStore, Query and FileSystem on different runs — three pools, no leaf, nothing to act on.
    //
    // The delegate is kept BY REFERENCE while the leaf is in flight (no stack capture, no string
    // built on the hot path) and formatted only when a residual is reported, so a pool that drains
    // cleanly pays one dictionary insert and one removal per leaf and nothing else.
    private readonly ConcurrentDictionary<long, object> _inFlightLeaves = new();
    private long _leafSeq;

    /// <summary>
    /// Records an in-flight leaf so a residual can NAME it; returns its ticket. Takes the work
    /// ITSELF — a delegate on the three functional entry points, the source observable on
    /// <see cref="SubscribeThroughPool{T}"/>, which has no delegate to name.
    /// </summary>
    private long EnterLeaf(object io)
    {
        var id = Interlocked.Increment(ref _leafSeq);
        _inFlightLeaves[id] = io;
        return id;
    }

    /// <summary>Retires a leaf's ticket. Safe to call for an id that was never added.</summary>
    private void LeaveLeaf(long id) => _inFlightLeaves.TryRemove(id, out _);

    /// <summary>
    /// The call sites of the leaves still in flight, most useful form first: a lambda's
    /// compiler-generated method name carries its ENCLOSING method, so
    /// <c>MeshQuery+&lt;&gt;c__DisplayClass22_0.&lt;MergeProviderObservables&gt;b__1</c> points at the
    /// exact operation that did not unwind. Empty when the pool drained clean.
    /// </summary>
    internal IReadOnlyList<string> PendingLeafSites
    {
        get
        {
            var leaves = _inFlightLeaves.Values
                .Select(d =>
                {
                    try
                    {
                        // A delegate names its enclosing method through the compiler-generated
                        // lambda; anything else (the subscribe path's observable) can only offer
                        // its type, which still says WHICH chain is stuck.
                        if (d is Delegate del)
                        {
                            var m = del.Method;
                            return $"{m.DeclaringType?.FullName ?? "?"}.{m.Name}";
                        }
                        return d.GetType().FullName ?? d.GetType().Name;
                    }
                    catch
                    {
                        // A diagnostic must never be the reason a teardown fails.
                        return "(unavailable)";
                    }
                })
                .Distinct(StringComparer.Ordinal);
            // The third residual Drain() can report is not a leaf and registers no site of its own:
            // a cancel that never returned. Label it, or the residual reads as an anonymous count —
            // the shape that misdirected #2598 (see Drain).
            return _cancelJoinExpired
                ? leaves.Prepend(CancelJoinResidualSite).ToArray()
                : leaves.ToArray();
        }
    }

    /// <summary>
    /// The site <see cref="PendingLeafSites"/> reports when <see cref="Drain"/>'s CANCEL join expired:
    /// <c>_poolCts.Cancel()</c> did not return within the budget because a registered teardown
    /// callback is parked. Every leaf names itself on entry; this residual is not a leaf, so it needs
    /// a name of its own — <c>Query=1</c> with no site is exactly what it used to look like.
    /// </summary>
    internal const string CancelJoinResidualSite =
        "IoPool.Drain: the pool token's cancel did not return within the budget — a registered teardown "
        + "callback is parked (a subscriber's OnCompleted or unsubscribe waiting on a lock the callback "
        + "holds; see Doc/Architecture/ControlledIoPooling → 'A residual with NO site is the CANCEL join')";

    // Set by Drain() when its cancel join expires; read by PendingLeafSites, which the registry
    // consults right after Drain() returns. Never cleared — Drain is terminal.
    private volatile bool _cancelJoinExpired;

    // What Drain()'s GRACE could not wait out: the leaves that were still running when the pool
    // stopped making progress and had to be cancelled. Distinct from the residual (leaves that
    // ignored even the cancel): these unwound, but only because they were killed — each one is a
    // unit of work that did not finish its job, and the report names it so the owner can see why.
    private int _leavesCancelledAfterGrace;
    private volatile IReadOnlyList<string> _cancelledLeafSites = [];

    /// <summary>
    /// Leaves <see cref="Drain"/> had to CANCEL because they outlived the grace with the pool making
    /// no further progress — the wedged work the drain killed, as opposed to the residual it could
    /// not even kill. Zero when every leaf finished on its own.
    /// </summary>
    public int LeavesCancelledAfterGrace => Volatile.Read(ref _leavesCancelledAfterGrace);

    /// <summary>The call sites of the leaves counted by <see cref="LeavesCancelledAfterGrace"/>.</summary>
    public IReadOnlyList<string> CancelledLeafSites => _cancelledLeafSites;

    /// <summary>
    /// Runs an async I/O leaf off the calling scheduler under the pool's concurrency gate.
    /// </summary>
    /// <typeparam name="T">Result type produced by the I/O operation.</typeparam>
    /// <param name="io">The cancellable async work to run once the gate grants a slot.</param>
    /// <returns>A cold observable that, on subscribe, runs the work and emits its single result.</returns>
    public IObservable<T> Invoke<T>(Func<CancellationToken, Task<T>> io)
        => (_disposed || _draining) ? Cancelled<T>() : InvokeCore(io);

    private IObservable<T> InvokeCore<T>(Func<CancellationToken, Task<T>> io)
        // SubscribeOn moves the whole subscribe — including the gate wait and the
        // synchronous prologue of `io` — onto a ThreadPool thread, so the work
        // never runs on the calling hub/grain scheduler. (FromAsync's own
        // scheduler arg only affects notification delivery, not where the
        // function is invoked — hence the SubscribeOn, matching MeshQuery.)
        => Observable.FromAsync(async subscriberCt =>
        {
            // 🚨 The region opens BEFORE the first touch of _poolCts/_gate — see TryEnterGateRegion.
            // The `_disposed` fast path above ran when this cold observable was BUILT; disposal can
            // land in the whole interval between that and this subscribe.
            if (!TryEnterGateRegion())
                throw new OperationCanceledException(DisposedMessage);
            try
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
                var leaf = EnterLeaf(io);
                try
                {
                    return await io(ct).ConfigureAwait(false);
                }
                finally
                {
                    LeaveLeaf(leaf);
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
                    ReleaseGate();
                }
            }
            finally
            {
                LeaveGateRegion();
            }
        }).SubscribeOn(TaskPoolScheduler.Default);

    /// <summary>
    /// The exit path every gated leaf shares: hand the permit back while the gate is still alive,
    /// then account for the leaf. Disposal is completed by <see cref="LeaveGateRegion"/>, which runs
    /// once the leaf can no longer touch <see cref="_gate"/> at all.
    ///
    /// <para>Extracted so the ordering exists in ONE place. It was duplicated at three call sites
    /// and wrong at all three, which is what made <see cref="Dispose"/>'s careful "release the
    /// resources on the last leaf's way out" design throw on that very last leaf.</para>
    /// </summary>
    private void ReleaseGate()
    {
        _gate.Release();
        Interlocked.Decrement(ref _inFlight);
    }

    /// <summary>
    /// Streams an async-enumerable I/O leaf off the calling scheduler under the pool's concurrency gate.
    /// </summary>
    /// <typeparam name="T">Element type produced by the stream.</typeparam>
    /// <param name="source">The cancellable async sequence to enumerate once the gate grants a slot.</param>
    /// <returns>A cold observable that, on subscribe, enumerates the source and emits each element.</returns>
    public IObservable<T> InvokeStream<T>(Func<CancellationToken, IAsyncEnumerable<T>> source)
        => (_disposed || _draining) ? Cancelled<T>() : InvokeStreamCore(source);

    private IObservable<T> InvokeStreamCore<T>(Func<CancellationToken, IAsyncEnumerable<T>> source)
        => Observable.Create<T>(async (observer, subscriberCt) =>
        {
            if (!TryEnterGateRegion())
                throw new OperationCanceledException(DisposedMessage);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(subscriberCt, _poolCts.Token);
                var ct = linked.Token;
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                Interlocked.Increment(ref _inFlight);
                var leaf = EnterLeaf(source);
                try
                {
                    await foreach (var item in source(ct).WithCancellation(ct).ConfigureAwait(false))
                        observer.OnNext(item);
                    observer.OnCompleted();
                }
                finally
                {
                    LeaveLeaf(leaf);
                    ReleaseGate();
                }
            }
            finally
            {
                LeaveGateRegion();
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
        => (_disposed || _draining) ? Cancelled<T>() : InvokeBlockingCore(work);

    private IObservable<T> InvokeBlockingCore<T>(Func<CancellationToken, T> work)
        => Observable.Create<T>(observer =>
        {
            // 🚨 The region spans the WHOLE leaf, not just this subscribe: _poolCts.Token is read
            // here, but _blockingIdle is touched much later — when the limited-concurrency scheduler
            // finally grants the slot. Both must find their primitive alive, so the region is closed
            // by the ContinueWith below (which runs whether the work ran, faulted or never started).
            if (!TryEnterGateRegion())
            {
                observer.OnError(new OperationCanceledException(DisposedMessage));
                return Disposable.Empty;
            }

            // Exactly-once region exit. The region is normally closed by the ContinueWith, which is
            // NOT reached if the scheduling below throws — and a leaked region would park disposal
            // forever (Disposed never fires), so the failure path must close it too, without ever
            // double-closing: a negative count is a wedge, not a warning.
            var regionLeft = 0;
            void LeaveRegionOnce()
            {
                if (Interlocked.Exchange(ref regionLeft, 1) == 0)
                    LeaveGateRegion();
            }

            try
            {
                // Linked to the pool token so Drain()/Dispose() cancels blocking work too.
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_poolCts.Token);
                _blockingFactory.StartNew(() =>
                    {
                        // _inFlight increments only once the scheduler grants a slot —
                        // so CurrentInFlight reflects actually-running blocking work,
                        // capped at the scheduler's MaximumConcurrencyLevel.
                        Interlocked.Increment(ref _inFlight);
                        var blockingLeaf = EnterLeaf(work);
                        if (Interlocked.Increment(ref _blockingInFlight) == 1)
                            _blockingIdle.Reset();
                        try
                        {
                            return work(cts.Token);
                        }
                        finally
                        {
                            LeaveLeaf(blockingLeaf);
                            // 🚨 ORDER MATTERS: the signal is set LAST. Drain() wakes on _blockingIdle, so
                            // anything it releases must already observe fully-updated counters — setting
                            // the signal before decrementing _inFlight let Drain return while
                            // CurrentInFlight was still 1, which is both a false "no pool thread is
                            // running" and a flake in this file's own assertion. (Copilot review, #1338.)
                            Interlocked.Decrement(ref _inFlight);
                            if (Interlocked.Decrement(ref _blockingInFlight) == 0)
                                _blockingIdle.Set();
                            // Disposal is completed by the ContinueWith's LeaveGateRegion, which runs
                            // strictly after this finally — so it sees both counters already at zero.
                            // (It used to be attempted HERE, and had to sit after both decrements for
                            // the same reason: a blocking leaf holds _blockingInFlight as well as
                            // _inFlight, so a call between them always saw a non-zero count, returned
                            // early, and left nothing to retry — Disposed never fired for a pool whose
                            // last leaf was a blocking one.)
                        }
                    }, cts.Token)
                    .ContinueWith(t =>
                    {
                        try
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
                        }
                        finally
                        {
                            // The leaf can no longer touch _blockingIdle / _poolCts — release the region
                            // so a pending disposal can complete.
                            LeaveRegionOnce();
                        }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                return Disposable.Create(() =>
                {
                    // 🚨 No catch. The old `catch { /* already disposed */ }` guarded a case that
                    // cannot occur — `cts` is this subscription's own linked source, disposed
                    // nowhere but the finally below, and Disposable.Create runs its action at most
                    // once — while silently swallowing the case that CAN: Cancel() throws an
                    // AggregateException when a registered cancellation callback faults, and that is
                    // a real defect in the pooled work, not teardown noise. Same call as the two
                    // ObjectDisposedException catches this change deletes: a catch for the
                    // impossible only hides the possible.
                    try { cts.Cancel(); }
                    finally { cts.Dispose(); }
                });
            }
            catch
            {
                LeaveRegionOnce();
                throw;
            }
        });

    /// <inheritdoc />
    public IObservable<T> SubscribeThroughPool<T>(IObservable<T> source) =>
        (_disposed || _draining) ? Cancelled<T>() : SubscribeThroughPoolCore(source);

    private IObservable<T> SubscribeThroughPoolCore<T>(IObservable<T> source) =>
        Observable.Create<T>(observer =>
        {
            // 🚨 This subscribe itself reads _poolCts.Token (the drain registration at the bottom),
            // so it needs a region of its own — the setup leaf's region below opens too late. Every
            // entry point here is a COLD observable: `_disposed` was read when it was BUILT, and the
            // pool can die in the interval before anyone subscribes.
            if (!TryEnterGateRegion())
            {
                observer.OnError(new OperationCanceledException(DisposedMessage));
                return Disposable.Empty;
            }

            try
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
                        if (!TryEnterGateRegion())
                            throw new OperationCanceledException(DisposedMessage);
                        try
                        {
                            using var linked = CancellationTokenSource.CreateLinkedTokenSource(subscriberCt, _poolCts.Token);
                            var ct = linked.Token;
                            await _gate.WaitAsync(ct).ConfigureAwait(false);
                            Interlocked.Increment(ref _inFlight);
                            var subLeaf = EnterLeaf(source);
                            try
                            {
                                // Draining before we even subscribed → do not open providers / create hubs.
                                ct.ThrowIfCancellationRequested();
                                inner.Disposable = source.Subscribe(observer);
                            }
                            finally
                            {
                                LeaveLeaf(subLeaf);
                                ReleaseGate();
                            }
                        }
                        finally
                        {
                            LeaveGateRegion();
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
                // 🚨 No `catch (ObjectDisposedException)` here any more. That catch was the band-aid for
                // exactly the window the region now closes: _poolCts cannot be disposed while this
                // subscribe holds a region, so reading its Token is safe by construction. Catching it
                // would only hide a region that was never entered.
                var drainReg = _poolCts.Token.Register(() =>
                {
                    if (Interlocked.Exchange(ref drainTerminated, 1) != 0) return;
                    inner.Dispose();
                    observer.OnCompleted();
                });

                // 🚨 UNREGISTER, NEVER DISPOSE, the drain registration from the subscriber's side.
                //
                // CancellationTokenRegistration.Dispose() BLOCKS until a callback that is executing on
                // another thread has finished (WaitForCallbackIfNecessary; only the callback's own
                // thread is exempt). Unregister() never waits. The callback above is the subscriber's
                // downstream teardown run inline on the IoPool-cancel thread — and Rx operators
                // forward from their timers UNDER THEIR GATE: Throttle.Propagate calls ForwardOnNext
                // inside `lock (_gate)`, Throttle.OnCompleted takes the same gate, and Take(1)
                // completes and disposes upstream synchronously, still inside it. So a consumer
                // shaped `Query(...).Throttle(1 s).Take(1)` whose timer fired as the drain cancelled
                // held its operator gate while `.Dispose()` here waited for the drain callback, and
                // the drain callback waited in Throttle.OnCompleted for that gate. Two locks, two
                // threads, no exit: _poolCts.Cancel() never returned, Drain() reported the cancel
                // residual after its whole budget (`pools=[Query=1]`, no site, RSS flat — parked, not
                // computing), and the pair stayed deadlocked for the life of the process.
                // MeshNodeLanguageServiceTest went DIRTY that way on 2026-08-28 (#2578, #2616), 08-30
                // and 09-03 (Plugins #1260, attempt 1, shard 3): the body PASSED in ~1 s and the
                // 1 s Throttle of CompletionUsageIndex.EnsureFresh() landed on the drain. Pinned by
                // IoPoolDrainCancelJoinTest.
                //
                // Unregistering is exactly right for both orders: a callback that has not started is
                // removed (the consumer left, nothing to terminate); one that IS running finishes on
                // its own — `inner.Dispose()` is idempotent and the observer's OnCompleted is
                // exactly-once through Rx's AutoDetachObserver — and nobody waits for it.
                return new CompositeDisposable(setup, inner, Disposable.Create(() => drainReg.Unregister()));
            }
            finally
            {
                // The synchronous subscribe is done — it cannot touch _poolCts again. The
                // registration deliberately OUTLIVES this region: Dispose cancels the token while
                // holding a region of its own, so the callback has already run before anything
                // disposes the source, and disposing a registration whose source is gone is a
                // no-op. Holding the region for the whole SUBSCRIPTION instead would park
                // disposal behind every long-lived change feed routed through this pool.
                LeaveGateRegion();
            }
        });

    /// <summary>
    /// Lets every in-flight leaf FINISH (the grace — see <see cref="DefaultDrainGrace"/>), then
    /// cancels whatever stopped making progress and JOINS — blocks until everything has unwound —
    /// WITHOUT disposing the pool. Called before a collectible node <c>AssemblyLoadContext</c> is unloaded
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
    /// separately, PLUS one for a cancellation that is still running its registered teardown
    /// callbacks (see <see cref="StartCancelOffCallerThread"/>). <c>0</c> means the join is REAL:
    /// no pool thread is still running when this returns. Anything else means teardown is about to
    /// proceed over live work (the use-after-unload SIGSEGV precondition) — the caller must surface
    /// it, never swallow it: a drain that silently gives up is how "disposal completed" becomes a
    /// lie and the crash moves 8&#160;ms into the next test's INIT where nothing can attribute it.
    /// </returns>
    public int Drain()
    {
        // 🚨 A REGION, not `if (_disposed)`. Drain touches _poolCts, _gate AND _blockingIdle for as
        // long as the join runs — reading a flag and then touching them is the same check-then-act
        // that produced #2146 on the leaf side: disposal could complete in the gap and pull all
        // three out from under this join. Refused once disposal has begun, which is the same answer
        // the flag gave (disposal cancels and reports its own residual through Disposed) — only now
        // it is a claim rather than a guess.
        if (!TryEnterGateRegion()) return 0;
        // Terminal from here: nothing issued after this line is admitted (see _draining).
        _draining = true;
        try
        {
            // 🚨 GRACE FIRST — let the work finish its job. Nothing is cancelled until the pool has
            // provably stopped making progress. "Work" is every caller admitted to the pool —
            // running leaves, leaves still QUEUED on the gate, blocking leaves on their scheduler —
            // which is exactly what the admission counter (_gateUsers) counts, minus this drain's own
            // region. The wait is progress-based: every time that count drops (a leaf finished, or a
            // queued one ran and finished) the clock restarts, so a burst of short leaves drains in as
            // many completions however long that takes in total. Only when a whole grace passes with
            // NOTHING finishing is what remains wedged — and only then does the cancel below run.
            //
            // Not the gate itself: re-acquiring permits here would compete with the queued leaves for
            // them and steal their turn, then cancel them at the gate — accepted work discarded, which
            // is the very thing the grace exists to prevent. A change-feed subscription holds neither
            // a permit nor a region past its subscribe, so it never holds the grace open; the cancel
            // is what ends it, as it always was — a stream has no job to finish.
            //
            // The predecessor cancelled FIRST and joined second, so every in-flight write, read or
            // compile at teardown was aborted the instant the mesh decided to go down — a flush that
            // would have landed in 50 ms thrown away and its row handed to the sampler. The grace
            // costs nothing on an idle pool and one completion's worth on a busy one.
            var lastSeen = Volatile.Read(ref _gateUsers) - 1;
            while (lastSeen > 0)
            {
                var seen = lastSeen;
                var progressed = SpinWait.SpinUntil(
                    () => Volatile.Read(ref _gateUsers) - 1 < seen,
                    _drainGrace);
                if (!progressed)
                    break; // a whole grace with nothing finishing: what remains is wedged
                lastSeen = Volatile.Read(ref _gateUsers) - 1;
            }
            var wedged = Math.Max(0, Volatile.Read(ref _gateUsers) - 1);
            if (wedged > 0)
            {
                // Name them NOW, while they are provably the ones still in flight: after the
                // cancel a leaf that observed its token has unwound and left no trace. A queued
                // leaf has entered no site yet, so the list can be shorter than the count.
                _cancelledLeafSites = PendingLeafSites;
                Interlocked.Exchange(ref _leavesCancelledAfterGrace, wedged);
            }

            // 🚨 NOT `_poolCts.Cancel()` on this thread — see StartCancelOffCallerThread. The
            // callbacks this token carries run the pooled subscriptions' whole DOWNSTREAM teardown,
            // and this thread is the mesh-teardown thread.
            //
            // Joined BEFORE the gate join, under the same budget, because the gate join's whole
            // meaning depends on the cancel having landed: "once _poolCts is cancelled every waiting
            // leaf's WaitAsync throws, so no NEW leaf can take a permit" (see the remarks above).
            // A cancel still running would leave that premise false. In the healthy case this costs
            // a thread start; a callback that never finishes costs one budget and is then REPORTED
            // in the residual — which is the whole difference between #2394's silent 8-minute
            // wall-clock kill and a named, failing teardown.
            var cancelResidual = StartCancelOffCallerThread().Join(_drainTimeout) ? 0 : 1;
            // 🚨 NAME IT. A cancel that does not return is not a leaf, so it registers no site — and
            // an unlabelled `Query=1` sent two investigations (#2598, then this) into leaves that held
            // no permit. PendingLeafSites carries the label from here on.
            if (cancelResidual != 0)
                _cancelJoinExpired = true;
            var acquired = 0;
            for (var i = 0; i < _maxConcurrency; i++)
            {
                if (_gate.Wait(_drainTimeout)) acquired++;
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
            var blockingResidual = 0;
            if (!_blockingIdle.Wait(_drainTimeout))
            {
                // _blockingInFlight, never _inFlight: an async leaf that ignored its token is ALREADY
                // reported through gateResidual, so adding it again would double-count it.
                blockingResidual = Volatile.Read(ref _blockingInFlight);
            }

            // A teardown callback still running is live application code on the very scope (and the
            // very collectible node ALCs) the caller is about to release, so it belongs in the
            // residual exactly like an un-unwound leaf.
            return cancelResidual + gateResidual + blockingResidual;
        }
        finally
        {
            LeaveGateRegion();
        }
    }

    /// <summary>
    /// Cancels <see cref="_poolCts"/> on a DEDICATED thread and returns that thread so the caller
    /// can join it under its own budget.
    ///
    /// <para>🚨 <b><see cref="CancellationTokenSource.Cancel()"/> runs every registered callback
    /// SYNCHRONOUSLY on the thread that calls it</b>, and the callbacks on this pool's token are
    /// not bookkeeping. <see cref="SubscribeThroughPool{T}"/> registers one per LIVE pooled
    /// subscription that performs that subscription's whole downstream teardown
    /// (<c>inner.Dispose()</c> then <c>observer.OnCompleted()</c>) — layout-render pipelines, query
    /// change feeds, routing dispatch bookkeeping. Every gated leaf additionally links its
    /// subscriber token to this one, so cancelling also resumes each leaf's
    /// <c>await _gate.WaitAsync(ct)</c>, whose <see cref="OperationCanceledException"/> surfaces to
    /// that leaf's observer — more downstream teardown, inline on the same thread.</para>
    ///
    /// <para>The thread calling <see cref="Drain"/>/<see cref="Dispose"/> is the MESH TEARDOWN
    /// thread (<c>IoPoolRegistry.DrainAll()</c>, from <c>MeshTeardownExtensions</c> and
    /// <c>MonolithMeshTestBase.DisposeAsync</c>). Running arbitrary application teardown there was
    /// unbounded by construction: <see cref="DefaultDrainTimeout"/> bounds only the gate join that comes
    /// AFTER the cancel, no watchdog covers this phase (the hub's own watchdog ends at
    /// <c>DisposalCompleted</c>, which teardown has already observed by then), and nothing on the
    /// path logs. One teardown leg that blocks therefore parked mesh teardown SILENTLY and forever
    /// — issue #2394: a whole test assembly killed at its 8&#160;min wall-clock cap with no test
    /// named and not one line written after <c>DISPOSE_INVOKED</c>.</para>
    ///
    /// <para>A DEDICATED thread, never the ThreadPool or this pool's own blocking scheduler: the
    /// work this cancel exists to unwind may be holding every one of those slots, so scheduling the
    /// cancel behind it is the starvation deadlock <see cref="Dispose"/> already refuses.</para>
    /// </summary>
    private Thread StartCancelOffCallerThread()
    {
        // The region is taken HERE, on the caller's thread — not inside the new one — so disposal
        // cannot complete (and dispose _poolCts) in the window between Start() and the thread
        // actually getting scheduled.
        Interlocked.Increment(ref _gateUsers);
        var canceller = new Thread(() =>
        {
            try
            {
                _poolCts.Cancel();
            }
            catch (Exception)
            {
                // Cancel() aggregates whatever the registered teardown callbacks threw. Each of
                // those callbacks is responsible for its own diagnostics; what must not happen is
                // this thread dying before the finally hands the region back, because that hand-back
                // is what completes disposal.
            }
            finally
            {
                LeaveGateRegion();
            }
        })
        {
            IsBackground = true,
            Name = "IoPool-cancel",
        };
        canceller.Start();
        return canceller;
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
        // So: cancel (leaves unwind promptly), and put the WAITING on `Disposed`, which the
        // caller awaits ASYNCHRONOUSLY. Resource release happens on the last leaf's way out —
        // see TryFinishDisposal — because a leaf still running would otherwise touch a disposed
        // _gate / _poolCts.
        //
        // 🚨 …and the cancel is issued OFF this thread (StartCancelOffCallerThread). "Cancel here"
        // used to mean `_poolCts.Cancel()` inline, which is itself a blocking call: Cancel runs
        // every registered callback synchronously on the caller, and this token's callbacks tear
        // down whole downstream pipelines. #2394.
        if (Interlocked.CompareExchange(ref _disposing, 1, 0) != 0) return;

        // Set BEFORE the cancel so a leaf issued in the gap short-circuits to Cancelled<T>()
        // instead of racing the token.
        _disposed = true;

        // 🚨 Dispose takes a region of its own, and it must be taken AFTER the CAS above so that
        // TryEnterGateRegion's publish-then-recheck is sound: _disposing is published first, then
        // this increment, then the counter read inside TryFinishDisposal. Without the region, a leaf
        // finishing between the CAS and the Cancel would take _gateUsers to zero, complete disposal,
        // and dispose _poolCts under this very line — which is why the Cancel used to need a
        // `catch (ObjectDisposedException)`. It no longer does: the region makes the source provably
        // alive, and a swallowed ObjectDisposedException here would only hide the next such hole.
        Interlocked.Increment(ref _gateUsers);
        try
        {
            // 🚨 The cancel itself runs OFF this thread — see StartCancelOffCallerThread. Cancel()
            // executes every pooled subscription's downstream teardown synchronously on whoever
            // calls it, so `_poolCts.Cancel()` here WAS a blocking call in the one method whose
            // contract above says it must never block. Nothing joins it: the WAIT lives on
            // Disposed, and the canceller's own region hand-back is what lets that fire.
            StartCancelOffCallerThread();
        }
        finally
        {
            // Covers the common case too: nothing in flight, so disposal completes right here.
            LeaveGateRegion();
        }
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
        // 🚨 _gateUsers FIRST — it is the only counter raised before the primitives are touched, so
        // it is the only one that can answer "may anyone still touch them?". _inFlight and
        // _blockingInFlight answer the narrower "is anyone RUNNING?", which is what let a leaf
        // holding a permit but not yet counted slip past (#2146).
        if (Volatile.Read(ref _gateUsers) != 0) return;
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
