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
}
