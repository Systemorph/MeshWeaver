using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data.Serialization;

/// <summary>
/// Which party a delivery on a stream's synchronization hub must come from, when the stream takes
/// its subscriber's input only (<see cref="StreamConfiguration{TStream}.WithInputFromSubscriberOnly"/>).
/// </summary>
internal enum StreamDeliveryRole
{
    /// <summary>
    /// Input to the stream from outside it: accepted only when the delivery carries the identity
    /// the stream was subscribed under. No exemption by sender.
    /// </summary>
    SubscriberInput,

    /// <summary>
    /// The stream's own write path: accepted only when this stream's own hub posted it.
    /// </summary>
    OwnWrite,

    /// <summary>
    /// The end of the subscription, or an answer to a delivery the stream's hub sent: accepted from
    /// the subscriber's identity, or from the mesh's own hubs (a delivery that did not enter
    /// through a participant connection — see <see cref="ParticipantIngress"/>).
    /// </summary>
    ReleaseOrAnswer,

    /// <summary>
    /// A type <see cref="StreamInputRule"/> does not name: the hub's own framework messages, and a
    /// handler registered on the hub at run time (<c>IMessageHub.Register</c>) rather than in the
    /// stream's hub configuration. Held to the <see cref="ReleaseOrAnswer"/> rule — accepted from
    /// the subscriber's identity or from the mesh's own hubs, refused from any other participant
    /// connection — so an unclassified type is never open to another participant.
    /// </summary>
    Unclassified,
}

/// <summary>
/// The explicit classification of every message type a synchronization hub handles, read by the
/// input rule (see <c>SynchronizationStream.AcceptInputFromSubscriberOnly</c>). Two guarantees
/// keep it closed: the stream refuses to configure its hub with a handler (registered in its hub
/// configuration) for a type that is not listed, so such a handler cannot ship unclassified; and
/// any type that reaches the hub without being listed — its framework messages, a handler
/// registered on the hub at run time — is held to the <see cref="StreamDeliveryRole.Unclassified"/>
/// rule, so it is never open to a participant connection other than the subscriber's. The one
/// run-time registration the platform makes, <c>LayoutAreaHost</c>'s click / blur / dialog
/// handlers, is for <see cref="IUserAction"/> types, which are listed.
///
/// <list type="table">
/// <listheader><term>Message</term><description>Role and reason</description></listheader>
/// <item><term><see cref="IUserAction"/></term><description><see cref="StreamDeliveryRole.SubscriberInput"/>:
/// a person's click, blur or dialog dismissal on the area.</description></item>
/// <item><term><see cref="PatchDataChangeRequest"/></term><description><see cref="StreamDeliveryRole.SubscriberInput"/>:
/// a value the subscriber edited, applied to the stream.</description></item>
/// <item><term><see cref="DataChangeRequest"/></term><description><see cref="StreamDeliveryRole.SubscriberInput"/>:
/// a data write handed to the workspace. Writes from a view are addressed to the stream's owner,
/// not to the stream, so the stream receives one only as input.</description></item>
/// <item><term><see cref="DataChangedEvent"/></term><description><see cref="StreamDeliveryRole.SubscriberInput"/>:
/// a frame applied as the stream's state. Frames flow from an owner to its mirrors; a stream
/// produced for a subscriber has no upstream sending it frames, so any it receives is input.</description></item>
/// <item><term><see cref="StreamErrorEvent"/></term><description><see cref="StreamDeliveryRole.SubscriberInput"/>:
/// ends the stream in error. Like a frame it flows from an owner to its mirrors, so on a stream
/// produced for a subscriber it is input.</description></item>
/// <item><term><c>SynchronizationStream&lt;T&gt;.UpdateStreamRequest</c>, <c>SetCurrentRequest</c></term>
/// <description><see cref="StreamDeliveryRole.OwnWrite"/>: the stream's own write path
/// (<c>Update</c>, <c>OnNext</c>), posted by the stream to its own hub under the identity of
/// whoever wrote, which is often the platform identity rendering the area — so the rule for these
/// is the posting hub, not the identity. Both are also <see cref="InfrastructureOnlyAttribute"/>.</description></item>
/// <item><term><see cref="UnsubscribeRequest"/></term><description><see cref="StreamDeliveryRole.ReleaseOrAnswer"/>:
/// ends the subscription. It is posted by the subscribing hub while that hub is tearing down, as a
/// <see cref="SystemMessageAttribute"/> message that need not carry the subscriber's identity, and
/// it is the only thing that releases the owner's per-subscriber stream — refusing the mesh's own
/// release would keep that stream's hub alive for the life of the process.</description></item>
/// <item><term><see cref="GetDataResponse"/>, <see cref="DeliveryFailure"/></term>
/// <description><see cref="StreamDeliveryRole.ReleaseOrAnswer"/>: answers to deliveries this hub
/// sent, issued by whichever hub answers under that hub's own identity — including the refusals
/// this rule itself posts. They are never the subscriber's, so they cannot be held to its
/// identity.</description></item>
/// </list>
/// </summary>
internal static class StreamInputRule
{
    /// <summary>
    /// The classification. A key is matched by assignability, or — for the stream's nested
    /// generic messages — by generic type definition.
    /// </summary>
    internal static readonly ImmutableArray<(Type Type, StreamDeliveryRole Role)> Roles =
    [
        (typeof(IUserAction), StreamDeliveryRole.SubscriberInput),
        (typeof(PatchDataChangeRequest), StreamDeliveryRole.SubscriberInput),
        (typeof(DataChangeRequest), StreamDeliveryRole.SubscriberInput),
        (typeof(DataChangedEvent), StreamDeliveryRole.SubscriberInput),
        (typeof(StreamErrorEvent), StreamDeliveryRole.SubscriberInput),
        (typeof(SynchronizationStream<>.UpdateStreamRequest), StreamDeliveryRole.OwnWrite),
        (typeof(SynchronizationStream<>.SetCurrentRequest), StreamDeliveryRole.OwnWrite),
        (typeof(UnsubscribeRequest), StreamDeliveryRole.ReleaseOrAnswer),
        (typeof(GetDataResponse), StreamDeliveryRole.ReleaseOrAnswer),
        (typeof(DeliveryFailure), StreamDeliveryRole.ReleaseOrAnswer),
    ];

    /// <summary>The role of <paramref name="messageType"/>, or <c>null</c> when it is not classified.</summary>
    /// <param name="messageType">The CLR type of a delivered message.</param>
    /// <returns>The role, or <c>null</c>.</returns>
    internal static StreamDeliveryRole? RoleOf(Type messageType)
    {
        var definition = messageType.IsGenericType ? messageType.GetGenericTypeDefinition() : null;
        // A type nested in a generic type is itself generic (it carries the outer type argument).
        foreach (var (type, role) in Roles)
        {
            if (type.IsGenericTypeDefinition)
            {
                if (definition == type)
                    return role;
            }
            else if (type.IsAssignableFrom(messageType))
                return role;
        }
        return null;
    }
}
