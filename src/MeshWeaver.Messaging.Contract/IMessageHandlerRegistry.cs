namespace MeshWeaver.Messaging;

public interface IMessageHandlerRegistry
{
    IDisposable Register<TMessage>(SyncDelivery<TMessage> action);
    IDisposable Register<TMessage>(AsyncDelivery<TMessage> action);
    IDisposable Register<TMessage>(SyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter);
    IDisposable Register<TMessage>(AsyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter);
    IDisposable Register(Type tMessage, AsyncDelivery action);
    IDisposable Register(Type tMessage, AsyncDelivery action, DeliveryFilter filter);
    IDisposable Register(Type tMessage, SyncDelivery action);
    IDisposable Register(Type tMessage, SyncDelivery action, DeliveryFilter filter);
    IDisposable RegisterInherited<TMessage>(
        AsyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter = null
    );
    IDisposable RegisterInherited<TMessage>(
        SyncDelivery<TMessage> action,
        DeliveryFilter<TMessage> filter = null
    );
    IDisposable Register(SyncDelivery delivery);
    IDisposable Register(AsyncDelivery delivery);
}
