using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;

namespace MeshWeaver.Testing.FaultInjection;

/// <summary>
/// One deterministic fault, CLOSED from the moment it is created until <see cref="Release"/>.
/// Every injector in this library hands one out: while it is closed the fault is in force (a write
/// is held, a path answers NotFound, a feed is paused, an inbox refuses), and releasing it ends the
/// fault exactly once. Nothing about it depends on a clock.
///
/// <para><b>Release it in a <c>finally</c></b> — or with <c>using</c>, since <see cref="Dispose"/>
/// is <see cref="Release"/>. A failing assertion must never strand the work the switch holds, or the
/// fixture's teardown waits on it.</para>
///
/// <para>No thread ever parks on a switch. A held operation is an observable that has not emitted
/// yet: <see cref="Gate{T}"/> composes the source behind <see cref="Released"/> (an
/// <see cref="AsyncSubject{T}"/> the release completes), so the caller's pipeline simply continues
/// when the fault ends. That is what makes the switch legal inside a hub's action block, where a
/// blocking gate would deadlock the message it waits for.</para>
/// </summary>
public sealed class FaultSwitch : IDisposable
{
    private readonly AsyncSubject<Unit> _released = new();
    private readonly ReplaySubject<string> _arrivals = new();
    private readonly Action<FaultSwitch>? _onRelease;
    private readonly int? _limit;
    private int _isReleased;
    private int _arrivalCount;
    private int _applications;
    private int _recorded;

    /// <summary>Creates a closed switch.</summary>
    /// <param name="name">What the fault is, for failure messages ("hold writes of X").</param>
    /// <param name="onRelease">Invoked once, after the switch opens (the owner unregisters it).</param>
    /// <param name="limit">
    /// When set, the fault is applied at most this many times through <see cref="TryApply"/> and the
    /// switch RELEASES ITSELF with the last application — a one-shot fault is <c>limit: 1</c>. The
    /// reservation is atomic, so two operations racing for the last application cannot both be
    /// faulted. <c>null</c> (the default) applies the fault until <see cref="Release"/>.
    /// </param>
    public FaultSwitch(string name, Action<FaultSwitch>? onRelease = null, int? limit = null)
    {
        if (limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "A limited fault applies at least once.");
        Name = name;
        _onRelease = onRelease;
        _limit = limit;
    }

    /// <summary>What this switch injects.</summary>
    public string Name { get; }

    /// <summary>
    /// True while the fault is in force. A limited switch is OPEN the moment its last application is
    /// RESERVED — before that application's arrival is published — so a test that resumes on the
    /// arrival always reads the spent fault as open, whichever thread it resumes on.
    /// </summary>
    public bool IsClosed
        => Volatile.Read(ref _isReleased) == 0
           && (_limit is not { } limit || Volatile.Read(ref _applications) < limit);

    /// <summary>Emits once and completes when the fault ends; completes at once for a released switch.</summary>
    public IObservable<Unit> Released => _released.AsObservable();

    /// <summary>
    /// Each operation the fault met while closed, described (replayed). The positive signal a test
    /// waits on before asserting — "the write reached the hold" — so it never has to guess when the
    /// code under test got there.
    /// </summary>
    public IObservable<string> Arrivals => _arrivals.AsObservable();

    /// <summary>
    /// How many operations met this fault while it was closed — the count a test asserts to prove
    /// the fault was actually INJECTED (a faulted case whose count is zero exercised the healthy path
    /// and cannot fail). It keeps its value after the release.
    /// </summary>
    public int ArrivalCount => Volatile.Read(ref _arrivalCount);

    /// <summary>The most applications <see cref="TryApply"/> grants, or <c>null</c> for "until released".</summary>
    public int? Limit => _limit;

    /// <summary>Records that an operation met this fault. Called by injectors; ignored once released.</summary>
    /// <param name="what">A description of the operation.</param>
    public void NoteArrival(string what)
    {
        if (!IsClosed)
            return;
        Record(what);
    }

    private void Record(string what)
    {
        Interlocked.Increment(ref _arrivalCount);
        _arrivals.OnNext(what);
    }

    /// <summary>
    /// Test seam: runs inside <see cref="TryApply"/> after an application is reserved and before it is
    /// recorded — the window in which a racing caller can spend the limit. Null in every real use.
    /// </summary>
    internal Action? AfterReservation { get; set; }

    /// <summary>
    /// The decision an injector that FAILS an operation (rather than holding it) makes: <c>true</c>
    /// when the fault applies to this operation — it is then noted as an arrival — and <c>false</c>
    /// when the switch is released or its <see cref="Limit"/> is spent. With a limit, the application
    /// that spends it releases the switch, so the next operation runs healthy — but only once EVERY
    /// granted application has been recorded, so <see cref="ArrivalCount"/> and <see cref="Arrivals"/>
    /// never under-report an application that a faster racing caller outran.
    /// </summary>
    /// <param name="what">A description of the operation, for <see cref="Arrivals"/>.</param>
    public bool TryApply(string what)
    {
        if (!IsClosed)
            return false;
        var application = Interlocked.Increment(ref _applications);
        if (_limit is { } limit && application > limit)
            return false;
        AfterReservation?.Invoke();
        // A granted application is recorded unconditionally: IsClosed may already read false (the
        // limit is spent by reservation), but this application WAS applied. The switch releases only
        // when the last granted application has been recorded, never when the last one is reserved.
        Record(what);
        if (_limit is { } spent && Interlocked.Increment(ref _recorded) == spent)
            Release();
        return true;
    }

    /// <summary>
    /// Holds <paramref name="source"/> until the switch opens: subscribed while closed, it notes an
    /// arrival and subscribes the source only after <see cref="Release"/>; subscribed after the
    /// release, it is the source unchanged.
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="source">The operation to hold.</param>
    /// <param name="what">A description of the operation, for <see cref="Arrivals"/>.</param>
    public IObservable<T> Gate<T>(IObservable<T> source, string what)
        => Observable.Defer(() =>
        {
            if (!IsClosed)
                return source;
            NoteArrival(what);
            return _released.SelectMany(_ => source);
        });

    /// <summary>Ends the fault. Idempotent; safe from any thread.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _isReleased, 1) != 0)
            return;
        _released.OnNext(Unit.Default);
        _released.OnCompleted();
        _arrivals.OnCompleted();
        _onRelease?.Invoke(this);
    }

    /// <summary>Same as <see cref="Release"/>, so a <c>using</c> releases on every exit path.</summary>
    public void Dispose() => Release();

    /// <inheritdoc />
    public override string ToString() => $"{Name} ({(IsClosed ? "closed" : "released")})";
}
