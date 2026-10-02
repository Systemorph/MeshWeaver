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
    private int _isReleased;

    /// <summary>Creates a closed switch.</summary>
    /// <param name="name">What the fault is, for failure messages ("hold writes of X").</param>
    /// <param name="onRelease">Invoked once, after the switch opens (the owner unregisters it).</param>
    public FaultSwitch(string name, Action<FaultSwitch>? onRelease = null)
    {
        Name = name;
        _onRelease = onRelease;
    }

    /// <summary>What this switch injects.</summary>
    public string Name { get; }

    /// <summary>True while the fault is in force.</summary>
    public bool IsClosed => Volatile.Read(ref _isReleased) == 0;

    /// <summary>Emits once and completes when the fault ends; completes at once for a released switch.</summary>
    public IObservable<Unit> Released => _released.AsObservable();

    /// <summary>
    /// Each operation the fault met while closed, described (replayed). The positive signal a test
    /// waits on before asserting — "the write reached the hold" — so it never has to guess when the
    /// code under test got there.
    /// </summary>
    public IObservable<string> Arrivals => _arrivals.AsObservable();

    /// <summary>Records that an operation met this fault. Called by injectors; ignored once released.</summary>
    /// <param name="what">A description of the operation.</param>
    public void NoteArrival(string what)
    {
        if (IsClosed)
            _arrivals.OnNext(what);
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
