using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Threading;

/// <summary>
/// The <see cref="IBlockingReadScheduler"/> implementation: runs a blocking read on the
/// <see cref="IoPoolNames.FileSystem"/> pool's dedicated blocking scheduler
/// (<see cref="IIoPool.InvokeBlocking{T}"/>), so it neither holds a ThreadPool worker nor escapes
/// mesh teardown's drain. Registered as a mesh singleton in <c>AddIoPools</c>.
/// </summary>
/// <param name="registry">The mesh's I/O pool registry.</param>
public sealed class IoPoolBlockingReadScheduler(IoPoolRegistry registry) : IBlockingReadScheduler
{
    /// <inheritdoc />
    public IObservable<T> InvokeBlocking<T>(Func<CancellationToken, T> work) =>
        registry.Get(IoPoolNames.FileSystem).InvokeBlocking(work);
}
