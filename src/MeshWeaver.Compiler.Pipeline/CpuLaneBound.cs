using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// The wall-clock bound on a compile leg whose CPU half QUEUES on the bounded CPU lane
/// (<c>IoPoolNames.CompileCpu</c>). The bound times the leg's OWN work, never its wait for a slot,
/// yet a lane that has STOPPED draining still ends in a terminal failure. There are three phases,
/// each with its own named expiry:
/// <list type="number">
/// <item><b>Prefix</b>, from subscription until the emit is QUEUED: bounded by <c>bound</c>.</item>
/// <item><b>Queue</b>, from QUEUED until ADMITTED: no clock on the wait itself, because a draining
/// lane is healthy however long the line is. It fails only when the lane makes NO progress (no
/// admission of any leaf completes) for <see cref="StallWindowFactor"/> × <c>bound</c>. That is a
/// lane whose every slot is held by a leaf that ignores its cancellation (for example a source
/// generator's constructor, which no token interrupts).</item>
/// <item><b>Emit</b>, from ADMITTED: bounded afresh by <c>bound</c>. On expiry the leg is
/// unsubscribed, which cancels the token it was handed, so a cooperative emit stops and frees its
/// slot.</item>
/// </list>
///
/// <para>Why the queue is not timed: the lane is capped at the processor count, so a burst of
/// distinct-type compiles waits in line. With one clock from subscription, a compile still queued
/// when the bound elapsed failed TERMINALLY as "hung" although it had not started. Measured in the
/// combo verification lane (#4413): 47 types of one package were dispatched together, and the ten still
/// queued after five minutes were parked at <c>CompilationStatus=Error</c>, which read as a red
/// package.</para>
///
/// <para>Why the stall window is a multiple of the bound: an admitted compile is cancelled at its own
/// bound, but cancellation is observed at Roslyn's next check, so its completion can land shortly
/// after. A window of one bound would call that a stall. A window of
/// <see cref="StallWindowFactor"/> bounds would not.</para>
/// </summary>
internal static class CpuLaneBound
{
    /// <summary>How many bounds without a single lane completion count as a stalled lane.</summary>
    internal const int StallWindowFactor = 2;

    /// <summary>
    /// Bound <paramref name="leg"/> as described on the type. <paramref name="laneProgress"/> reads a
    /// monotone count of finished lane admissions. When it is <c>null</c> (an unbounded fallback pool
    /// that never queues) the queue phase has no stall detection.
    /// </summary>
    internal static IObservable<T> Bound<T>(
        Func<CancellationToken, CpuLaneWait, IObservable<T>> leg,
        TimeSpan bound,
        string legName,
        string nodePath,
        Func<long>? laneProgress,
        IScheduler? scheduler = null)
        => Observable.Defer(() =>
        {
            var clock = scheduler ?? DefaultScheduler.Instance;
            var wait = new CpuLaneWait();

            var prefixExpired = Observable.Timer(bound, clock)
                .TakeUntil(wait.Queued)
                .Select(_ => Expired(legName, nodePath,
                    $"its IO prefix did not reach the CPU lane within {Seconds(bound)}s"));
            var laneStalled = wait.Queued
                .SelectMany(_ => Stalled(laneProgress, bound * StallWindowFactor, clock))
                .TakeUntil(wait.Admitted)
                .Select(window => Expired(legName, nodePath,
                    $"it was queued for a CPU-lane slot and the lane completed NO leaf for {Seconds(window)}s "
                    + "(every slot is held by work that does not stop when cancelled)"));
            var emitExpired = wait.Admitted
                .SelectMany(_ => Observable.Timer(bound, clock))
                .Select(_ => Expired(legName, nodePath,
                    $"its emit did not complete within {Seconds(bound)}s of being admitted to the CPU lane"));

            var expiry = Observable.Merge(prefixExpired, laneStalled, emitExpired)
                .Take(1)
                .Concat(Observable.Never<TimeoutException>())
                .SelectMany(Observable.Throw<T>);

            // Amb: the first of the two to say anything wins and the other is unsubscribed. When the
            // expiry wins, unsubscribing the leg disposes the CancellationDisposable, which trips the
            // token the leg's NuGet restore / Roslyn emit was started with.
            return Observable
                .Using(() => new CancellationDisposable(), cancel => leg(cancel.Token, wait))
                .Amb(expiry);
        });

    // Emits the window once the lane completes nothing for a whole window; never otherwise.
    private static IObservable<TimeSpan> Stalled(Func<long>? laneProgress, TimeSpan window, IScheduler clock)
        => laneProgress is null
            ? Observable.Never<TimeSpan>()
            : Observable.Defer(() =>
            {
                var seen = laneProgress();
                return Observable.Interval(window, clock)
                    .Select(_ =>
                    {
                        var now = laneProgress();
                        var stalled = now == seen;
                        seen = now;
                        return stalled;
                    })
                    .Where(stalled => stalled)
                    .Take(1)
                    .Select(_ => window);
            });

    private static TimeoutException Expired(string legName, string nodePath, string why)
        => new($"Compile leg '{legName}' for '{nodePath}' timed out: {why}. Time spent waiting in a "
            + "draining CPU-lane queue is not counted. The compile fails terminally instead of parking at "
            + "CompilationStatus=Compiling forever (where the single-flight status lock would absorb "
            + "every later trigger); retry via the Compile button / a fresh RequestedReleaseAt once the "
            + "cause is understood.");

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#",
        System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The two moments of a compile's CPU half that <see cref="CpuLaneBound"/> times against: QUEUED for a
/// CPU-lane slot, and ADMITTED to one. Each fires at most once and replays to a late subscriber. A
/// caller that joins a compile already in flight (single-flight) follows the leader's moments, so its
/// bound also excludes the leader's queue wait.
/// </summary>
internal sealed class CpuLaneWait
{
    private readonly ReplaySubject<Unit> _queued = new(1);
    private readonly ReplaySubject<Unit> _admitted = new(1);

    public IObservable<Unit> Queued => _queued;
    public IObservable<Unit> Admitted => _admitted;

    public void MarkQueued() => Mark(_queued);
    public void MarkAdmitted() => Mark(_admitted);

    /// <summary>Mirror the moments of the compile this caller joined.</summary>
    public void Follow(CpuLaneWait leader)
    {
        leader.Queued.Take(1).Subscribe(_ => MarkQueued());
        leader.Admitted.Take(1).Subscribe(_ => MarkAdmitted());
    }

    // A second mark is inert: a completed subject ignores OnNext.
    private static void Mark(ReplaySubject<Unit> moment)
    {
        moment.OnNext(Unit.Default);
        moment.OnCompleted();
    }
}
