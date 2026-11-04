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
        Func<CpuLaneReading>? laneState = null)
        => Observable.Defer(() =>
        {
            var clock = scheduler ?? DefaultScheduler.Instance;
            var wait = new CpuLaneWait();

            var prefixExpired = Observable.Timer(bound, clock)
                .TakeUntil(wait.Queued)
                .Select(_ => Expired(legName, nodePath, CpuLaneExpiryPhase.Prefix, bound, laneState));
            var laneStalled = wait.Queued
                .SelectMany(_ => Stalled(laneProgress, bound * StallWindowFactor, clock))
                .TakeUntil(wait.Admitted)
                .Select(window => Expired(legName, nodePath, CpuLaneExpiryPhase.Stalled, window, laneState));
            var emitExpired = wait.Admitted
                .SelectMany(_ => Observable.Timer(bound, clock))
                .Select(_ => Expired(legName, nodePath, CpuLaneExpiryPhase.Emit, bound, laneState));

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
        string legName, string nodePath, string phase, TimeSpan window, Func<CpuLaneReading>? laneState)
        => new(legName, nodePath, phase, window.TotalSeconds, laneState?.Invoke(), GcReading.Now());
}

/// <summary>Which phase of <see cref="CpuLaneBound"/> expired. Never persisted as a value of its own:
/// it names the catalog key of the activity line (<c>activity.compile.laneExpired.{phase}</c>).</summary>
internal static class CpuLaneExpiryPhase
{
    /// <summary>The IO prefix did not reach the CPU lane within the bound.</summary>
    public const string Prefix = "prefix";

    /// <summary>Queued for a slot while the lane completed no leaf for the stall window.</summary>
    public const string Stalled = "stalled";

    /// <summary>Admitted, and the emit did not finish within the bound.</summary>
    public const string Emit = "emit";
}

/// <summary>The CPU lane's occupancy at one moment.</summary>
/// <param name="Running">Leaves holding a slot.</param>
/// <param name="Capacity">The lane's slot count.</param>
/// <param name="Queued">Leaves waiting for a slot.</param>
internal sealed record CpuLaneReading(int Running, int Capacity, int Queued);

/// <summary>
/// The process's garbage-collection state at one moment, from the runtime's own counters. A compile is
/// suspended for every pause, so this is the first thing to read when an admitted emit runs past its
/// bound (#6195: about 20 % of wall time in pauses on the replica where three compiles expired).
/// </summary>
/// <param name="PausePercent">Share of wall time the last GC spent paused.</param>
/// <param name="PausedSeconds">Total pause time since the process started.</param>
/// <param name="HeapGiB">Heap size after the last GC.</param>
/// <param name="Gen2Collections">Gen2 collections since the process started.</param>
internal sealed record GcReading(double PausePercent, double PausedSeconds, double HeapGiB, int Gen2Collections)
{
    /// <summary>Read the counters now.</summary>
    public static GcReading Now()
    {
        var info = GC.GetGCMemoryInfo();
        return new(Math.Round(info.PauseTimePercentage, 1), Math.Round(GC.GetTotalPauseDuration().TotalSeconds, 1),
            Math.Round(info.HeapSizeBytes / (1024.0 * 1024 * 1024), 2), GC.CollectionCount(2));
    }
}

/// <summary>
/// A <see cref="CpuLaneBound"/> expiry: the compile leg ran out of its bound, so no verdict about the
/// code was formed. Still a <see cref="TimeoutException"/> for every reader that asks only that;
/// <c>NodeTypeCompilationHelpers.IsAvailabilityNonVerdict</c> reads THIS type to stamp
/// <c>Unavailable</c> instead of <c>Error</c> and to leave the park budget alone (#6195).
///
/// <para>The <see cref="Exception.Message"/> is the English diagnostic for server logs. What a person
/// reads is <see cref="ToActivityMessage"/>: a catalog key per phase with every reading as a named
/// argument, so the sentence is the viewer's language and the numbers are formatted in their culture.</para>
/// </summary>
internal sealed class CpuLaneExpiredException(
    string legName, string nodePath, string phase, double seconds, CpuLaneReading? lane, GcReading gc)
    : TimeoutException(EnglishMessage(legName, nodePath, phase, seconds, lane, gc))
{
    /// <summary>The leg that expired, e.g. <c>roslyn-compile</c>.</summary>
    public string LegName { get; } = legName;

    /// <summary>The NodeType whose compile expired.</summary>
    public string NodePath { get; } = nodePath;

    /// <summary>The phase that expired — one of <see cref="CpuLaneExpiryPhase"/>.</summary>
    public string Phase { get; } = phase;

    /// <summary>The bound (or stall window) that elapsed, in seconds.</summary>
    public double Seconds { get; } = seconds;

    /// <summary>The lane's occupancy at the expiry; <c>null</c> when the lane is not a bounded pool.</summary>
    public CpuLaneReading? Lane { get; } = lane;

    /// <summary>The process's GC state at the expiry.</summary>
    public GcReading Gc { get; } = gc;

    /// <summary>The catalog key of this expiry's activity line.</summary>
    public string MessageKey => $"activity.compile.laneExpired.{Phase}";

    /// <summary>
    /// The activity line: the English sentence as fallback text, the key, and every reading as a named
    /// argument. An absent lane reading binds as an em dash, which needs no translation.
    /// </summary>
    public MeshWeaver.Data.LogMessage ToActivityMessage() =>
        new MeshWeaver.Data.LogMessage(Message, Microsoft.Extensions.Logging.LogLevel.Error)
            .WithKey(MessageKey,
                ("leg", LegName),
                ("path", NodePath),
                ("seconds", Math.Round(Seconds, 1)),
                ("running", (object?)Lane?.Running ?? "—"),
                ("capacity", (object?)Lane?.Capacity ?? "—"),
                ("queued", (object?)Lane?.Queued ?? "—"),
                ("gcPausePercent", Gc.PausePercent),
                ("gcPausedSeconds", Gc.PausedSeconds),
                ("heapGiB", Gc.HeapGiB),
                ("gen2", Gc.Gen2Collections));

    private static string EnglishMessage(
        string legName, string nodePath, string phase, double seconds, CpuLaneReading? lane, GcReading gc)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var why = phase switch
        {
            CpuLaneExpiryPhase.Prefix => $"its IO prefix did not reach the CPU lane within {seconds.ToString("0.#", inv)}s",
            CpuLaneExpiryPhase.Stalled => $"it was queued for a CPU-lane slot and the lane completed NO leaf for "
                                          + $"{seconds.ToString("0.#", inv)}s (every slot is held by work that does "
                                          + "not stop when cancelled)",
            _ => $"its emit did not complete within {seconds.ToString("0.#", inv)}s of being admitted to the CPU lane",
        };
        var laneText = lane is null
            ? "CPU-lane occupancy not measured (no bounded lane)"
            : $"CPU lane {lane.Running}/{lane.Capacity} running, {lane.Queued} queued";
        return $"Compile leg '{legName}' for '{nodePath}' timed out: {why}. Time spent waiting in a draining "
            + $"CPU-lane queue is not counted. At the expiry: {laneText}; GC pause "
            + $"{gc.PausePercent.ToString("0.#", inv)}% of time, {gc.PausedSeconds.ToString("0.#", inv)}s paused "
            + $"since start, heap {gc.HeapGiB.ToString("0.##", inv)}GiB, gen2 collections {gc.Gen2Collections}. "
            + "No verdict about the code was formed, so the type settles at CompilationStatus=Unavailable "
            + "(never Compiling, where the single-flight status lock would absorb every later trigger, and "
            + "never Error, which nothing retries) and the failed-verdict re-drive compiles it again under "
            + "its own bound.";
    }
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
