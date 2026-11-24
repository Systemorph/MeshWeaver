using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// The mesh's teardown lane (#5799): up to <see cref="MaxThreads"/> dedicated, kept-parked threads on
/// which a disposing hub's pump drains, so its <c>ShutdownRequest</c> never waits behind the thread
/// pool's queue. Built on <see cref="LimitedConcurrencyLevelTaskScheduler"/> in its dedicated-thread
/// mode — the same parking core the blocking I/O lanes use — and completed with the mesh's container.
/// One per mesh (registered by <c>AddIoPools</c>), never a static.
/// </summary>
public sealed class DisposalDrainLane : IDisposalDrainScheduler, IDisposable
{
    /// <summary>The most dedicated threads the lane starts.</summary>
    public const int MaxThreads = 16;

    /// <summary>Name of the lane's threads — visible in dumps.</summary>
    public const string ThreadName = "mw-disposal-lane";

    private readonly LimitedConcurrencyLevelTaskScheduler scheduler = new(MaxThreads, ThreadName);

    /// <inheritdoc />
    public TaskScheduler Scheduler => scheduler;

    /// <summary>Ends the lane threads once the queue is empty. A task queued afterwards still runs.</summary>
    public void Dispose() => scheduler.Complete();
}
