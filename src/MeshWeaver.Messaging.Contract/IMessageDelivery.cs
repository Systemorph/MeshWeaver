﻿using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("MeshWeaver.Messaging.Hub")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Monolith")]

namespace MeshWeaver.Messaging;

public interface IMessageDelivery
{
    private const string Error = nameof(Error);
    IReadOnlyDictionary<string, object> Properties { get; }
    string Id { get; }
    object Sender { get; }
    object Target { get; }
    string State { get; }
    object Message { get; }

    string AccessObject { get; }
    IMessageDelivery Package(JsonSerializerOptions options);

    internal IMessageDelivery SetAccessObject(string accessObject, object address);
    internal IMessageDelivery ChangeState(string state);
    IMessageDelivery SetProperty(string name, object value);
    IMessageDelivery SetProperties(IReadOnlyDictionary<string, object> properties);
    IMessageDelivery ForwardTo(object target);
    IMessageDelivery Failed(string message) => ChangeState(MessageDeliveryState.Failed).WithProperty(nameof(Error), message);
    IMessageDelivery WithProperty(string name, object value);
    IMessageDelivery Forwarded() => ChangeState(MessageDeliveryState.Forwarded);
    IMessageDelivery Submitted() => ChangeState(MessageDeliveryState.Submitted);
    IMessageDelivery NotFound() => ChangeState(MessageDeliveryState.NotFound);
    IMessageDelivery Processed() => ChangeState(MessageDeliveryState.Processed);
    IMessageDelivery Rejected() => ChangeState(MessageDeliveryState.Rejected);
    IMessageDelivery Ignored() => ChangeState(MessageDeliveryState.Ignored);

    IMessageDelivery WithMessage(object message);
    internal IMessageDelivery WithSender(object address);
    internal IMessageDelivery WithTarget(object address);
    IReadOnlyCollection<object> ToBeForwarded(IEnumerable<object> addresses);
    IMessageDelivery Forwarded(IEnumerable<object> addresses);
}

public interface IMessageDelivery<out TMessage> : IMessageDelivery
{
    new TMessage Message { get; }
}
