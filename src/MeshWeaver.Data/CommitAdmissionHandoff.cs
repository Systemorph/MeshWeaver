using System;
using System.Threading;

namespace MeshWeaver.Data;

/// <summary>
/// Carries ONE cross-hub patch's admission against the deletion scopes
/// (<see cref="MeshWeaver.Mesh.IActiveDeletionScopes.AdmitCommit"/>) from the owner's merge turn to
/// the durable flush's storage answer, and decides — atomically against the patch's echo watcher —
/// whether the turn may still COMMIT (Systemorph/MeshWeaver#7346).
///
/// <para>Two parties race on it: the merge turn (on the owner's primary executor) and the echo
/// watcher, whose bound may expire — or whose owner may go away — while the turn is queued or running.
/// The watcher is the ONLY route to the flush, so a turn whose watcher has ended must not commit:
/// nothing would store the write, the writer has already been answered, and the admission would have
/// to be dropped while the commit still lands — a delete opening its scope then would find nothing to
/// wait for. Conversely the watcher must not release an admission while a turn is DECIDING, or it
/// releases it for a turn that still commits.</para>
///
/// <para>So the commit decision is one compare-and-swap: the turn moves <c>Deciding → Committed</c>
/// immediately before it returns its change, and the watcher's end moves any phase to <c>Ended</c>.
/// Whichever lands first wins: an ended watcher makes the turn abort (<see cref="TryCommit"/> false),
/// and a committed turn hands the admission to the flush (<see cref="TakeForFlush"/>). A watcher that
/// ends while a turn is deciding leaves the admission to that turn, which releases it as it aborts.
/// Lock-free: no turn ever waits on the other party.</para>
/// </summary>
internal sealed class CommitAdmissionHandoff
{
    private const int Idle = 0;
    private const int Deciding = 1;
    private const int Committed = 2;
    private const int Ended = 3;

    private int _phase = Idle;
    private IDisposable? _admission;

    /// <summary>True once the echo watcher has ended — no flush will follow any later commit.</summary>
    public bool WatcherEnded => Volatile.Read(ref _phase) == Ended;

    /// <summary>
    /// The merge turn takes the admission and starts deciding. False — with the admission released —
    /// when the echo watcher has already ended: the turn must then commit nothing.
    /// </summary>
    /// <param name="admission">The admission the turn was granted (null when the host has no scopes).</param>
    /// <returns><c>true</c> when the turn may go on towards its commit.</returns>
    public bool TryBeginDecision(IDisposable? admission)
    {
        Interlocked.Exchange(ref _admission, admission)?.Dispose();
        if (Interlocked.CompareExchange(ref _phase, Deciding, Idle) == Idle)
            return true;
        Release();
        return false;
    }

    /// <summary>
    /// The turn's commit point. True — the admission stays for the flush — when the watcher has not
    /// ended; false — the admission released — when it has, and the turn must then return no change.
    /// </summary>
    /// <returns><c>true</c> when the turn may commit.</returns>
    public bool TryCommit()
    {
        if (Interlocked.CompareExchange(ref _phase, Committed, Deciding) == Deciding)
            return true;
        Release();
        return false;
    }

    /// <summary>
    /// The turn commits nothing after all — it was refused, changed nothing, faulted, or its commit
    /// failed to apply after <see cref="TryCommit"/>: the admission is released (nothing will be
    /// flushed for a deletion to wait on) and a later turn may decide again.
    /// </summary>
    public void Abandon()
    {
        Release();
        if (Interlocked.CompareExchange(ref _phase, Idle, Deciding) != Deciding)
            Interlocked.CompareExchange(ref _phase, Idle, Committed);
    }

    /// <summary>The flush takes the admission over, to release it on its own storage answer.</summary>
    /// <returns>The admission, or null when there is none (any more).</returns>
    public IDisposable? TakeForFlush() => Interlocked.Exchange(ref _admission, null);

    /// <summary>
    /// The echo watcher ended. Releases the admission unless a turn is still deciding — that turn
    /// will see the end at its commit point, abort, and release it itself.
    /// </summary>
    public void OnWatcherEnded()
    {
        if (Interlocked.Exchange(ref _phase, Ended) != Deciding)
            Release();
    }

    private void Release() => Interlocked.Exchange(ref _admission, null)?.Dispose();
}
