using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace OpenSmc.Messaging;

public interface IMessageDelivery
{
    private const string Error = nameof(Error);
    ImmutableDictionary<string, object> Properties { get; }
    string Id { get; }
    object Sender { get; }
    object Target { get; }
    string State { get; }
    object Message { get; }

    string AccessObject { get; }

    internal IMessageDelivery SetAccessObject(string accessObject, object address);
    internal IMessageDelivery ChangeState(string state);
    IMessageDelivery SetProperty(string name, object value);
    IMessageDelivery ForwardTo(object target);
    IMessageDelivery Failed(string message) => ChangeState(MessageDeliveryState.Failed).WithProperty(nameof(Error), message);
    IMessageDelivery WithProperty(string name, object value);
    IMessageDelivery Forwarded() => ChangeState(MessageDeliveryState.Forwarded);
    IMessageDelivery Submitted() => ChangeState(MessageDeliveryState.Submitted);
    IMessageDelivery NotFound() => ChangeState(MessageDeliveryState.NotFound);
    IMessageDelivery Processed() => ChangeState(MessageDeliveryState.Processed);
    IMessageDelivery Rejected() => ChangeState(MessageDeliveryState.Rejected);
    IMessageDelivery Ignored() => ChangeState(MessageDeliveryState.Ignored);

    IMessageDelivery Copy<TMessage>( TMessage message, Func<PostOptions, PostOptions> postOptions);
    IMessageDelivery WithMessage(object message);
    internal IMessageDelivery WithRoutedSender(object address);
    internal IMessageDelivery WithRoutedTarget(object address);
    IReadOnlyCollection<object> ToBeForwarded(IEnumerable<object> addresses);
    IMessageDelivery Forwarded(IEnumerable<object> addresses);
    IMessageDelivery Mask(object hostAddress);
}

public interface IMessageDelivery<out TMessage> : IMessageDelivery
{
    new TMessage Message { get; }
}
