namespace MeshWeaver.Messaging;

/// <summary>
/// Runs a synchronous, blocking read — a file read and parse — on the mesh's controlled I/O pool,
/// off the calling hub, grain or render thread, and hands the result back as an observable.
///
/// <para>The abstraction exists for code that sits BELOW <c>MeshWeaver.Mesh.Contract</c> (where
/// <c>IIoPool</c> lives) and therefore cannot reference the pool: the mesh registers the pool-backed
/// implementation in <c>AddIoPools</c>, and such code resolves it from a hub's
/// <c>ServiceProvider</c>. The first consumer is the XML-documentation reader, whose cold read of an
/// assembly's documentation file must never run while a view is being built
/// (<c>Doc/Architecture/XmlDocumentationReader</c>).</para>
///
/// <para>A hub with no I/O pools registered (a bare messaging-only hub) resolves nothing; the
/// consumer then hops the work onto the task pool itself, so the calling thread still never reads.</para>
/// </summary>
public interface IBlockingReadScheduler
{
    /// <summary>
    /// Returns a COLD observable that, when subscribed, runs <paramref name="work"/> on the pool's
    /// blocking scheduler and emits its result once. The work is a tracked leaf: mesh teardown
    /// cancels and joins it before the service scope disposes.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The blocking work; observes the cancellation token.</param>
    /// <returns>The result, emitted once.</returns>
    IObservable<T> InvokeBlocking<T>(Func<CancellationToken, T> work);
}
