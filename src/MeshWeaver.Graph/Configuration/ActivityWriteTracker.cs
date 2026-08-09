using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// Keeps the activity-tracking hub's IN-FLIGHT writes visible to its own shutdown.
///
/// <para>🚨 The gap this closes. <c>HandleTrackActivity</c> starts its write and returns
/// <c>delivery.Processed()</c> immediately — deliberately, because <c>TrackLogin</c> sits on the
/// cold-login hot path and must not stall behind a node write. The consequence is that the write is
/// not part of any request: Orleans tracks work on an activation's scheduler <i>inside a request</i>,
/// so once the handler returns there is nothing keeping the activation alive. A detached
/// subscription running on the thread pool is invisible to the runtime — the thread pool is
/// precisely what escapes Orleans' turn-based concurrency, so it can never hold a grain open.</para>
///
/// <para>When deactivation lands in that window, <c>MessageHubGrain.OnDeactivateAsync</c> calls
/// <c>CancelCurrentExecution()</c> and <c>Dispose()</c> on the hub the write is still using. The
/// write dies mid-flight. Nothing reports it as a failure, because from the message layer's point
/// of view the request completed successfully some time ago.</para>
///
/// <para>The fix is not a different scheduler — it is making the work KNOWN. Each write registers
/// here for its lifetime; the tracking hub registers <see cref="Drain"/> as a reactive dispose
/// action, so hub disposal waits for the outstanding writes before completing, and
/// <c>DisposalCompleted</c> — which the grain already awaits, bounded — now accounts for them.</para>
///
/// <para><b>Bounded, and honest about it.</b> Draining is best-effort within
/// <see cref="DrainTimeout"/>. The grain gives hub disposal 5 s total before "moving on", and a
/// deactivation must never hold a silo shutdown open indefinitely, so a drain that overruns logs
/// what it abandoned rather than blocking. This buys the overwhelmingly common case — a write with
/// milliseconds left to run — not a guarantee. A true "no shutdown until activities complete" would
/// have to keep the work inside the request, which is the latency trade the detachment exists to
/// avoid.</para>
/// </summary>
public sealed class ActivityWriteTracker
{
    /// <summary>
    /// How long <see cref="Drain"/> waits for outstanding writes before giving up.
    ///
    /// <para>Deliberately under the grain's own 5 s hub-disposal budget
    /// (<c>MessageHubGrain.OnDeactivateAsync</c>): a drain that outlived it would be cut off by the
    /// grain anyway, and would only cost the OTHER registered dispose actions their share of the
    /// window. Overrunning here should read as "activity writes lost", not as a disposal hang.</para>
    /// </summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(3);

    private readonly object gate = new();

    // Path -> how many writes are outstanding for it. A SET would be wrong: concurrent tracks for
    // the same path are the documented create-vs-update race, `Add` on a set is idempotent, and the
    // first release would then drop the only entry and report drained while the second write was
    // still running. (Caught by ConcurrentWritesToTheSamePath_BothMustFinishBeforeDraining, which
    // failed against exactly that first implementation.)
    private ImmutableDictionary<string, int> inFlight =
        ImmutableDictionary<string, int>.Empty;

    // 🚨 Subject.Synchronize — Rx subjects are NOT thread-safe across concurrent OnNext, and both
    // sides of this one are concurrent by construction: Begin runs on whichever hub handled the
    // request, Release runs on the detached pipeline's thread whenever that write ends. Same
    // reasoning as MeshNodeStreamCache's eviction subject.
    //
    // The subject carries the WHOLE in-flight map, not just its total: Drain needs "is anything
    // running", WhenSettled needs "is THIS path running", and deriving both from one notification
    // keeps a single ordered channel (two subjects could interleave and let a per-path observer
    // see a state the count never reported).
    private readonly BehaviorSubject<ImmutableDictionary<string, int>> stateSubject =
        new(ImmutableDictionary<string, int>.Empty);
    private readonly IObserver<ImmutableDictionary<string, int>> state;
    private readonly IObservable<ImmutableDictionary<string, int>> states;

    /// <summary>Creates an empty tracker.</summary>
    public ActivityWriteTracker()
    {
        state = Observer.Synchronize(stateSubject, preventReentrancy: true);
        states = stateSubject.AsObservable();
    }

    /// <summary>Paths currently being written — what a drain overrun names.</summary>
    public ImmutableHashSet<string> InFlight
    {
        get { lock (gate) return inFlight.Keys.ToImmutableHashSet(); }
    }

    /// <summary>Number of writes currently outstanding — counting each concurrent write to the
    /// same path separately, since each has to finish.</summary>
    public int Count
    {
        get { lock (gate) return inFlight.Values.Sum(); }
    }

    /// <summary>
    /// Marks <paramref name="activityPath"/> as being written, and returns the token that clears it.
    ///
    /// <para>Keyed by path rather than a bare counter so a drain overrun can NAME what was lost —
    /// an abandoned activity write is otherwise invisible. Counted PER PATH rather than held in a
    /// set, because concurrent tracks for the same path are the documented create-vs-update race:
    /// with a set the first release would drop the only entry and report drained while the second
    /// write was still running.</para>
    /// </summary>
    public IDisposable Begin(string activityPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityPath);
        // Emit INSIDE the lock so the notification order matches the state transitions. Emitting
        // outside it would let a Release's "0" overtake a concurrent Begin's "1", and a drain
        // watching for zero would then complete while a write that had just started was still
        // running — the exact loss this class exists to prevent. Safe to hold `gate` across the
        // notification: the observer is synchronized with preventReentrancy, and the subscribers
        // are Drain's and WhenSettled's Where/Take filters, neither of which calls back in here.
        lock (gate)
        {
            inFlight = inFlight.SetItem(
                activityPath, inFlight.TryGetValue(activityPath, out var c) ? c + 1 : 1);
            state.OnNext(inFlight);
        }
        return new Registration(this, activityPath);
    }

    private void Release(string activityPath)
    {
        lock (gate)
        {
            if (inFlight.TryGetValue(activityPath, out var c))
                inFlight = c <= 1 ? inFlight.Remove(activityPath) : inFlight.SetItem(activityPath, c - 1);
            state.OnNext(inFlight);
        }
    }

    /// <summary>
    /// Completes once the write for <paramref name="activityPath"/> has ENTERED and then LEFT the
    /// in-flight set — the detached pipeline's own termination signal (success, error, or an
    /// unsubscribe forced by disposal; <c>Begin</c>'s registration is cleared from a <c>Finally</c>,
    /// so every ending counts).
    ///
    /// <para>🚨 <b>Subscribe BEFORE you trigger the write.</b> This deliberately does NOT complete
    /// on a path that has never been in flight — that is the whole difference from
    /// <see cref="Drain"/>, which "completes IMMEDIATELY when nothing is in flight" and therefore
    /// races <see cref="Begin"/> when called right after a <c>Post</c>: it would return before the
    /// write was even registered and turn a timing flake into a deterministic false pass. A
    /// subscriber that attaches after the write already finished waits forever (by design — the
    /// alternative is that false pass), so subscribe first, then post.</para>
    ///
    /// <para>This is the signal to wait on instead of polling a query for the node the write
    /// produces: <c>IMeshService.Query</c> is eventually consistent, so a poll asserts "the index
    /// caught up within N seconds" rather than "the write finished" (issue #993). Wait here, then
    /// read the node authoritatively.</para>
    ///
    /// <para><b>Scope of the signal.</b> It completes the first time NO write for this path is
    /// outstanding, having seen at least one. Writes that OVERLAP are therefore all awaited (the
    /// per-path counter only reaches zero when the last of them ends — the create-vs-update race
    /// stays covered). A later, NON-overlapping write for the same path is a separate cycle and
    /// needs its own subscription; this is a "my write finished" signal, not "no write will ever
    /// run again".</para>
    /// </summary>
    /// <param name="activityPath">The activity node path passed to <see cref="Begin"/>.</param>
    public IObservable<Unit> WhenSettled(string activityPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activityPath);
        return states
            .Select(s => s.ContainsKey(activityPath))
            .DistinctUntilChanged()
            // Drop the leading "not started yet" — SkipWhile begins forwarding at the first `true`,
            // so the `false` that follows is unambiguously a LEAVE and never the initial state.
            .SkipWhile(running => !running)
            .Where(running => !running)
            .Take(1)
            .Select(_ => Unit.Default);
    }

    /// <summary>
    /// Completes once every outstanding write has finished, or after <see cref="DrainTimeout"/>.
    ///
    /// <para>Registered by the tracking hub as a reactive dispose action, so hub disposal composes
    /// it into the chain it subscribes at shutdown. Completes IMMEDIATELY when nothing is in flight,
    /// which is the normal case — this must not add latency to an idle shutdown.</para>
    /// </summary>
    public IObservable<Unit> Drain(ILogger? logger = null) =>
        Observable.Defer(() =>
        {
            if (Count == 0)
                return Observable.Return(Unit.Default);

            logger?.LogInformation(
                "Activity drain: waiting for {Count} in-flight write(s) before disposal: {Paths}",
                Count, string.Join(", ", InFlight));

            return states
                .Where(s => s.Count == 0)
                .Take(1)
                .Select(_ => Unit.Default)
                .Timeout(DrainTimeout)
                .Catch<Unit, Exception>(_ =>
                {
                    // Loud, and it NAMES them: a lost activity write is otherwise invisible —
                    // the request it belonged to completed successfully long before.
                    logger?.LogWarning(
                        "Activity drain: {Count} write(s) did not finish within {Timeout}s and are "
                        + "being abandoned by shutdown: {Paths}",
                        Count, DrainTimeout.TotalSeconds, string.Join(", ", InFlight));
                    return Observable.Return(Unit.Default);
                });
        });

    private sealed class Registration(ActivityWriteTracker owner, string path) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            // Idempotent: Rx can dispose a subscription more than once, and a double release
            // would under-count the set and let a drain report clear while a write is running.
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            owner.Release(path);
        }
    }
}
