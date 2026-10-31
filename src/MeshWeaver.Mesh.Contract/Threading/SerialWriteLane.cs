using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// A SERIAL write lane: runs its writes one at a time, in the order they were subscribed, each on
/// a slot of a bounded budget <see cref="IIoPool"/>. Obtained from
/// <see cref="IoPoolRegistry.GetSerialLane"/>, which owns it for the life of the mesh, and
/// reported by <see cref="IoPoolRegistry.SerialLaneSnapshot"/> so a delete's no-progress watchdog
/// can tell a delete WAITING ITS TURN from one that is stuck (MeshWeaver#7464).
///
/// <para><b>Why not an <see cref="IIoPool"/> per lane.</b> Every <see cref="IoPool"/> parks a
/// canceller OS thread for its lifetime (#4448). A storage backend that keeps one lane per partition
/// schema has hundreds of lanes, so the lane is the hub's own serialization shape instead — a
/// synchronized subject drained with <c>Concat</c> — which costs no thread, while the bounded,
/// drainable I/O still runs on the registry-owned budget pool.</para>
///
/// <para>🚨 <b>A write keeps the lane until its I/O has unwound</b>, even when its caller gives up.
/// Disposing a subscription cancels the write's token, which is linked into the leaf (a statement
/// parked on a database lock is cancelled at the server), but the lane runs the next write only
/// after the budget leaf has terminated — otherwise the next write could overlap, or commit before,
/// a write that is still executing.</para>
/// </summary>
public sealed class SerialWriteLane : IDisposable
{
    private readonly ISubject<IObservable<Unit>> _queue = Subject.Synchronize(new Subject<IObservable<Unit>>());
    private readonly IIoPool _budget;
    private readonly ILogger? _logger;

    private int _disposed;
    // Every write's ticket, in subscribe order, and the last ticket a DRAIN still runs (see
    // CloseForDrain): a write subscribed after the drain began is refused at its turn.
    private long _enqueued;
    private long _closedAfter = long.MaxValue;
    // The ticket of the last write whose turn is OVER — run to completion, refused, or skipped.
    // Turns are taken strictly in ticket order (Concat), so this only grows, and "every write up to
    // ticket N is done" is exactly `_finished >= N` — continuously, with no window between one
    // write's dequeue and its admission (which the waiting/in-flight counters had).
    private long _finished;
    private long _admitted;
    private int _waiting;
    private int _waitingDeletes;
    private int _inFlight;
    private int _headIsDelete;

    /// <param name="name">The lane's name, as its readings report it.</param>
    /// <param name="budget">The bounded pool every write of this lane takes a slot of.</param>
    /// <param name="logger">Receives a fault of the drain itself, which no write can see.</param>
    public SerialWriteLane(string name, IIoPool budget, ILogger? logger = null)
    {
        Name = name;
        _budget = budget;
        _logger = logger;
        // Each queued item terminates normally whatever its write did (its outcome is delivered to
        // its own caller, see Run), so the drain never stops on one write's fault.
        // The drain lives as long as the lane: the subject holds it, and Dispose deliberately does not
        // end it (see Dispose).
        _queue.Concat().Subscribe(
            _ => { },
            ex => logger?.LogError(ex,
                "[SerialWriteLane] lane {Lane} faulted; later writes on it get no verdict", name));
    }

    /// <summary>The lane's name.</summary>
    public string Name { get; }

    /// <summary>
    /// Runs <paramref name="io"/> after every write queued on this lane before it, on a slot of the
    /// budget. Cold: the write is queued on subscribe. Disposing the subscription cancels the write
    /// (queued: skipped; running: its token is cancelled) without releasing the lane before the leaf
    /// has unwound.
    /// </summary>
    /// <typeparam name="T">The write's result.</typeparam>
    /// <param name="io">The round-trip.</param>
    /// <param name="isDelete">
    /// Whether this write is a DELETE. The lane reports how many deletes wait and whether the write
    /// in flight is one, so a delete queued behind another kind of write is credited for waiting its
    /// turn (see <see cref="SerialWriteLaneReading"/>).
    /// </param>
    /// <returns>The write's result, or its fault.</returns>
    public IObservable<T> Run<T>(Func<CancellationToken, Task<T>> io, bool isDelete = false)
        => Observable.Create<T>(observer =>
        {
            // 🚨 THE CALLER'S IDENTITY, captured HERE. A queued write is started by Concat from the
            // PREVIOUS write's completion flow, so without this its leaf and its observer would run
            // under whichever AccessContext that flow carried (none, or another caller's). The
            // AccessContext lives in AsyncLocals, which the ExecutionContext carries.
            var context = ExecutionContext.Capture();
            var pending = new PendingWrite(isDelete, Interlocked.Increment(ref _enqueued));
            var cancel = new CancellationTokenSource();
            Interlocked.Increment(ref _waiting);
            if (isDelete)
                Interlocked.Increment(ref _waitingDeletes);

            var item = Observable.Create<Unit>(laneObserver =>
            {
                IDisposable started = Disposable.Empty;
                RunIn(context, () => started = Start(io, observer, pending, cancel, context).Subscribe(laneObserver));
                return started;
            });
            _queue.OnNext(item);

            return Disposable.Create(() =>
            {
                // Given up while QUEUED: leave the live counters now, exactly once, so a queue reading
                // never reports a write that will not run (the dequeue takes the same transition).
                if (pending.TryLeaveQueue())
                    LeaveQueue(isDelete);
                // 🚨 The cancel is raised OFF the disposing thread: cancelling runs the leaf's
                // registered callbacks (a database cancel request) synchronously, and the thread giving
                // up may be a hub turn or a watchdog. The lane is still held until the leaf terminates.
                TaskPoolScheduler.Default.Schedule(() => cancel.Cancel());
            });
        });

    /// <summary>
    /// The write at its turn: skipped when given up or when the lane is disposed (the caller is
    /// told), otherwise run on a slot of the budget with the caller's cancellation linked in, its
    /// outcome delivered to its caller, and the lane released only once the leaf has terminated.
    /// </summary>
    private IObservable<Unit> Start<T>(
        Func<CancellationToken, Task<T>> io, IObserver<T> observer, PendingWrite pending,
        CancellationTokenSource cancel, ExecutionContext? context)
    {
        // Given up while queued: it already left the counters, nothing runs, the lane moves on.
        if (!pending.TryLeaveQueue())
            return Observable.Empty<Unit>().Finally(() => Finish(pending.Ticket));
        LeaveQueue(pending.IsDelete);

        // The lane was disposed (its mesh is tearing down) before this write's turn: the write never
        // runs, and its caller is TOLD so instead of waiting for a verdict nobody sends.
        if (Volatile.Read(ref _disposed) != 0)
            return Refuse(observer, context, pending.Ticket, new ObjectDisposedException(Name,
                $"Write lane '{Name}' was disposed before this write's turn; the write did not run."));
        // Subscribed after a drain began (mesh teardown): refused like any call a draining pool
        // will not admit; the writes queued BEFORE the drain still run (CloseForDrain).
        if (pending.Ticket > Interlocked.Read(ref _closedAfter))
            return Refuse(observer, context, pending.Ticket, new OperationCanceledException(
                $"Write lane '{Name}' is draining; a write subscribed after the drain began does not run."));

        Interlocked.Increment(ref _admitted);
        Volatile.Write(ref _headIsDelete, pending.IsDelete ? 1 : 0);
        Volatile.Write(ref _inFlight, 1);
        CancellationTokenSource? linked = null;
        return _budget.Invoke(budgetCt =>
            {
                // The caller's cancellation linked into the leaf, disposed when the leaf has
                // terminated (Finally below) — no awaited helper outside the pool.
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, budgetCt);
                return io(linked.Token);
            })
            // Deliver the outcome to THIS write's caller, and end the item normally either way, so
            // the next write runs after this leaf terminated — never before.
            .Materialize()
            .Do(notification => Deliver(notification, observer, context))
            .Where(_ => false)
            .Select(_ => Unit.Default)
            .Finally(() =>
            {
                linked?.Dispose();
                Volatile.Write(ref _inFlight, 0);
                Volatile.Write(ref _headIsDelete, 0);
                Finish(pending.Ticket);
            });
    }

    /// <summary>
    /// Answers a write the lane will not run, OFF the subscribing thread: on an idle lane the turn
    /// comes inside the caller's own Subscribe, and delivering the fault there would run the caller's
    /// teardown on its hub turn — the boundary <c>IoPool</c> keeps for its own refusals. The item
    /// completes after the delivery, so the lane moves on normally.
    /// </summary>
    private IObservable<Unit> Refuse<T>(IObserver<T> observer, ExecutionContext? context, long ticket, Exception refusal)
        => Observable.Start(
                () => Deliver(System.Reactive.Notification.CreateOnError<T>(refusal), observer, context),
                TaskPoolScheduler.Default)
            .Finally(() => Finish(ticket));

    private void Finish(long ticket) => Volatile.Write(ref _finished, ticket);

    /// <summary>
    /// Closes the lane to writes subscribed from now on — the first half of the lane's part in a
    /// mesh DRAIN (<see cref="IoPoolRegistry.DrainAll()"/>). Writes already queued keep running in
    /// turn; later ones are refused at theirs. Idempotent.
    /// </summary>
    internal void CloseForDrain()
        => Interlocked.CompareExchange(ref _closedAfter, Interlocked.Read(ref _enqueued), long.MaxValue);

    /// <summary>Whether a drain has closed the lane to writes subscribed from now on.</summary>
    internal bool IsDraining => Interlocked.Read(ref _closedAfter) != long.MaxValue;

    /// <summary>
    /// Whether every write the lane accepted before the drain closed it has had its turn. Late
    /// arrivals do not count: they are refused, and they can neither keep a drain waiting nor make
    /// one look finished early.
    /// </summary>
    internal bool IsDrained => Interlocked.Read(ref _finished) >= Interlocked.Read(ref _closedAfter);

    /// <summary>
    /// The lane's progress mark for a drain: it changes exactly when a write's turn starts or ends —
    /// never because a late write arrived or was given up while queued behind a stuck head.
    /// </summary>
    internal (long Admitted, long Finished) DrainProgress
        => (Interlocked.Read(ref _admitted), Interlocked.Read(ref _finished));


    private void LeaveQueue(bool isDelete)
    {
        Interlocked.Decrement(ref _waiting);
        if (isDelete)
            Interlocked.Decrement(ref _waitingDeletes);
    }

    private static void RunIn(ExecutionContext? context, Action action)
    {
        if (context is null)
            action();
        else
            ExecutionContext.Run(context, static state => ((Action)state!)(), action);
    }

    /// <summary>One write's single transition out of the queue — by its turn or by its caller giving up.</summary>
    private sealed class PendingWrite(bool isDelete, long ticket)
    {
        private int _left;

        public bool IsDelete { get; } = isDelete;

        /// <summary>The write's position in subscribe order.</summary>
        public long Ticket { get; } = ticket;

        /// <summary><c>true</c> exactly once: for whichever of dequeue and give-up comes first.</summary>
        public bool TryLeaveQueue() => Interlocked.Exchange(ref _left, 1) == 0;
    }

    /// <summary>
    /// Hands one write's outcome to its caller. 🚨 Isolated: a caller's observer that throws (a
    /// disposed result subject, a faulting continuation) must not become an error on the lane's
    /// drain — that would stop the drain for good while the registry keeps handing out this lane, and
    /// every later write to it would get no verdict. The throw is the caller's defect and is logged
    /// as such; the lane moves on.
    /// </summary>
    private void Deliver<T>(System.Reactive.Notification<T> notification, IObserver<T> observer, ExecutionContext? context)
    {
        try
        {
            // Under the caller's own identity — see Run.
            RunIn(context, () => notification.Accept(observer));
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "[SerialWriteLane] the caller of a write on lane {Lane} threw while receiving its {Kind}; "
                + "the lane continues with the next write", Name, notification.Kind);
        }
    }

    /// <summary>A point-in-time reading of this lane, from lock-free counters.</summary>
    public SerialWriteLaneReading Read() => new(
        Name,
        Interlocked.Read(ref _admitted),
        Volatile.Read(ref _inFlight),
        Math.Max(0, Volatile.Read(ref _waiting)),
        Math.Max(0, Volatile.Read(ref _waitingDeletes)),
        Volatile.Read(ref _headIsDelete) == 1);

    /// <summary>
    /// Disposes the lane: every write that has not started yet — queued now or subscribed later —
    /// is answered with an <see cref="ObjectDisposedException"/> when its turn comes, instead of
    /// being dropped; a write already running is NOT cut off and still delivers its outcome (its I/O
    /// is a leaf of the budget pool, which the mesh drains and cancels on its own).
    ///
    /// <para>🚨 Deliberately neither completing the queue nor disposing the drain: completing it
    /// would silently ignore a write subscribed a moment later, and disposing the drain would
    /// unsubscribe the running write's delivery and every queued item — callers waiting for a
    /// verdict that no one sends. The flag is the whole admission decision, read by each write at
    /// its turn, so there is no window in which a write is accepted and never answered.</para>
    /// </summary>
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

/// <summary>
/// One <see cref="SerialWriteLane"/> at a point in time.
/// </summary>
/// <param name="Name">The lane's name.</param>
/// <param name="Admitted">Writes the lane has started so far (monotonic).</param>
/// <param name="InFlight">1 while a write is running, else 0.</param>
/// <param name="Waiting">Writes queued behind it.</param>
/// <param name="WaitingDeletes">How many of the queued writes are deletes.</param>
/// <param name="HeadIsDelete">Whether the write in flight is a delete.</param>
public readonly record struct SerialWriteLaneReading(
    string Name, long Admitted, int InFlight, int Waiting, int WaitingDeletes, bool HeadIsDelete);
