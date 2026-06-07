using System.Reactive.Linq;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// Main routing in the mesh.
/// </summary>
public interface IRoutingService
{
    /// <summary>
    /// Routes the delivery in the mesh. Returns an <see cref="IObservable{T}"/>
    /// that emits the routed delivery (typically <c>Forwarded</c>) once and
    /// completes. Errors propagate via <c>OnError</c>. Per
    /// <c>Doc/Architecture/AsynchronousCalls.md</c> — no <c>Task&lt;T&gt;</c>
    /// on hub-reachable surfaces.
    /// </summary>
    IObservable<IMessageDelivery> DeliverMessage(IMessageDelivery delivery);


    /// <summary>
    /// Registers a stream for the given address and returns immediately. The returned
    /// <see cref="IDisposable"/> unregisters on <c>Dispose</c> — purely synchronous to
    /// the caller (the hub couples it to its lifetime via
    /// <c>RegisterForDisposal(IDisposable)</c>). Implementations that need genuinely-async
    /// teardown (e.g. an Orleans memory-stream <c>UnsubscribeAsync</c>) MUST bridge it onto
    /// the mesh <c>IIoPool</c> inside <c>Dispose</c> — the async never leaks to the caller.
    /// </summary>
    /// <param name="address">Address to be registered for streaming.</param>
    /// <param name="callback">Callback to deliver messages from the stream.</param>
    /// <returns>A disposable which will unregister when disposed.</returns>
    IDisposable RegisterStream(Address address, AsyncDelivery callback);

    /// <summary>
    /// Registers a stream for the given address and returns immediately.
    /// </summary>
    IDisposable RegisterStream(Address address, SyncDelivery callback)
        => RegisterStream(address, (d, _) => Observable.Return(callback(d)));

    /// <summary>
    /// Easy-access overload to register a hub as a stream sink.
    /// </summary>
    IDisposable RegisterStream(IMessageHub hub)
        => RegisterStream(hub.Address, hub.DeliverMessage);

    /// <summary>
    /// Stream Namespace for incoming messages
    /// </summary>
    public const string MessageIn = nameof(MessageIn);

}
