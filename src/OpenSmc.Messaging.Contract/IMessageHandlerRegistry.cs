namespace OpenSmc.Messaging;

public interface IMessageHandlerRegistry
{
    IMessageHandlerRegistry Register<TMessage>(SyncDelivery<TMessage> action);
    IMessageHandlerRegistry Register<TMessage>(AsyncDelivery<TMessage> action);
    IMessageHandlerRegistry Register<TMessage>(SyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter);
    IMessageHandlerRegistry Register<TMessage>(AsyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter);
    IMessageHandlerRegistry Register(Type tMessage, AsyncDelivery action);
    IMessageHandlerRegistry Register(Type tMessage, AsyncDelivery action, DeliveryFilter filter);
    IMessageHandlerRegistry Register(Type tMessage, SyncDelivery action);
    IMessageHandlerRegistry Register(Type tMessage, SyncDelivery action, DeliveryFilter filter);
    IMessageHandlerRegistry RegisterInherited<TMessage>(AsyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter = null);
    IMessageHandlerRegistry RegisterInherited<TMessage>(SyncDelivery<TMessage> action, DeliveryFilter<TMessage> filter = null);
    IMessageHandlerRegistry Register(SyncDelivery delivery);
    IMessageHandlerRegistry Register(AsyncDelivery delivery);
}