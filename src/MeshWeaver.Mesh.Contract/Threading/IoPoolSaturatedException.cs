namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// A leaf admitted through <see cref="IoPool.InvokeOrRefuse{T}"/> found every slot of its pool taken.
/// The work never started. Refusing instead of queuing is deliberate on a lane whose leaves wait on
/// other leaves of the same lane: there, a queue is a deadlock once every slot is held by a waiter.
/// </summary>
/// <param name="maxConcurrency">The pool's slot count, every one of which was in use.</param>
public sealed class IoPoolSaturatedException(int maxConcurrency)
    : InvalidOperationException(
        $"Refused: all {maxConcurrency} slot(s) of this pool are in use, and this lane refuses rather "
        + "than queues because its leaves wait on each other.")
{
    /// <summary>The pool's slot count at the moment of refusal.</summary>
    public int MaxConcurrency { get; } = maxConcurrency;
}
