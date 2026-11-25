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
/// 2026-11-25). The cap stays modest on purpose — a burst must not start hundreds of threads — and the
/// lane logs a Warning, naming the turns that hold its threads, when a drain is queued while every
/// thread is taken.</para>
/// </summary>
public sealed class DisposalDrainLane : IDisposalDrainScheduler, IDisposable
{
    /// <summary>The most dedicated threads the lane starts.</summary>
    public const int MaxThreads = 64;

    /// <summary>Name of the lane's threads — visible in dumps.</summary>
    public const string ThreadName = "mw-disposal-lane";

    private static readonly TimeSpan WarnEvery = TimeSpan.FromSeconds(5);

    private readonly LimitedConcurrencyLevelTaskScheduler scheduler = new(MaxThreads, ThreadName);
    private readonly ILogger? logger;
    private readonly ConcurrentDictionary<long, RunningDrain> running = new();
    private long nextId;
    private long lastWarnedTicks;

    /// <summary>Creates the lane; the logger, when resolvable, receives the saturation warning.</summary>
    public DisposalDrainLane(ILoggerFactory? loggerFactory = null)
    {
        logger = loggerFactory?.CreateLogger(nameof(DisposalDrainLane));
    }

    /// <inheritdoc />
    public TaskScheduler Scheduler => scheduler;

    /// <summary>Drains currently executing on the lane (a parked lane thread is not counted).</summary>
    public int ActiveDrains => running.Count;

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
        var last = Interlocked.Read(ref lastWarnedTicks);
        if (last != 0 && Stopwatch.GetElapsedTime(last, now) < WarnEvery)
            return;
        if (Interlocked.CompareExchange(ref lastWarnedTicks, now, last) != last)
            return;
        var holders = running.Values
            .OrderBy(r => r.StartedTicks)
            .Take(5)
            .Select(r => $"{r.Address} [{r.CurrentTurn() ?? "between turns"}, "
                         + $"{(long)Stopwatch.GetElapsedTime(r.StartedTicks, now).TotalMilliseconds}ms in this drain]");
        logger?.LogWarning(
            "[DISPOSAL-LANE] saturated: all {Max} lane threads are running drains, so the drain for {Hub} "
            + "waits behind them. A teardown turn that BLOCKS holds its thread — blocking I/O belongs behind "
            + "IIoPool. Oldest holders: {Holders}",
            MaxThreads, hubAddress, string.Join("; ", holders));
    }

    /// <summary>Ends the lane threads once the queue is empty. A task queued afterwards still runs.</summary>
    public void Dispose() => scheduler.Complete();

    private sealed record RunningDrain(string Address, Func<string?> CurrentTurn, long StartedTicks);

    private sealed class Untrack(ConcurrentDictionary<long, RunningDrain> running, long id) : IDisposable
    {
        public void Dispose() => running.TryRemove(id, out _);
    }
}
