using System.Reflection;

namespace MeshWeaver.Messaging;

/// <summary>
/// The contract between an INGRESS — a transport that injects a remote participant's deliveries
/// into the mesh (the SignalR and gRPC connection endpoints, public and trusted) — and the hubs
/// that receive those deliveries.
///
/// <para>An ingress calls <see cref="FromParticipant"/> on every delivery it accepts, after it
/// has read the client's envelope and before it hands the delivery to the mesh. That stamps the
/// validated identity and <see cref="Property"/>. The receiving hub refuses a stamped delivery
/// whose message type is <see cref="InfrastructureOnlyAttribute"/>, with a
/// <see cref="ErrorType.Forbidden"/> failure the client receives as its answer.</para>
///
/// <para>🚨 The refusal is made at the RECEIVING hub, once the message is typed, and not at the
/// ingress: a message type the ingress hub has not registered crosses the mesh as raw JSON, so an
/// ingress-side type check alone would wave through exactly the messages it cannot name. The
/// ingress may refuse early when the type is already resolved (<see cref="Refuses(IMessageDelivery)"/>),
/// which saves the routing, but the hub-side check is the one that holds.</para>
/// </summary>
public static class ParticipantIngress
{
    /// <summary>
    /// Delivery property an ingress stamps on every delivery it accepts from a participant. Its
    /// value names the ingress (for logs); only its PRESENCE is decisive. Properties travel with
    /// the delivery across hubs and silos, so the stamp reaches the target.
    /// </summary>
    public const string Property = "ParticipantIngress";

    /// <summary>
    /// Prepares a participant's delivery for injection: stamps the connection's validated
    /// <paramref name="accessContext"/> (the client's claimed one is never trusted) and the
    /// ingress <see cref="Property"/> naming <paramref name="ingress"/>.
    /// </summary>
    /// <param name="delivery">The delivery as the participant sent it.</param>
    /// <param name="accessContext">The identity the ingress validated for this connection.</param>
    /// <param name="ingress">A short name of the ingress, e.g. <c>signalr</c> or <c>grpc-trusted</c>.</param>
    /// <returns>The delivery to hand to the mesh.</returns>
    public static IMessageDelivery FromParticipant(
        this IMessageDelivery delivery, AccessContext accessContext, string ingress)
        => delivery
            .SetAccessContext(accessContext)
            .SetProperty(Property, string.IsNullOrEmpty(ingress) ? "participant" : ingress);

    /// <summary>Whether <paramref name="delivery"/> entered the mesh through a participant ingress.</summary>
    /// <param name="delivery">The delivery to inspect.</param>
    /// <returns><c>true</c> when the ingress stamp is present.</returns>
    public static bool IsFromParticipant(this IMessageDelivery delivery)
        => delivery.Properties.ContainsKey(Property);

    /// <summary>Whether a participant may post a message of <paramref name="messageType"/>.</summary>
    /// <param name="messageType">The CLR type of the message.</param>
    /// <returns><c>false</c> for a type carrying <see cref="InfrastructureOnlyAttribute"/>.</returns>
    public static bool IsParticipantPostable(Type messageType)
        => !messageType.IsDefined(typeof(InfrastructureOnlyAttribute), inherit: true);

    /// <summary>
    /// The refusal for a participant's delivery of an infrastructure-only message, or <c>null</c>
    /// when the delivery may proceed — because it did not come from a participant, because its
    /// message is not yet typed (raw JSON is judged by the receiving hub once it is), or because
    /// the type is participant-postable.
    /// </summary>
    /// <param name="delivery">The delivery to judge.</param>
    /// <returns>The reason to refuse, or <c>null</c>.</returns>
    public static string? Refuses(IMessageDelivery delivery)
    {
        if (!delivery.IsFromParticipant())
            return null;
        var type = delivery.Message?.GetType();
        if (type is null || type == typeof(RawJson) || IsParticipantPostable(type))
            return null;
        return $"'{type.Name}' is mesh infrastructure and is never accepted from a participant connection "
               + $"(ingress '{delivery.Properties[Property]}', sender '{delivery.Sender}', target '{delivery.Target}'). "
               + "Use the checked request for the operation instead — e.g. CreateOrUpdateNodeRequest or "
               + "GetMeshNodeStream(path).Update for a node write.";
    }
}
