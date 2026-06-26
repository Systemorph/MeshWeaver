using System;
using System.Threading.Tasks;

namespace MeshWeaver.Messaging;


// Delivery rules are reactive: a handler maps a delivery to an IObservable that
// emits the (single) transformed delivery. Synchronous handlers project via
// Observable.Return; genuinely-async handlers delegate to the pool and return a
// ReplaySubject (see MeshWeaver.Messaging.DeliveryObservable.Run). The
// "AsyncDelivery" name is kept for continuity — "async" now means "may complete
// later" (reactive), not Task.
/// <summary>
/// Reactive handler for a typed message, returning an observable that emits the
/// (single) transformed delivery once processing completes.
/// </summary>
/// <typeparam name="TMessage">The message type handled.</typeparam>
/// <param name="request">The incoming delivery.</param>
/// <param name="cancellationToken">Token to cancel processing.</param>
/// <returns>An observable emitting the resulting delivery.</returns>
public delegate IObservable<IMessageDelivery> AsyncDelivery<in TMessage>(IMessageDelivery<TMessage> request, CancellationToken cancellationToken);
/// <summary>
/// Synchronous handler for a typed message, returning the transformed delivery directly.
/// </summary>
/// <typeparam name="TMessage">The message type handled.</typeparam>
/// <param name="request">The incoming delivery.</param>
/// <returns>The resulting delivery.</returns>
public delegate IMessageDelivery SyncDelivery<in TMessage>(IMessageDelivery<TMessage> request);
/// <summary>
/// Reactive handler for an untyped message, returning an observable that emits the
/// (single) transformed delivery once processing completes.
/// </summary>
/// <param name="request">The incoming delivery.</param>
/// <param name="cancellationToken">Token to cancel processing.</param>
/// <returns>An observable emitting the resulting delivery.</returns>
public delegate IObservable<IMessageDelivery> AsyncDelivery(IMessageDelivery request, CancellationToken cancellationToken);
/// <summary>
/// Synchronous handler for an untyped message, returning the transformed delivery directly.
/// </summary>
/// <param name="request">The incoming delivery.</param>
/// <returns>The resulting delivery.</returns>
public delegate IMessageDelivery SyncDelivery(IMessageDelivery request);
/// <summary>
/// Predicate deciding whether a typed delivery should be handled by a registration.
/// </summary>
/// <typeparam name="TMessage">The message type filtered.</typeparam>
/// <param name="request">The incoming delivery.</param>
/// <returns>True to handle the delivery; false to skip it.</returns>
public delegate bool DeliveryFilter<in TMessage>(IMessageDelivery<TMessage> request);
/// <summary>
/// Predicate deciding whether an untyped delivery should be handled by a registration.
/// </summary>
/// <param name="request">The incoming delivery.</param>
/// <returns>True to handle the delivery; false to skip it.</returns>
public delegate bool DeliveryFilter(IMessageDelivery request);

/// <summary>
/// Reactive route handler that processes a delivery destined for a specific route address.
/// </summary>
/// <param name="routeAddress">The address the delivery is routed to.</param>
/// <param name="request">The incoming delivery.</param>
/// <param name="cancellationToken">Token to cancel processing.</param>
/// <returns>An observable emitting the resulting delivery.</returns>
public delegate IObservable<IMessageDelivery> AsyncRouteDelivery(Address routeAddress, IMessageDelivery request, CancellationToken cancellationToken);
/// <summary>
/// Synchronous route handler that processes a delivery destined for a specific route address.
/// </summary>
/// <param name="routeAddress">The address the delivery is routed to.</param>
/// <param name="request">The incoming delivery.</param>
/// <returns>The resulting delivery.</returns>
public delegate IMessageDelivery SyncRouteDelivery(Address routeAddress, IMessageDelivery request);

/// <summary>
/// Handler interface for synchronously processing a typed message delivery.
/// </summary>
/// <typeparam name="TMessage">The message type handled.</typeparam>
public interface IMessageHandler<in TMessage>
{
    /// <summary>
    /// Handles the given typed delivery and returns the resulting delivery.
    /// </summary>
    /// <param name="request">The incoming delivery.</param>
    /// <returns>The resulting delivery.</returns>
    public IMessageDelivery HandleMessage(IMessageDelivery<TMessage> request);
}
/// <summary>
/// Handler interface for reactively processing a typed message delivery.
/// </summary>
/// <typeparam name="TMessage">The message type handled.</typeparam>
public interface IMessageHandlerAsync<in TMessage>
{
    /// <summary>
    /// Handles the given typed delivery and returns an observable emitting the resulting delivery.
    /// </summary>
    /// <param name="request">The incoming delivery.</param>
    /// <param name="cancellationToken">Token to cancel processing.</param>
    /// <returns>An observable emitting the resulting delivery.</returns>
    public IObservable<IMessageDelivery> HandleMessageAsync(IMessageDelivery<TMessage> request, CancellationToken cancellationToken);
}
