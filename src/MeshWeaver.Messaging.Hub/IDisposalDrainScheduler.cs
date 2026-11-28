namespace MeshWeaver.Messaging;

/// <summary>
/// The scheduler a hub's turns run on from the moment its <c>Dispose()</c> begins (#5799): dedicated
/// threads the ThreadPool never lends out and never queues behind, so a <c>ShutdownRequest</c> does not
/// wait behind the pool's backlog. Defined here because <see cref="MessageService"/> consumes it; the
/// mesh implements it (<c>MeshWeaver.Mesh.Threading.DisposalDrainLane</c>, registered by
/// <c>AddIoPools</c>) on top of the already-sanctioned dedicated-thread lane scheduler — the same seam
/// as <c>IPooledSubscribeScheduler</c>. A hub whose container has none keeps draining on its own
/// scheduler.
/// </summary>
public interface IDisposalDrainScheduler
{
    /// <summary>The scheduler teardown drains run on.</summary>
    TaskScheduler Scheduler { get; }

    /// <summary>
    /// A drain is about to be queued on the lane for <paramref name="hubAddress"/>. A lane that is
    /// saturated says so here (a Warning naming the turns holding its threads), so a stall is
    /// visible at the moment it forms instead of surfacing eight seconds later as a verdict.
    /// </summary>
    void NoteQueued(string hubAddress)
    {
    }

    /// <summary>
    /// Registers a drain that is RUNNING on the lane, with a reader for the turn it is executing, so a
    /// saturated lane can name what holds its threads. Dispose the result when the drain ends.
    /// </summary>
    IDisposable TrackDrain(string hubAddress, Func<string?> currentTurn) => NoDrain.Instance;

    /// <summary>
    /// A one-line reading of the lane's own state (live threads, parked threads, drains running, tasks
    /// queued and for how long), named in the pump verdict so "the scheduler accepted a drain and did
    /// not run it" says WHICH of the lane's states it was (#5799 recurrence 2026-11-28: 312 verdicts,
    /// no lane report, no way to tell a full lane from a lane with no thread behind its queue).
    /// </summary>
    string DescribeState() => string.Empty;

    private sealed class NoDrain : IDisposable
    {
        public static readonly NoDrain Instance = new();
        public void Dispose()
        {
        }
    }
}
