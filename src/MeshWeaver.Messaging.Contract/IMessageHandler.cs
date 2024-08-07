using System;
using System.Threading.Tasks;

namespace MeshWeaver.Messaging;


public delegate Task<IMessageDelivery> AsyncDelivery<in TMessage>(IMessageDelivery<TMessage> request, CancellationToken cancellationToken);
public delegate IMessageDelivery SyncDelivery<in TMessage>(IMessageDelivery<TMessage> request);
public delegate Task<IMessageDelivery> AsyncDelivery(IMessageDelivery request, CancellationToken cancellationToken);
public delegate IMessageDelivery SyncDelivery(IMessageDelivery request);
public delegate bool DeliveryFilter<in TMessage>(IMessageDelivery<TMessage> request);
public delegate bool DeliveryFilter(IMessageDelivery request);

public delegate Task<IMessageDelivery> AsyncRouteDelivery<in TAddress>(TAddress routeAddress, IMessageDelivery request, CancellationToken cancellationToken);
public delegate IMessageDelivery SyncRouteDelivery<in TAddress>(TAddress routeAddress, IMessageDelivery request);

public interface IMessageHandler<in TMessage>
{
    public IMessageDelivery HandleMessage(IMessageDelivery<TMessage> request);
}
public interface IMessageHandlerAsync<in TMessage>
{
    public Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery<TMessage> request, CancellationToken cancellationToken);
}
