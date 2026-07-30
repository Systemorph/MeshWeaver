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
    private readonly BehaviorSubject<int> countSubject = new(0);
    private readonly IObserver<int> count;
    private readonly IObservable<int> counts;

    /// <summary>Creates an empty tracker.</summary>
    public ActivityWriteTracker()
    {
        count = Observer.Synchronize(countSubject, preventReentrancy: true);
        counts = countSubject.AsObservable();
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
        // notification: the observer is synchronized with preventReentrancy, and the only
        // subscriber is Drain's Where/Take filter, which never calls back in here.
        lock (gate)
        {
            inFlight = inFlight.SetItem(
                activityPath, inFlight.TryGetValue(activityPath, out var c) ? c + 1 : 1);
            count.OnNext(inFlight.Values.Sum());
        }
        return new Registration(this, activityPath);
    }

    private void Release(string activityPath)
    {
        lock (gate)
        {
            if (inFlight.TryGetValue(activityPath, out var c))
                inFlight = c <= 1 ? inFlight.Remove(activityPath) : inFlight.SetItem(activityPath, c - 1);
            count.OnNext(inFlight.Values.Sum());
        }
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

            return counts
                .Where(n => n == 0)
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
