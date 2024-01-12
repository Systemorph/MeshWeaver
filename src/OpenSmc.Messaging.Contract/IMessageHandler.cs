using System;
using System.Threading.Tasks;

namespace OpenSmc.Messaging;

public interface IMessageHandler : IAsyncDisposable
{
    Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery request);
    void Connect(IMessageService messageService, object address);
}

public delegate Task<IMessageDelivery> AsyncDelivery<in TMessage>(IMessageDelivery<TMessage> request);
public delegate IMessageDelivery SyncDelivery<in TMessage>(IMessageDelivery<TMessage> request);
public delegate Task<IMessageDelivery> AsyncDelivery(IMessageDelivery request);
public delegate IMessageDelivery SyncDelivery(IMessageDelivery request);
