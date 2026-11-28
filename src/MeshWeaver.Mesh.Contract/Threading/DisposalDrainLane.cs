using System.Collections.Concurrent;
using System.Diagnostics;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// The mesh's teardown lane (#5799): up to <see cref="MaxThreads"/> dedicated, kept-parked threads on
/// which a disposing hub's pump drains, so its <c>ShutdownRequest</c> never waits behind the thread
/// pool's queue. Built on <see cref="LimitedConcurrencyLevelTaskScheduler"/> in its dedicated-thread
/// mode — the same parking core the blocking I/O lanes use — and completed with the mesh's container.
/// One per mesh (registered by <c>AddIoPools</c>), never a static.
///
/// <para>🚨 <b>Saturation is LOUD.</b> A lane thread is held for as long as the turn on it runs, so a
/// teardown turn that blocks (the thing a hub turn must never do — blocking I/O belongs behind
/// <c>IIoPool</c>) holds a thread for as long as it blocks, and enough of them make every other
/// disposing hub's first turn wait (<c>RunLevel=Started, drainsAwaitingScheduler=1</c>, memex-cloud
/// 2026-11-25). The cap stays modest on purpose — a burst must not start hundreds of threads.</para>
///
/// <para>🚨 <b>A full lane is not a stuck lane</b> (#5799, measured 2026-11-26 on image 4.0.14261: 45
/// saturations in 3 h across four instances, every holder a <c>sync/*</c> hub running its
/// <c>ShutdownRequest</c>, the OLDEST holder never older than 252 ms, zero stall verdicts). That is a
/// mass teardown — a parent disposing all its hosted children at once — queueing behind the bound the
/// lane exists to impose, and it drains in milliseconds. Reporting it at Warning made the signal
/// useless for the case it was built for. So the lane distinguishes the two by the one thing that
/// does: how long the oldest holder has been in its drain. Past <see cref="StuckAfter"/> a turn is
/// BLOCKING and the lane logs a Warning naming the turns that hold its threads; below it the lane
/// logs the absorbed burst at Information, with the oldest holder's age, so the burst stays countable.</para>
/// </summary>
public sealed class DisposalDrainLane : IDisposalDrainScheduler, IDisposable
{
    /// <summary>The most dedicated threads the lane starts.</summary>
    public const int MaxThreads = 64;

    /// <summary>Name of the lane's threads — visible in dumps.</summary>
    public const string ThreadName = "mw-disposal-lane";

    /// <summary>How long the oldest holder may have been in its drain before a full lane is reported as stuck.</summary>
    public static readonly TimeSpan DefaultStuckAfter = TimeSpan.FromSeconds(1);

    /// <summary>How long the lane may stay continuously full, however short each drain, before it is reported as stuck.</summary>
    public static readonly TimeSpan DefaultSustainedAfter = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan WarnEvery = TimeSpan.FromSeconds(5);

    private readonly LimitedConcurrencyLevelTaskScheduler scheduler = new(MaxThreads, ThreadName);
    private readonly ILogger? logger;
    private readonly ConcurrentDictionary<long, RunningDrain> running = new();
    private long nextId;
    private long lastWarnedTicks;
    private long lastNotedTicks;
    private long fullSinceTicks;
    private long lastFullTicks;

    /// <summary>
    /// Creates the lane; the logger, when resolvable, receives the saturation reports.
    /// </summary>
    /// <param name="loggerFactory">Source of the lane's logger.</param>
    /// <param name="stuckAfter">Age of the oldest holder at which a full lane is reported as stuck
    /// (Warning) rather than as an absorbed burst (Information); <see cref="DefaultStuckAfter"/> when null.</param>
    /// <param name="sustainedAfter">How long the lane may stay continuously full (each drain short, the queue
    /// never empty) before that too is reported as stuck; <see cref="DefaultSustainedAfter"/> when null.</param>
    public DisposalDrainLane(ILoggerFactory? loggerFactory = null, TimeSpan? stuckAfter = null,
        TimeSpan? sustainedAfter = null)
    {
        logger = loggerFactory?.CreateLogger(nameof(DisposalDrainLane));
        StuckAfter = stuckAfter ?? DefaultStuckAfter;
        SustainedAfter = sustainedAfter ?? DefaultSustainedAfter;
    }

    /// <summary>How long continuous saturation counts as stuck even when no single drain is old.</summary>
    public TimeSpan SustainedAfter { get; }

    /// <summary>The age of the oldest holder at which a full lane counts as stuck.</summary>
    public TimeSpan StuckAfter { get; }

    /// <inheritdoc />
    public TaskScheduler Scheduler => scheduler;

    /// <summary>Drains currently executing on the lane (a parked lane thread is not counted).</summary>
    public int ActiveDrains => running.Count;

    /// <inheritdoc />
    public string DescribeState()
    {
        // Lock-free reads except the two short lock(_tasks) gauges: this runs inside a stall verdict.
        return $"lane state: liveThreads={scheduler.LiveDrainLoops}/{MaxThreads}, parkedThreads={scheduler.ParkedLaneThreads}, "
            + $"drainsRunning={running.Count}, tasksQueued={scheduler.QueuedTasks}";
    }

    /// <inheritdoc />
    public IDisposable TrackDrain(string hubAddress, Func<string?> currentTurn)
    {
        var id = Interlocked.Increment(ref nextId);
        running[id] = new RunningDrain(hubAddress, currentTurn, Stopwatch.GetTimestamp());
        return new Untrack(running, id);
    }

    /// <inheritdoc />
    public void NoteQueued(string hubAddress)
    {
        // Saturated = every thread the lane may start is running a drain and none is parked.
        if (running.Count < MaxThreads)
            return;
        var now = Stopwatch.GetTimestamp();
        var oldest = running.Values.OrderBy(r => r.StartedTicks).Take(5).ToArray();
        if (oldest.Length == 0)
            return;
        var oldestAge = Stopwatch.GetElapsedTime(oldest[0].StartedTicks, now);
        // Churn is not a burst: drains that each finish quickly but never let the lane empty are
        // consecutive saturated observations, no more than a throttle period apart, spanning
        // SustainedAfter. A gap longer than that means the lane drained in between.
        var lastFull = Interlocked.Exchange(ref lastFullTicks, now);
        if (lastFull == 0 || Stopwatch.GetElapsedTime(lastFull, now) > WarnEvery + WarnEvery)
            Interlocked.Exchange(ref fullSinceTicks, now);
        var fullFor = Stopwatch.GetElapsedTime(Interlocked.Read(ref fullSinceTicks), now);
        var sustained = fullFor >= SustainedAfter;
        var stuck = oldestAge >= StuckAfter || sustained;
        // One throttle per level: an absorbed burst a moment ago must not swallow the report of a
        // stuck lane.
        ref var gate = ref stuck ? ref lastWarnedTicks : ref lastNotedTicks;
        var last = Interlocked.Read(ref gate);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < WarnEvery)
            return;
        if (Interlocked.CompareExchange(ref gate, now, last) != last)
            return;
        var holders = string.Join("; ", oldest.Select(r => $"{r.Address} [{r.CurrentTurn() ?? "between turns"}, "
            + $"{(long)Stopwatch.GetElapsedTime(r.StartedTicks, now).TotalMilliseconds}ms in this drain]"));
        if (stuck)
            logger?.LogWarning(
                "[DISPOSAL-LANE] stuck (saturated): all {Max} lane threads are running drains — the oldest has held its "
                + "thread for {Age}ms, the lane has been full for {FullFor}ms — so the drain for {Hub} waits behind them. A teardown turn that BLOCKS "
                + "holds its thread — blocking I/O belongs behind IIoPool. Oldest holders: {Holders}",
                MaxThreads, (long)oldestAge.TotalMilliseconds, (long)fullFor.TotalMilliseconds, hubAddress, holders);
        else
            logger?.LogInformation(
                "[DISPOSAL-LANE] burst absorbed (saturated): all {Max} lane threads are running drains (oldest {Age}ms, "
                + "under the {StuckAfter}ms stuck threshold), so the drain for {Hub} waits its turn — a mass "
                + "teardown draining, not a blocked turn. Oldest holders: {Holders}",
                MaxThreads, (long)oldestAge.TotalMilliseconds, (long)StuckAfter.TotalMilliseconds, hubAddress, holders);
    }

    /// <summary>Ends the lane threads once the queue is empty. A task queued afterwards still runs.</summary>
    public void Dispose() => scheduler.Complete();

    private sealed record RunningDrain(string Address, Func<string?> CurrentTurn, long StartedTicks);

    private sealed class Untrack(ConcurrentDictionary<long, RunningDrain> running, long id) : IDisposable
    {
        public void Dispose() => running.TryRemove(id, out _);
    }
}
