using System.Runtime.CompilerServices;
using System.Text.Json;

[assembly: InternalsVisibleTo("MeshWeaver.Messaging.Hub")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting")]

namespace MeshWeaver.Messaging;

public interface IMessageDelivery
{
    private const string Error = nameof(Error);
    IReadOnlyDictionary<string, object> Properties { get; }
    string Id { get; }
    Address Sender { get; }
    Address Target { get; }
    string State { get; }
    object Message { get; }

    IMessageDelivery Package(JsonSerializerOptions options);

    internal IMessageDelivery SetAccessContext(AccessContext accessObject);
    internal IMessageDelivery ChangeState(string state);
    IMessageDelivery SetProperty(string name, object value);
    IMessageDelivery SetProperties(IReadOnlyDictionary<string, object> properties);
    IMessageDelivery ForwardTo(Address target);
    IMessageDelivery Failed(string message) => ChangeState(MessageDeliveryState.Failed).WithProperty(nameof(Error), message);
    IMessageDelivery WithProperty(string name, object value);
    IMessageDelivery Forwarded() => ChangeState(MessageDeliveryState.Forwarded);
    IMessageDelivery Submitted() => ChangeState(MessageDeliveryState.Submitted);
    IMessageDelivery NotFound() => ChangeState(MessageDeliveryState.NotFound);
    IMessageDelivery Processed() => ChangeState(MessageDeliveryState.Processed);
    IMessageDelivery Rejected() => ChangeState(MessageDeliveryState.Rejected);
    IMessageDelivery Ignored() => ChangeState(MessageDeliveryState.Ignored);

    IMessageDelivery WithMessage(object message);
    internal IMessageDelivery WithSender(Address address);
    internal IMessageDelivery WithTarget(Address address);
    IMessageDelivery Forwarded(params IEnumerable<Address> addresses);
    AccessContext AccessContext { get; }

}

public interface IMessageDelivery<out TMessage> : IMessageDelivery
{
    new TMessage Message { get; }
}
