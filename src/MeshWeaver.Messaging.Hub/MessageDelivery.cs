using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Messaging;

/// <summary>
/// The envelope that carries a message through the mesh from a sender <see cref="Address"/>
/// to a target <see cref="Address"/>. Immutable record: every mutation (forwarding, retargeting,
/// property changes, state transitions) returns a new instance. The abstract base is non-generic;
/// the strongly-typed payload lives on the generic <see cref="MessageDelivery{TMessage}"/> subclass.
/// </summary>
public abstract record MessageDelivery : IMessageDelivery
{
    private readonly JsonSerializerOptions options;

    /// <summary>
    /// Initializes a new delivery envelope.
    /// </summary>
    /// <param name="Sender">The address the message originates from.</param>
    /// <param name="Target">The address the message is destined for.</param>
    /// <param name="options">JSON serializer options used when the envelope is packaged (serialized) for transport.</param>
    protected MessageDelivery(Address Sender, Address Target, JsonSerializerOptions options)
    {
        this.options = options;
        this.Sender = Sender;
        this.Target = Target;
    }

    /// <summary>
    /// Unique identifier for this delivery, assigned at creation. Used to correlate
    /// responses back to their originating request.
    /// </summary>
    public string Id { get; init; } = Guid.NewGuid().AsString();


    object IMessageDelivery.Message => GetMessage();
    /// <summary>
    /// Returns the message payload carried by this envelope. Implemented by the generic
    /// subclass to expose its strongly-typed <c>Message</c> as an <see cref="object"/>.
    /// </summary>
    /// <returns>The boxed message payload.</returns>
    protected abstract object GetMessage();

    IMessageDelivery IMessageDelivery.ChangeState(MessageDeliveryState state)
    {
        return this with { State = state };
    }

    /// <summary>
    /// Optional marker indicating which entity granted access for this delivery. May be null
    /// when access provenance is not tracked for the message.
    /// </summary>
    public object? AccessProvidedBy { get; init; }
    /// <summary>
    /// The access/identity context under which this delivery is processed, i.e. who the
    /// message is attributed to. May be null when no identity has been stamped.
    /// </summary>
    public AccessContext? AccessContext { get; init; } // TODO SMCv2: later on we might think about accessibility for this property (2023/10/04, Dmitry Kalabin)

    IMessageDelivery IMessageDelivery.SetAccessContext(AccessContext accessObject) => this with { AccessContext = accessObject };

    IMessageDelivery IMessageDelivery.SetProperty(string name, object value)
    {
        return this with { Properties = PropertiesImpl.SetItem(name, value) };
    }

    /// <summary>
    /// Returns a copy of this delivery retargeted to <paramref name="target"/> and reset to
    /// the <c>Submitted</c> state, ready to be routed onward to a new recipient.
    /// </summary>
    /// <param name="target">The new target address to forward the message to.</param>
    /// <returns>A new delivery addressed to <paramref name="target"/>.</returns>
    public IMessageDelivery ForwardTo(Address target)
        => this with { Target = target, State = MessageDeliveryState.Submitted };

    /// <summary>
    /// Returns a copy of this delivery with the property <paramref name="name"/> set to
    /// <paramref name="value"/> (added or overwritten).
    /// </summary>
    /// <param name="name">The property key.</param>
    /// <param name="value">The property value.</param>
    /// <returns>A new delivery carrying the updated property.</returns>
    public IMessageDelivery WithProperty(string name, object value)
        => this with { Properties = PropertiesImpl.SetItem(name, value) };

    /// <summary>
    /// Returns a copy of this delivery with all entries from <paramref name="properties"/>
    /// merged into its property bag (existing keys overwritten).
    /// </summary>
    /// <param name="properties">The properties to add or overwrite.</param>
    /// <returns>A new delivery carrying the merged properties.</returns>
    public IMessageDelivery SetProperties(IReadOnlyDictionary<string, object> properties)
    => this with { Properties = PropertiesImpl.AddRange(properties) };


    private ImmutableHashSet<object> ForwardedTo { get; init; } = ImmutableHashSet<object>.Empty;
    /// <summary>
    /// Ordered list of addresses this delivery has traversed, in hop order. Used for routing
    /// diagnostics and loop detection.
    /// </summary>
    public ImmutableList<Address> RoutingPath { get; init; } = ImmutableList<Address>.Empty;
    /// <summary>The address the message originates from.</summary>
    public Address Sender { get; init; }
    /// <summary>The address the message is currently destined for.</summary>
    public Address Target { get; init; }

    IMessageDelivery IMessageDelivery.Forwarded(IEnumerable<Address> addresses) => this with { ForwardedTo = ForwardedTo.Union(addresses), State = MessageDeliveryState.Forwarded };

    /// <summary>
    /// Returns a copy of this delivery with <paramref name="address"/> appended to its
    /// <see cref="RoutingPath"/>, recording another hop in the route.
    /// </summary>
    /// <param name="address">The address to append to the routing path.</param>
    /// <returns>A new delivery with the extended routing path.</returns>
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

    /// <summary>
    /// Returns a new delivery carrying <paramref name="message"/> as its payload while
    /// preserving this envelope's id, sender, target, properties, state and routing metadata.
    /// The concrete generic type is resolved from the runtime type of <paramref name="message"/>.
    /// </summary>
    /// <param name="message">The replacement message payload.</param>
    /// <returns>A new strongly-typed delivery wrapping <paramref name="message"/>.</returns>
    public IMessageDelivery WithMessage(object message)
    {
        return (IMessageDelivery)WithMessageMethod.MakeGenericMethod(message.GetType()).Invoke(this, new[] { message })!;
    }

    private IMessageDelivery<TMessage> WithMessageImpl<TMessage>(TMessage message)
    {
        // Carry the captured serializer options into the re-typed delivery — dropping them here
        // made Package() fall back to the runtime defaults (PascalCase, record-shaped RawJson),
        // shipping a wire frame no client contract recognizes.
        return new MessageDelivery<TMessage>(Sender, Target!, message, options)
        {
            State = State,
            Properties = Properties,
            Id = Id,
            ForwardedTo = ForwardedTo,
            RoutingPath = RoutingPath,
            AccessContext = AccessContext,
        };
    }

    /// <summary>
    /// Serializes the message payload into a <c>RawJson</c> envelope ready for transport across
    /// a process/serialization boundary. Already-raw payloads pass through unchanged; a
    /// serialization failure returns a failed delivery describing the error rather than throwing.
    /// </summary>
    /// <returns>This delivery with its payload replaced by serialized <c>RawJson</c>, or a failed delivery on error.</returns>
    public IMessageDelivery Package() => Package(null);

    /// <summary>
    /// Serializes the message payload into a <c>RawJson</c> envelope, preferring the options this
    /// delivery captured at creation and falling back to <paramref name="fallbackOptions"/> when a
    /// re-typed / boundary-deserialized delivery carries none. Without the fallback such a
    /// delivery serialized with the runtime defaults — PascalCase properties, <c>RawJson</c> as a
    /// <c>{"Content": …}</c> record — a wire shape no client fold recognizes (the gRPC-web live
    /// takeover silently rendered nothing).
    /// </summary>
    /// <param name="fallbackOptions">The transport hub's serializer options, used when the delivery
    /// captured none. Null keeps the delivery's own captured options.</param>
    /// <returns>This delivery with its payload replaced by serialized <c>RawJson</c>, or a failed delivery on error.</returns>
    public IMessageDelivery Package(JsonSerializerOptions? fallbackOptions)
    {
        try
        {
            var message = GetMessage();
            if (message is RawJson)
                return this;
            var serialized = JsonSerializer.Serialize(message, options ?? fallbackOptions);
            var rawJson = new RawJson(serialized);
            return WithMessage(rawJson);
        }
        catch (Exception e)
        {
            return ((IMessageDelivery)this).Failed($"Error serializing: \n{e}");
        }
    }
    private ImmutableDictionary<string, object> PropertiesImpl { get; init; } =
        ImmutableDictionary<string, object>.Empty;
    /// <summary>
    /// Arbitrary key/value metadata attached to this delivery (e.g. request-id correlation).
    /// Backed by an immutable dictionary; assigning copies the supplied values.
    /// </summary>
    public IReadOnlyDictionary<string, object> Properties { get => PropertiesImpl; init => PropertiesImpl = value.ToImmutableDictionary(); }
    /// <summary>
    /// The delivery's lifecycle state (e.g. Submitted, Forwarded, Processed, Failed),
    /// updated as it moves through the routing pipeline.
    /// </summary>
    public MessageDeliveryState State { get; init; } = MessageDeliveryState.Submitted;

}

/// <summary>
/// Strongly-typed delivery envelope carrying a payload of type <typeparamref name="TMessage"/>.
/// Concrete implementation of the abstract <see cref="MessageDelivery"/> base.
/// </summary>
/// <typeparam name="TMessage">The type of the message payload.</typeparam>
public record MessageDelivery<TMessage> : MessageDelivery, IMessageDelivery<TMessage>
{

    /// <summary>
    /// Parameterless constructor producing an empty envelope (null sender/target/message
    /// and default serializer options). Primarily used by deserialization.
    /// </summary>
    public MessageDelivery()
        : this(null!, null!, default!, null!)
    {
    }

    /// <summary>
    /// Creates a delivery from a message and the <see cref="PostOptions"/> describing how it is
    /// being posted, copying the sender, target, properties and any impersonation context.
    /// </summary>
    /// <param name="message">The message payload.</param>
    /// <param name="options">The post options supplying sender, target, properties and optional impersonation context.</param>
    /// <param name="jsonSerializerOptions">JSON serializer options used when the envelope is packaged for transport.</param>
    public MessageDelivery(TMessage message, PostOptions options, JsonSerializerOptions jsonSerializerOptions)
        : this(options.Sender, options.Target, message, jsonSerializerOptions)
    {
        Properties = options.Properties;
        if (options.ImpersonateContext is not null)
            AccessContext = options.ImpersonateContext;
    }

    /// <summary>
    /// Creates a delivery with explicit sender, target and payload.
    /// </summary>
    /// <param name="Sender">The address the message originates from.</param>
    /// <param name="Target">The address the message is destined for.</param>
    /// <param name="Message">The message payload.</param>
    /// <param name="jsonSerializerOptions">JSON serializer options used when the envelope is packaged for transport.</param>
    public MessageDelivery(Address Sender, Address Target, TMessage Message, JsonSerializerOptions jsonSerializerOptions) : base(Sender, Target, jsonSerializerOptions)
    {
        this.Message = Message;
    }

    /// <summary>The strongly-typed message payload carried by this envelope.</summary>
    public TMessage Message { get; init; }

    /// <summary>
    /// Returns the <see cref="Message"/> payload as a boxed <see cref="object"/>, satisfying the
    /// non-generic base contract.
    /// </summary>
    /// <returns>The boxed message payload.</returns>
    protected override object GetMessage()
    {
        return Message!;
    }

}
