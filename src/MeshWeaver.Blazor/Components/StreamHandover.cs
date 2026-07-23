using System.Reactive.Linq;

namespace MeshWeaver.Blazor.Components;

/// <summary>
/// Owns the RETIRING area stream during a <see cref="LayoutAreaView"/> navigation hand-over.
/// <para>
/// When the area reference/address changes, the outgoing stream must NOT be disposed
/// immediately: the previous content stays rendered (keep-last-good) until the NEW stream's
/// first frame arrives, and during that gap its interactive elements (e.g. a slide stage's
/// click-to-advance) still post into the old stream — disposing it up front silently
/// swallowed those clicks. Instead the component hands the outgoing stream to
/// <see cref="BeginTransition"/> and wires <see cref="CompleteOnFirstFrame{T}"/> on the new
/// stream; the retiring stream is disposed exactly when the new content's first frame lands.
/// </para>
/// <para>
/// At most ONE stream is ever retiring: a second transition before any frame supersedes the
/// previous one and disposes it immediately (<see cref="BeginTransition"/>), and a
/// superseded transition's pending first-frame trigger becomes a no-op via the generation
/// counter — so a late frame on an already-retiring stream can never dispose the wrong
/// stream (or itself). <see cref="Dispose"/> releases any still-pending retiring stream on
/// component teardown.
/// </para>
/// <para>
/// The seams are deliberately narrow — <see cref="IDisposable"/> for the streams and
/// <see cref="IObservable{T}"/> for the first-frame signal — so the hand-over protocol is
/// unit-testable without a mesh (see StreamHandoverTest).
/// </para>
/// </summary>
internal sealed class StreamHandover : IDisposable
{
    private readonly object _gate = new();
    private IDisposable? _retiring;
    private int _generation;

    /// <summary>
    /// Begins a transition: takes ownership of <paramref name="outgoing"/> (the stream the
    /// still-visible previous content posts into). Any stream retired by an OLDER, not yet
    /// completed transition is superseded and disposed immediately — at most one stream
    /// retires at a time.
    /// </summary>
    public void BeginTransition(IDisposable? outgoing)
    {
        IDisposable? superseded;
        lock (_gate)
        {
            superseded = _retiring;
            _retiring = outgoing;
            _generation++;
        }
        superseded?.Dispose();
    }

    /// <summary>
    /// Wires the hand-over completion: when <paramref name="newStreamFrames"/> emits its
    /// first item (the new content's first frame — the moment the kept old content is
    /// actually replaced on screen), the currently retiring stream is disposed. ALL three
    /// terminal paths complete the hand-over so the retiring stream is never orphaned:
    /// an error before the first frame, and — critically — a stream that COMPLETES without
    /// ever emitting a frame (otherwise the retiring stream would linger until component
    /// teardown; the fault/empty itself is surfaced by the primary area subscriptions, never
    /// here). <see cref="Complete"/> is idempotent + generation-guarded, so the normal
    /// onNext-then-onCompleted double-fire from <c>Take(1)</c> is harmless. Returns the
    /// trigger subscription; the caller registers it on the NEW stream
    /// (<c>RegisterForDisposal</c>) so it tears down with that stream automatically.
    /// </summary>
    public IDisposable CompleteOnFirstFrame<T>(IObservable<T> newStreamFrames)
    {
        int generation;
        lock (_gate)
            generation = _generation;
        return newStreamFrames
            .Take(1)
            .Subscribe(
                _ => Complete(generation),
                _ => Complete(generation),
                () => Complete(generation));
    }

    private void Complete(int generation)
    {
        IDisposable? retired;
        lock (_gate)
        {
            // A newer transition superseded this one (and already disposed its stream):
            // the stale trigger must not touch the CURRENT retiring stream.
            if (generation != _generation)
                return;
            retired = _retiring;
            _retiring = null;
        }
        retired?.Dispose();
    }

    /// <summary>Disposes any still-pending retiring stream (component teardown).</summary>
    public void Dispose()
    {
        IDisposable? pending;
        lock (_gate)
        {
            pending = _retiring;
            _retiring = null;
            _generation++;
        }
        pending?.Dispose();
    }
}
