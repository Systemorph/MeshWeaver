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
/// <para>🚨 <b>An expiry is NOT a verdict about the code</b> (#6195). It throws
/// <see cref="CpuLaneExpiredException"/>, which the terminal write-back reads as an availability
/// non-verdict: the type is stamped <c>Unavailable</c>, the park budget is untouched, and the
/// failed-verdict re-drive asks again. Measured on memex.systemorph.com (2026-11-03): three Hosting
/// types, each compiling a 302-file source closure whose Roslyn half takes about 240 s on a healthy
/// replica, crossed the 300 s bound on a replica spending about 20 % of wall time in GC pauses. They
/// were stamped <c>Error</c> with their verdict "formed under the live inputs", so nothing retried
/// them, and they stayed red for about 7 h until a roll replaced the pods. The expiry message now also
/// carries the lane's occupancy and the process's GC pause share at the moment of expiry, because that
/// incident's message carried neither and both had to be reconstructed from the liveness log.</para>
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
        IScheduler? scheduler = null,
        Func<string>? laneState = null)
        => Observable.Defer(() =>
        {
            var clock = scheduler ?? DefaultScheduler.Instance;
            var wait = new CpuLaneWait();

            var prefixExpired = Observable.Timer(bound, clock)
                .TakeUntil(wait.Queued)
                .Select(_ => Expired(legName, nodePath,
                    $"its IO prefix did not reach the CPU lane within {Seconds(bound)}s", laneState));
            var laneStalled = wait.Queued
                .SelectMany(_ => Stalled(laneProgress, bound * StallWindowFactor, clock))
                .TakeUntil(wait.Admitted)
                .Select(window => Expired(legName, nodePath,
                    $"it was queued for a CPU-lane slot and the lane completed NO leaf for {Seconds(window)}s "
                    + "(every slot is held by work that does not stop when cancelled)", laneState));
            var emitExpired = wait.Admitted
                .SelectMany(_ => Observable.Timer(bound, clock))
                .Select(_ => Expired(legName, nodePath,
                    $"its emit did not complete within {Seconds(bound)}s of being admitted to the CPU lane",
                    laneState));

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

    private static CpuLaneExpiredException Expired(
        string legName, string nodePath, string why, Func<string>? laneState)
        => new($"Compile leg '{legName}' for '{nodePath}' timed out: {why}. Time spent waiting in a "
            + "draining CPU-lane queue is not counted. "
            + $"At the expiry: {Reading(laneState)}; {GcReading()}. "
            + "No verdict about the code was formed, so the type settles at CompilationStatus=Unavailable "
            + "(never Compiling, where the single-flight status lock would absorb every later trigger, and "
            + "never Error, which nothing retries) and the failed-verdict re-drive compiles it again under "
            + "its own bound.");

    private static string Reading(Func<string>? laneState)
        => laneState?.Invoke() ?? "CPU-lane occupancy not measured (no bounded lane)";

    // Process-wide, from the runtime's own counters: the share of wall time the last GC spent paused
    // and the total pause time since start. A compile is suspended for every pause, so this is the
    // first thing to read when an admitted emit runs past its bound.
    private static string GcReading()
    {
        var info = GC.GetGCMemoryInfo();
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"GC pause {info.PauseTimePercentage:0.#}% of time, {GC.GetTotalPauseDuration().TotalSeconds:0.#}s "
            + $"paused since start, heap {info.HeapSizeBytes / (1024.0 * 1024 * 1024):0.##}GiB, "
            + $"gen2 collections {GC.CollectionCount(2)}");
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#",
        System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A <see cref="CpuLaneBound"/> expiry: the compile leg ran out of its bound, so no verdict about the
/// code was formed. Still a <see cref="TimeoutException"/> for every reader that asks only that;
/// <c>NodeTypeCompilationHelpers.IsAvailabilityNonVerdict</c> reads THIS type to stamp
/// <c>Unavailable</c> instead of <c>Error</c> and to leave the park budget alone (#6195).
/// </summary>
internal sealed class CpuLaneExpiredException(string message) : TimeoutException(message);

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
