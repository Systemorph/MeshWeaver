using System.Collections.Immutable;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("MeshWeaver.Messaging.Hub")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting")]
[assembly: InternalsVisibleTo("MeshWeaver.Hosting.Orleans")]
[assembly: InternalsVisibleTo("MeshWeaver.AI")]

namespace MeshWeaver.Messaging;

/// <summary>
/// Envelope carrying a message through the hub pipeline: its sender, target,
/// delivery <see cref="State"/>, access context, routing path, and arbitrary
/// properties. Deliveries are immutable; the transition helpers return a new
/// delivery in the updated state.
/// </summary>
public interface IMessageDelivery
{
    private const string Error = nameof(Error);
    /// <summary>
    /// Arbitrary properties attached to this delivery (correlation, response keys, etc.).
    /// </summary>
    IReadOnlyDictionary<string, object> Properties { get; }
    /// <summary>
    /// Unique id of this delivery, used for correlating responses.
    /// </summary>
    string Id { get; }
    /// <summary>
    /// Address that sent the message.
    /// </summary>
    Address Sender { get; }
    /// <summary>
    /// Address the message is being delivered to, or null when unrouted.
    /// </summary>
    Address? Target { get; }
    /// <summary>
    /// Current delivery state in the pipeline.
    /// </summary>
    MessageDeliveryState State { get; }
    /// <summary>
    /// The message payload.
    /// </summary>
    object Message { get; }

    /// <summary>
    /// Returns a wire-ready copy of this delivery with the message packaged
    /// (serialized) for transport across a hub boundary.
    /// </summary>
    /// <returns>The packaged delivery.</returns>
    IMessageDelivery Package();

    /// <summary>
    /// Returns a wire-ready copy of this delivery with the message packaged (serialized) for
    /// transport, using <paramref name="fallbackOptions"/> when this delivery carries no captured
    /// serializer options. A delivery that was re-typed (<c>WithMessage</c>) or deserialized at a
    /// process boundary has NO captured options — packaging it with the runtime defaults would put
    /// PascalCase properties and record-shaped <c>RawJson</c> on the wire, which no client contract
    /// recognizes. Transports own their wire shape, so they pass their hub's options here.
    /// </summary>
    /// <param name="fallbackOptions">The transport hub's serializer options, used when the delivery
    /// captured none. Null keeps the delivery's own captured options (the parameterless overload).</param>
    /// <returns>The packaged delivery.</returns>
    IMessageDelivery Package(System.Text.Json.JsonSerializerOptions? fallbackOptions);

    /// <summary>
    /// Returns a copy of this delivery stamped with the given access context.
    /// </summary>
    /// <param name="accessObject">The caller's access context.</param>
    /// <returns>The delivery carrying the access context.</returns>
    IMessageDelivery SetAccessContext(AccessContext accessObject);
    internal IMessageDelivery ChangeState(MessageDeliveryState state);
    /// <summary>
    /// Returns a copy of this delivery with a single property set.
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">The property value.</param>
    /// <returns>The delivery with the property applied.</returns>
    IMessageDelivery SetProperty(string name, object value);
    /// <summary>
    /// Returns a copy of this delivery with the given properties merged in.
    /// </summary>
    /// <param name="properties">The properties to set.</param>
    /// <returns>The delivery with the properties applied.</returns>
    IMessageDelivery SetProperties(IReadOnlyDictionary<string, object> properties);
    /// <summary>
    /// Returns a copy of this delivery retargeted to the given address.
    /// </summary>
    /// <param name="target">The new target address.</param>
    /// <returns>The retargeted delivery.</returns>
    IMessageDelivery ForwardTo(Address target);
    /// <summary>
    /// Transitions this delivery to the <see cref="MessageDeliveryState.Failed"/>
    /// state, recording the failure message.
    /// </summary>
    /// <param name="message">The failure message.</param>
    /// <returns>The failed delivery.</returns>
    IMessageDelivery Failed(string message) => ChangeState(MessageDeliveryState.Failed).WithProperty(nameof(Error), message);
    /// <summary>
    /// Returns a copy of this delivery with a single property set.
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">The property value.</param>
    /// <returns>The delivery with the property applied.</returns>
    IMessageDelivery WithProperty(string name, object value);
    /// <summary>
    /// Transitions this delivery to the <see cref="MessageDeliveryState.Forwarded"/> state.
    /// </summary>
    /// <returns>The forwarded delivery.</returns>
    IMessageDelivery Forwarded() => ChangeState(MessageDeliveryState.Forwarded);
    /// <summary>
    /// Transitions this delivery to the <see cref="MessageDeliveryState.Submitted"/> state.
    /// </summary>
    /// <returns>The submitted delivery.</returns>
    IMessageDelivery Submitted() => ChangeState(MessageDeliveryState.Submitted);
    /// <summary>
    /// Transitions this delivery to the <see cref="MessageDeliveryState.NotFound"/> state.
    /// </summary>
    /// <returns>The not-found delivery.</returns>
    IMessageDelivery NotFound() => ChangeState(MessageDeliveryState.NotFound);
    /// <summary>
    /// Transitions this delivery to the <see cref="MessageDeliveryState.Processed"/> state.
    /// </summary>
    /// <returns>The processed delivery.</returns>
    IMessageDelivery Processed() => ChangeState(MessageDeliveryState.Processed);
    /// <summary>
    /// Transitions this delivery to the <see cref="MessageDeliveryState.Rejected"/> state.
    /// </summary>
    /// <returns>The rejected delivery.</returns>
    IMessageDelivery Rejected() => ChangeState(MessageDeliveryState.Rejected);
    /// <summary>
    /// Transitions this delivery to the <see cref="MessageDeliveryState.Ignored"/> state.
    /// </summary>
    /// <returns>The ignored delivery.</returns>
    IMessageDelivery Ignored() => ChangeState(MessageDeliveryState.Ignored);

    /// <summary>
    /// Returns a copy of this delivery carrying a different message payload while
    /// preserving id, sender, target, and properties.
    /// </summary>
    /// <param name="message">The replacement message payload.</param>
    /// <returns>The delivery with the new message.</returns>
    IMessageDelivery WithMessage(object message);
    internal IMessageDelivery WithSender(Address address);
    internal IMessageDelivery WithTarget(Address address);
    /// <summary>
    /// Marks this delivery as forwarded, appending the given addresses to the routing path.
    /// </summary>
    /// <param name="addresses">The addresses the delivery is forwarded through.</param>
    /// <returns>The forwarded delivery.</returns>
    IMessageDelivery Forwarded(params IEnumerable<Address> addresses);
    /// <summary>
    /// The access context (caller identity) carried by this delivery, or null when none is set.
    /// </summary>
    AccessContext? AccessContext { get; }

    /// <summary>
    /// Returns a copy of this delivery with the given address appended to the routing
    /// path; used for routing-loop detection.
    /// </summary>
    /// <param name="address">The address to append.</param>
    /// <returns>The delivery with the updated routing path.</returns>
    IMessageDelivery AddToRoutingPath(Address address);
    /// <summary>
    /// The ordered list of addresses this delivery has been routed through.
    /// </summary>
    ImmutableList<Address> RoutingPath { get; }

}

/// <summary>
/// Strongly-typed message delivery exposing the payload as <typeparamref name="TMessage"/>.
/// </summary>
/// <typeparam name="TMessage">The message payload type.</typeparam>
public interface IMessageDelivery<out TMessage> : IMessageDelivery
{
    /// <summary>
    /// The strongly-typed message payload.
    /// </summary>
    new TMessage Message { get; }
}
