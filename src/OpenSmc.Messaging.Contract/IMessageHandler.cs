using System;
using System.Threading.Tasks;

namespace OpenSmc.Messaging;

public interface IMessageHandler : IAsyncDisposable
{
    Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery request);
    void Connect(IMessageService messageService, object address);
}
