using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Messaging;

public abstract record MessageDelivery : IMessageDelivery
{
    private readonly JsonSerializerOptions options;

    protected MessageDelivery(Address Sender, Address Target, JsonSerializerOptions options)
    {
        this.options = options;
        this.Sender = Sender;
        this.Target = Target;
    }

    public string Id { get; init; } = Guid.NewGuid().AsString();

    private ImmutableDictionary<string, object> PropertiesImpl { get; init; } =
        ImmutableDictionary<string, object>.Empty;
    public IReadOnlyDictionary<string, object> Properties { get => PropertiesImpl; init => PropertiesImpl = value.ToImmutableDictionary(); }
    public MessageDeliveryState State { get; init; } = MessageDeliveryState.Submitted;

    object IMessageDelivery.Message => GetMessage();
    protected abstract object GetMessage();

    IMessageDelivery IMessageDelivery.ChangeState(MessageDeliveryState state)
    {
        return this with { State = state };
    }

    public object? AccessProvidedBy { get; init; }
    public AccessContext? AccessContext { get; init; } // TODO SMCv2: later on we might think about accessibility for this property (2023/10/04, Dmitry Kalabin)

    IMessageDelivery IMessageDelivery.SetAccessContext(AccessContext accessObject) => this with { AccessContext = accessObject };

    IMessageDelivery IMessageDelivery.SetProperty(string name, object value)
    {
        return this with { Properties = PropertiesImpl.SetItem(name, value) };
    }

    public IMessageDelivery ForwardTo(Address target)
        => this with { Target = target, State = MessageDeliveryState.Submitted };

    public IMessageDelivery WithProperty(string name, object value)
        => this with { Properties = PropertiesImpl.SetItem(name, value) };

    public IMessageDelivery SetProperties(IReadOnlyDictionary<string, object> properties)
    => this with { Properties = PropertiesImpl.AddRange(properties) };


    private ImmutableHashSet<object> ForwardedTo { get; init; } = ImmutableHashSet<object>.Empty;
    public ImmutableList<Address> RoutingPath { get; init; } = ImmutableList<Address>.Empty;
    public Address Sender { get; init; }
    public Address Target { get; init; }

    IMessageDelivery IMessageDelivery.Forwarded(IEnumerable<Address> addresses) => this with { ForwardedTo = ForwardedTo.Union(addresses), State = MessageDeliveryState.Forwarded };

    public IMessageDelivery AddToRoutingPath(Address address)
    {
        return this with { RoutingPath = RoutingPath.Add(address) };
    }



    IMessageDelivery IMessageDelivery.WithSender(Address address)
    {
        return this with { Sender = address };
    }
    IMessageDelivery IMessageDelivery.WithTarget(Address address)
    {
        return this with { Target = address };
    }

    private static readonly MethodInfo WithMessageMethod = typeof(MessageDelivery).GetMethod(nameof(WithMessageImpl), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public IMessageDelivery WithMessage(object message)
    {
        return (IMessageDelivery)WithMessageMethod.MakeGenericMethod(message.GetType()).Invoke(this, new[] { message })!;
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
            RoutingPath = RoutingPath,
        };
    }

    public IMessageDelivery Package()
    {
        try
        {
            var message = GetMessage();
            if (message is RawJson)
                return this;
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

public record MessageDelivery<TMessage> : MessageDelivery, IMessageDelivery<TMessage>
{

    public MessageDelivery()
        : this(null!, null!, default!, null!)
    {
    }

    public MessageDelivery(TMessage message, PostOptions options, JsonSerializerOptions jsonSerializerOptions)
        : this(options.Sender, options.Target, message, jsonSerializerOptions)
    {
        Properties = options.Properties;
    }

    public MessageDelivery(Address Sender, Address Target, TMessage Message, JsonSerializerOptions jsonSerializerOptions) : base(Sender, Target, jsonSerializerOptions)
    {
        this.Message = Message;
    }

    public TMessage Message { get; init; }

    protected override object GetMessage()
    {
        return Message!;
    }

}
