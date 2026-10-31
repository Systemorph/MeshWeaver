using System.Reactive;
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
    private readonly IDisposable _drain;
    private readonly IIoPool _budget;

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
        // Each queued item terminates normally whatever its write did (its outcome is delivered to
        // its own caller, see Run), so the drain never stops on one write's fault.
        _drain = _queue.Concat().Subscribe(
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
            // Rx's CancellationDisposable: disposing it cancels the token, idempotently.
            var cancel = new CancellationDisposable();
            var item = Observable.Defer(() =>
            {
                Interlocked.Decrement(ref _waiting);
                if (isDelete)
                    Interlocked.Decrement(ref _waitingDeletes);
                // Given up while queued: nothing runs, so the lane moves straight on.
                if (cancel.IsDisposed)
                    return Observable.Empty<Unit>();
                Interlocked.Increment(ref _admitted);
                Volatile.Write(ref _headIsDelete, isDelete ? 1 : 0);
                Volatile.Write(ref _inFlight, 1);
                return _budget.Invoke(budgetCt => RunLinked(io, cancel.Token, budgetCt))
                    // Deliver the outcome to THIS write's caller, and end the item normally either
                    // way, so the next write runs after this leaf terminated — never before.
                    .Materialize()
                    .Do(notification => notification.Accept(observer))
                    .Where(_ => false)
                    .Select(_ => Unit.Default)
                    .Finally(() =>
                    {
                        Volatile.Write(ref _inFlight, 0);
                        Volatile.Write(ref _headIsDelete, 0);
                    });
            });
            Interlocked.Increment(ref _waiting);
            if (isDelete)
                Interlocked.Increment(ref _waitingDeletes);
            _queue.OnNext(item);
            return cancel;
        });

    /// <summary>A point-in-time reading of this lane, from lock-free counters.</summary>
    public SerialWriteLaneReading Read() => new(
        Name,
        Interlocked.Read(ref _admitted),
        Volatile.Read(ref _inFlight),
        Math.Max(0, Volatile.Read(ref _waiting)),
        Math.Max(0, Volatile.Read(ref _waitingDeletes)),
        Volatile.Read(ref _headIsDelete) == 1);

    private static async Task<T> RunLinked<T>(
        Func<CancellationToken, Task<T>> io, CancellationToken caller, CancellationToken pool)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, pool);
        return await io(linked.Token).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _queue.OnCompleted();
        _drain.Dispose();
    }
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
