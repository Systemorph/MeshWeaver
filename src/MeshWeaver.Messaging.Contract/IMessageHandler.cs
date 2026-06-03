using System;
using System.Threading.Tasks;

namespace MeshWeaver.Messaging;


// Delivery rules are reactive: a handler maps a delivery to an IObservable that
// emits the (single) transformed delivery. Synchronous handlers project via
// Observable.Return; genuinely-async handlers delegate to the pool and return a
// ReplaySubject (see MeshWeaver.Messaging.DeliveryObservable.Run). The
// "AsyncDelivery" name is kept for continuity — "async" now means "may complete
// later" (reactive), not Task.
public delegate IObservable<IMessageDelivery> AsyncDelivery<in TMessage>(IMessageDelivery<TMessage> request, CancellationToken cancellationToken);
public delegate IMessageDelivery SyncDelivery<in TMessage>(IMessageDelivery<TMessage> request);
public delegate IObservable<IMessageDelivery> AsyncDelivery(IMessageDelivery request, CancellationToken cancellationToken);
public delegate IMessageDelivery SyncDelivery(IMessageDelivery request);
public delegate bool DeliveryFilter<in TMessage>(IMessageDelivery<TMessage> request);
public delegate bool DeliveryFilter(IMessageDelivery request);

public delegate IObservable<IMessageDelivery> AsyncRouteDelivery(Address routeAddress, IMessageDelivery request, CancellationToken cancellationToken);
public delegate IMessageDelivery SyncRouteDelivery(Address routeAddress, IMessageDelivery request);

public interface IMessageHandler<in TMessage>
{
    public IMessageDelivery HandleMessage(IMessageDelivery<TMessage> request);
}
public interface IMessageHandlerAsync<in TMessage>
{
    public IObservable<IMessageDelivery> HandleMessageAsync(IMessageDelivery<TMessage> request, CancellationToken cancellationToken);
}
