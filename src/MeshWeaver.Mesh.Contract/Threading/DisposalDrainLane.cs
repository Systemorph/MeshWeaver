using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// The mesh's teardown lane (#5799): dedicated, kept-parked threads on which a disposing hub's pump
/// drains, so its <c>ShutdownRequest</c> never waits behind the thread pool's queue.
///
/// <para>🚨 <b>The cap is a safety ceiling, not a throttle (#5799 recurrence).</b> A lane thread is held
/// for as long as the turn it runs blocks, and a teardown turn can block for tens of seconds (a
/// disposal action waiting on a database timeout — memex-cloud 2026-11-25 07:59Z). At the original 16
/// threads a mass teardown parked sixteen such turns and every other disposing hub's FIRST turn then
/// queued behind them: <c>RunLevel=Started, queue depth 1, drainsAwaitingScheduler=1, 0 dequeued</c> —
/// the very stall the lane exists to remove, moved from the pool to the lane. A thread is only ever
/// started when no parked one is free, so the cap bounds a runaway, never a normal teardown.</para>
/// Built on <see cref="LimitedConcurrencyLevelTaskScheduler"/> in its dedicated-thread
/// mode — the same parking core the blocking I/O lanes use — and completed with the mesh's container.
/// One per mesh (registered by <c>AddIoPools</c>), never a static.
/// </summary>
public sealed class DisposalDrainLane : IDisposalDrainScheduler, IDisposable
{
    /// <summary>The most dedicated threads the lane starts.</summary>
    public const int MaxThreads = 1024;

    /// <summary>Name of the lane's threads — visible in dumps.</summary>
    public const string ThreadName = "mw-disposal-lane";

    private readonly LimitedConcurrencyLevelTaskScheduler scheduler = new(MaxThreads, ThreadName);

    /// <inheritdoc />
    public TaskScheduler Scheduler => scheduler;

    /// <summary>Ends the lane threads once the queue is empty. A task queued afterwards still runs.</summary>
    public void Dispose() => scheduler.Complete();
}
