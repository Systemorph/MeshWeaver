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
    /// Property key under which <see cref="Failed(string, ErrorType)"/> records the classification
    /// the FAILING SITE decided on.
    /// </summary>
    public const string FailureErrorTypeProperty = "FailureErrorType";

    /// <summary>
    /// Property key under which <see cref="FailedAndNacked"/> records that the failing site has
    /// already answered the sender itself.
    /// </summary>
    public const string SenderNackedProperty = "SenderNacked";

    /// <summary>
    /// Transitions this delivery to <see cref="MessageDeliveryState.Failed"/>, recording BOTH the
    /// failure message and the <see cref="ErrorType"/> the failing site decided on.
    ///
    /// <para>The classification is decided WHERE THE CONDITION IS KNOWN and carried on the delivery,
    /// never reconstructed downstream by pattern-matching the message text — that drifts the moment
    /// someone rewords a string. It matters concretely: a disposal race MUST reach the sender as the
    /// transient <see cref="ErrorType.ShuttingDown"/> so consumers with their own recovery machinery
    /// (chiefly <c>SynchronizationStream</c>'s resubscribe latch) ride it out instead of tearing
    /// down.</para>
    ///
    /// <para>Using this overload also DECLARES that the failing site did NOT answer the sender —
    /// whoever finishes the delivery owes it a <see cref="DeliveryFailure"/>. A site that posts its
    /// own NACK must use <see cref="FailedAndNacked"/> instead, or the sender gets two.</para>
    /// </summary>
    /// <param name="message">The failure message.</param>
    /// <param name="errorType">The classification for the resulting <see cref="DeliveryFailure"/>.</param>
    /// <returns>The failed delivery, carrying its classification.</returns>
    IMessageDelivery Failed(string message, ErrorType errorType) =>
        Failed(message).WithProperty(FailureErrorTypeProperty, errorType);

    /// <summary>
    /// Transitions this delivery to <see cref="MessageDeliveryState.Failed"/> and records that the
    /// failing site has ALREADY posted its own <see cref="DeliveryFailure"/> to the sender, so
    /// downstream reporting must not NACK it a second time.
    ///
    /// <para>Used by the routing services, whose "no node at this address" path posts a
    /// <see cref="ErrorType.NotFound"/> NACK before failing the delivery. That path is hot — every
    /// message to a missing/undeployed node takes it — so a duplicate NACK there is a traffic
    /// multiplier, not a harmless extra log line.</para>
    /// </summary>
    /// <param name="message">The failure message.</param>
    /// <returns>The failed delivery, marked as already answered.</returns>
    IMessageDelivery FailedAndNacked(string message) =>
        Failed(message).WithProperty(SenderNackedProperty, true);

    /// <summary>
    /// True when the site that failed this delivery already answered the sender with its own
    /// <see cref="DeliveryFailure"/> (see <see cref="FailedAndNacked"/>).
    /// </summary>
    bool SenderWasNacked => Properties.ContainsKey(SenderNackedProperty);

    /// <summary>
    /// The classification recorded by <see cref="Failed(string, ErrorType)"/>, or
    /// <paramref name="fallback"/> when the failing site recorded none.
    /// </summary>
    /// <param name="fallback">The verdict to use when the failing site classified nothing.</param>
    /// <returns>The recorded classification, or <paramref name="fallback"/>.</returns>
    ErrorType GetFailureErrorType(ErrorType fallback) =>
        Properties.TryGetValue(FailureErrorTypeProperty, out var value) && value is ErrorType errorType
            ? errorType
            : fallback;

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
