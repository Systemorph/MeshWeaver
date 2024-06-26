using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using OpenSmc.ShortGuid;

namespace OpenSmc.Messaging;

public abstract record MessageDelivery(object Sender, object Target) : IMessageDelivery
{
    public string Id { get; init; } = Guid.NewGuid().AsString();
    public ImmutableDictionary<string, object> Properties { get; init; } = ImmutableDictionary<string, object>.Empty;
    public string State { get; init; } = MessageDeliveryState.Submitted;

    object IMessageDelivery.Message => GetMessage();
    protected abstract object GetMessage();

    IMessageDelivery IMessageDelivery.ChangeState(string state)
    {
        return this with { State = state };
    }

    public object AccessProvidedBy { get; init; }
    public string AccessObject { get; init; } // TODO SMCv2: later on we might think about accessibility for this property (2023/10/04, Dmitry Kalabin)

    IMessageDelivery IMessageDelivery.SetAccessObject(string accessObject, object address) => this with { AccessObject = accessObject, AccessProvidedBy = address, };

    IMessageDelivery IMessageDelivery.SetProperty(string name, object value)
    {
        return this with { Properties = Properties.SetItem(name, value) };
    }

    public IMessageDelivery ForwardTo(object target)
        => this with { Target = target, State = MessageDeliveryState.Submitted };

    public IMessageDelivery WithProperty(string name, object value)
        => this with { Properties = Properties.SetItem(name, value) };

    private ImmutableHashSet<object> ForwardedTo { get; init; } = ImmutableHashSet<object>.Empty;

    IReadOnlyCollection<object> IMessageDelivery.ToBeForwarded(IEnumerable<object> addresses) => addresses.Where(a => !ForwardedTo.Contains(a)).ToArray();
    IMessageDelivery IMessageDelivery.Forwarded(IEnumerable<object> addresses) => this with { ForwardedTo = ForwardedTo.Union(addresses), State = MessageDeliveryState.Forwarded };



    IMessageDelivery IMessageDelivery.WithRoutedSender(object address)
    {
        return this with { Sender = address };
    }
    IMessageDelivery IMessageDelivery.WithRoutedTarget(object address)
    {
        return this with { Target = address };
    }

    private static readonly MethodInfo WithMessageMethod = typeof(MessageDelivery).GetMethod(nameof(WithMessageImpl), BindingFlags.NonPublic | BindingFlags.Instance);

    public IMessageDelivery WithMessage(object message)
    {
        return (IMessageDelivery)WithMessageMethod.MakeGenericMethod(message.GetType()).Invoke(this, new[] { message });
    }

    private IMessageDelivery<TMessage> WithMessageImpl<TMessage>(TMessage message)
    {
        return new MessageDelivery<TMessage>
        {
            Message = message,
            State = State,
            Target = Target,
            Sender = Sender,
            Properties = Properties,
            Id = Id,
            ForwardedTo = ForwardedTo,
        };
    }

    public IMessageDelivery Package(JsonSerializerOptions options)
    {
        try
        {
            var message = GetMessage();
            var serialized = JsonSerializer.Serialize(message, options);
            var rawJson = new RawJson(serialized);
            return WithMessage(rawJson);
        }
        catch (Exception e)
        {
            return ((IMessageDelivery)this).Failed($"Error serializing: \n{e}");
        }
    }

}

public record MessageDelivery<TMessage>(object Sender, object Target, TMessage Message) : MessageDelivery(Sender, Target), IMessageDelivery<TMessage>
{
    public MessageDelivery()
        : this(default, default, default)
    {
    }

    public MessageDelivery(TMessage message, PostOptions options)
        : this(options.Sender, options.Target, message)
    {
        Properties = options.Properties;
    }

    protected override object GetMessage()
    {
        return Message;
    }
}
