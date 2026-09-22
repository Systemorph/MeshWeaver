using System.Reactive.Disposables;
using System.Reactive.Linq;

namespace MeshWeaver.Messaging;

/// <summary>
/// Holds a subscription on a hub's disposal composite only for as long as the subscription is
/// live (#3432).
/// </summary>
public static class HubHeldSubscriptionExtensions
{
    /// <summary>
    /// Subscribes to <paramref name="source"/> through <paramref name="subscribe"/> and couples the
    /// subscription to <paramref name="hub"/>'s lifetime UNTIL IT TERMINATES — then detaches it, so
    /// the hub stops holding it (and everything its closures captured).
    ///
    /// <para>🚨 This is the shape for every per-request subscription a handler used to hand to
    /// <see cref="IMessageHub.RegisterForDisposal(IDisposable)"/>. That composite is append-only, so
    /// the hub held one entry per request served for its whole life — measured at +1 per
    /// <c>GetDataRequest</c>, +2 per <c>DataChangeRequest</c>, with every request long answered.
    /// Disposing a terminated subscription does nothing, so detaching it at its terminal changes no
    /// behaviour: a subscription still live when the hub goes down is disposed exactly as
    /// before.</para>
    ///
    /// <para>Not for a registrant whose disposal DOES something beyond cancelling the subscription
    /// (a teardown NACK, say): that one may be detached only once its own answer gate is claimed —
    /// use <see cref="IMessageHub.RegisterForDisposalDetachable"/> directly.</para>
    /// </summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="hub">The hub whose teardown must cancel the subscription while it is live.</param>
    /// <param name="source">The observable to subscribe to.</param>
    /// <param name="subscribe">Performs the actual subscription (observer arms and all).</param>
    /// <returns>The subscription.</returns>
    public static IDisposable SubscribeHeldUntilTerminal<T>(
        this IMessageHub hub,
        IObservable<T> source,
        Func<IObservable<T>, IDisposable> subscribe)
    {
        // Assigned after the registration below; a subscription that terminates synchronously,
        // inside subscribe(), disposes this first — and SingleAssignmentDisposable then disposes
        // the handle the moment it is assigned, so the detach is never lost to the ordering.
        var detach = new SingleAssignmentDisposable();
        var subscription = subscribe(source.Finally(detach.Dispose));
        detach.Disposable = hub.RegisterForDisposalDetachable(subscription);
        return subscription;
    }
}
