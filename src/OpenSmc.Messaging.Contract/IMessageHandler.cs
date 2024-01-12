using System;
using System.Threading.Tasks;

namespace OpenSmc.Messaging;

// TODO: Delete ==> make it part of plugin framework
public interface IMessageHandler : IAsyncDisposable
{
    Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery request);
    void Connect(IMessageService messageService, object address);
}

public delegate Task<IMessageDelivery> AsyncDelivery<in TMessage>(IMessageDelivery<TMessage> request);
public delegate IMessageDelivery SyncDelivery<in TMessage>(IMessageDelivery<TMessage> request);
public delegate Task<IMessageDelivery> AsyncDelivery(IMessageDelivery request);
public delegate IMessageDelivery SyncDelivery(IMessageDelivery request);
public delegate bool DeliveryFilter<in TMessage>(IMessageDelivery<TMessage> request);
public delegate bool DeliveryFilter(IMessageDelivery request);
