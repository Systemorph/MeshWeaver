using System.Reactive.Disposables;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;

namespace MeshWeaver.GitSync;

/// <summary>
/// Fire-and-forget GitSync work (a webhook-triggered import) owned for exactly as long as it runs.
/// </summary>
internal static class OwnedSubscriptionExtensions
{
    /// <summary>
    /// Subscribes <paramref name="source"/> and hands the subscription to the mesh's IoPool registry
    /// (<see cref="IoPoolOwnershipExtensions.OwnUntilIoDrain"/>) so a drain disposes it BEFORE it cancels the
    /// pool it runs on — instead of finding it attached and reporting it as an unowned leak at Error
    /// (#7968, MeshWeaver#8510). The ownership is released when the source terminates, so a long-lived
    /// mesh does not accumulate finished handles.
    /// </summary>
    internal static IDisposable SubscribeOwnedUntilIoDrain<T>(
        this IMessageHub hub, IObservable<T> source, string owner, Action<T> onNext, Action<Exception> onError)
    {
        var inner = new SingleAssignmentDisposable();
        var handle = hub.OwnUntilIoDrain(inner, owner);
        try
        {
            inner.Disposable = source
                .Finally(handle.Dispose)
                .Subscribe(onNext, onError);
        }
        catch
        {
            // A synchronous throw while wiring must not leave the registration lingering until the drain.
            handle.Dispose();
            throw;
        }
        return handle;
    }
}
